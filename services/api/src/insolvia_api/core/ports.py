"""The ports only this service composes.

The firm domain's ports — FirmStore, UserDirectory, JwksProvider — live in
`insolvia_core.ports` with the domain they serve (issue #208), and the
case-domain ports — CaseStore, DocumentStore, DocumentBlobStore, DebtorStore,
CaseEntityStore, AccessLog — followed when the MCP service became their second
composer (ADR 0016, issue #262); everything here is tenant-API-specific.
"""

from __future__ import annotations

from typing import Protocol

from insolvia_core.cases import Case

from insolvia_api.core.extraction import ExtractionModelResult, ExtractionRequest
from insolvia_api.core.jobs import Job
from insolvia_api.core.mail import OutboundEmail
from insolvia_api.core.packets import Packet
from insolvia_api.core.petition_review import ReviewModelResult
from insolvia_api.core.waitlist import WaitlistRecord


class WaitlistStore(Protocol):
    """Persists waitlist submissions. Implemented by adapters/aws (DynamoDB)
    and adapters/memory (tests and the plain development server)."""

    def add(self, record: WaitlistRecord) -> None: ...


class JobStore(Protocol):
    """Persists pipeline job records (ADR 0018, issue #271).

    Same table, same partition as every other case child item — a job is
    SK=JOB#<id> under its case — and the same authorisation rule DebtorStore
    and DocumentStore state: a job is reached only through its case, the
    route resolves the case through `CaseStore` first on every path, and a
    second ownership check here would eventually disagree with the first.
    `case_id` is half the key, so a job id from another case does not resolve.

    Written from BOTH sides of the pipeline: the API creates rows and reads
    them back for status; the worker Lambda advances them. The conditional
    `update` is what keeps those two writers — and SQS's at-least-once
    redelivery — from trampling each other.
    """

    def create(self, job: Job) -> None:
        """Store a new record. Ids are server-minted uuid4s, so an existing
        (case, id) means the minting is broken — implementations MUST raise
        rather than silently replace, exactly as CaseEntityStore.create
        does."""
        ...

    def get(self, case_id: str, job_id: str) -> Job | None: ...

    def list_for_case(self, case_id: str) -> tuple[Job, ...]:
        """Every job of one case, in creation order (core/jobs.list_order —
        the sort key embeds a random uuid, so neither implementation gets
        the ordering for free). All of them: the accept endpoint's
        one-active-job-per-kind check reads this, and a truncated answer
        would let a duplicate pipeline run through."""
        ...

    def update(self, job: Job, *, expected_status: str) -> Job | None:
        """Write `job` back, but only if the stored status is still
        `expected_status` — the compare-and-swap every transition in
        core/jobs.py rides on. None means the condition failed (or the row
        is gone): the caller lost a race with a concurrent delivery and must
        not pretend otherwise. See run_job for why that is the entire
        at-least-once story."""
        ...


class PacketStore(Protocol):
    """Persists assembled-packet records (issue #96) — and writes the case's
    effective-dating pins in the same operation, which is the reason this is
    its own port rather than three calls on the others.

    Same table, same partition, same reached-only-through-its-case rule as
    every sibling: a packet is SK=PACKET#<id> under its case, `case_id` is
    half the key, and the routes resolve the case through `CaseStore` first.

    Written by the pipeline WORKER (create), read by the API (get/list).
    """

    def create(
        self, packet: Packet, *, pinned_case: Case, expected_updated_at: str
    ) -> bool:
        """Store the packet record AND the pinned case, atomically — both or
        neither, exactly as CaseStore.create pairs the case with its
        assignment. A packet without its pins (or pins without their packet)
        makes "what data did this filing use" unanswerable, which is the
        provenance failure effective-dating.md exists to prevent.

        The case write is conditional on the stored `updatedAt` still being
        `expected_updated_at` (the value the worker READ before assembling)
        and on the status not being `filed`. False means the condition failed
        — the case was edited, filed, or deleted mid-assembly — and the
        caller must treat the packet as describing a case that no longer
        exists; nothing was written.

        The packet put itself refuses to overwrite an existing (case, id),
        the id-minting rule every sibling create states.
        """
        ...

    def get(self, case_id: str, packet_id: str) -> Packet | None: ...

    def list_for_case(self, case_id: str) -> tuple[Packet, ...]:
        """Every packet of one case, newest first (core/packets.list_order
        reversed — the SK is a random uuid, so neither implementation gets
        the ordering for free). All of them: a caller cannot page."""
        ...


class ReviewModel(Protocol):
    """Runs the AI petition review's model call (issue #97, ADR 0019).

    The one seam between the review worker and the Anthropic API. `document`
    is core/petition_review.review_document's output — already scrubbed,
    already deterministic — and the answer is the raw structured output plus
    the model that produced it; parse_findings validates it on the way back.
    Implemented by adapters/anthropic (the real call — worker image only, per
    ADR 0015's heavy-dependency rule) and adapters/memory (tests, and any
    laptop without a key).

    Failure contract, mirroring core/jobs.py's split: an implementation
    raises JobError for a failure a retry cannot change (a rejected key, the
    model declining the request) and lets anything transient (rate limits,
    5xx, network) propagate so SQS redelivery retries the job.
    """

    def review(self, document: str) -> ReviewModelResult: ...


class ExtractionModel(Protocol):
    """Runs one document-extraction model call (issues 8.7/8.8, under
    ADR 0019's posture — the same worker, key, and model seam as ReviewModel).

    The request is core/extraction.ExtractionRequest — the document's own
    bytes plus the fixed per-kind instruction and schema; the answer is the
    raw structured output plus the model that produced it, coerced and
    re-validated by core/extraction's per-kind parser on the way back.
    Implemented by adapters/anthropic (the real call — worker image only,
    per ADR 0015's heavy-dependency rule) and adapters/memory (tests, and
    any laptop without a key).

    Failure contract, mirroring ReviewModel's: an implementation raises
    JobError for a failure a retry cannot change (a rejected key, the model
    declining, an answer the schema cannot hold) and lets anything transient
    (rate limits, 5xx, network) propagate so SQS redelivery retries the job.
    """

    def extract(self, request: ExtractionRequest) -> ExtractionModelResult: ...


class JobQueue(Protocol):
    """Hands an accepted job to the pipeline (ADR 0018).

    The orchestration seam. One method on purpose: the message body is not a
    parameter — implementations serialize with core/jobs.job_message, the one
    owner of the wire shape, which is what the contract test pins. Implemented
    by adapters/aws (SQS) and adapters/memory (tests and the plain
    development server, which record the enqueue rather than running the job
    — locally, jobs run through entrypoints/worker_poller.py against this
    machine's real dev queue).
    """

    def enqueue(self, job: Job) -> None: ...


class Mailer(Protocol):
    """Sends transactional mail through the mailer service (issue 6.4).

    Implemented by adapters/aws/mailer_client.py's SigV4MailerClient
    (production) and adapters/memory/mailer_client.py's InMemoryMailerClient
    (tests and the plain development server).
    """

    def send(self, email: OutboundEmail, *, idempotency_key: str) -> None:
        """Send `email`. `idempotency_key` becomes the mailer contract's
        `application_message_id` — callers supply a stable key so retries of
        the same logical send (e.g. a Lambda retry) dedupe on the mailer
        side rather than emailing the recipient twice."""
        ...

    def suppress(self, address: str, *, reason: str) -> None:
        """Stop sending to `address` (issue #80).

        Writes to the mailer's suppression store — the same one the SES
        feedback path fills from bounces and complaints, and the one the
        sender checks before every send. Idempotent: suppressing an already
        suppressed address succeeds.

        This port takes no proof of ownership, and neither does the mailer
        endpoint behind it. Establishing that the request came from the
        address's owner happens *before* this call, in the unsubscribe route,
        by verifying the HMAC token from the link (core/unsubscribe.py).
        Calling this without doing that would be a
        suppress-anyone-you-like button.
        """
        ...
