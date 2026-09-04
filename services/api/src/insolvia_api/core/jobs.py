"""Async pipeline jobs: the record, its transitions, and the queue contract.

The job model behind ADR 0018 (issue #271 / 9.10): work that cannot finish
inside a request — packet assembly (9.6), AI review (9.7), extraction
(8.7-8.9) — is accepted by the API as a *job*, executed by the worker Lambda
beside it, and read back as status. This module owns everything both sides
must agree on, so the API and the worker cannot drift apart:

- the record and its stored item shape (a child item of the case partition,
  like debtors and the generic collections — no second table);
- the status transitions, written with conditional updates because SQS is
  at-least-once and two deliveries of one message must not both "win";
- the queue message envelope — the contract-pinned seam between the enqueue
  adapter and the worker entrypoint. It carries IDENTIFIERS ONLY: workers
  re-read everything else from the store, which is what makes a retry safe
  and keeps GLBA-scope case data out of SQS (the queue is not encrypted
  under the case key; a body that never contains case data is what makes
  that acceptable);
- the dispatch loop itself (`run_job` / `handle_sqs_event`), pure enough to
  run under pytest with the in-memory store — the local-testability rule
  ADR 0015 extends to pipelines.

Everything here is pure: no Flask, no boto3, no clock beyond datetime.now.
"""

from __future__ import annotations

import json
import logging
import uuid
from collections.abc import Callable, Mapping
from dataclasses import dataclass, replace
from datetime import UTC, datetime
from typing import TYPE_CHECKING, Any

from insolvia_core.errors import FieldValidationError, ValidationError

if TYPE_CHECKING:
    from insolvia_api.core.ports import JobStore

logger = logging.getLogger(__name__)

# Lifecycle. `queued` is written by the accept endpoint before the message is
# sent; the worker moves it forward. `failed` is terminal for a deterministic
# failure but NOT a dead end for an infrastructure one: a redelivered message
# may re-run a failed job (see `start_attempt`), so a crash that SQS retries
# can still end `succeeded`. Only `succeeded` is immutable.
STATUSES = ("queued", "running", "succeeded", "failed")

# The statuses the accept endpoint treats as "this work is already in flight".
ACTIVE_STATUSES = ("queued", "running")

# The wire envelope's version. Bumped only with a consumer that understands
# both — the worker rejects a version it does not know rather than guessing.
MESSAGE_VERSION = 1


class JobError(Exception):
    """A worker's *expected* failure, with a message safe to show the preparer.

    Raising this marks the job failed and does NOT re-raise into SQS retry:
    it means the failure is deterministic — running again produces the same
    answer — so retrying would only delay the person waiting on it. Anything
    else a worker raises is treated as infrastructure, marked failed with a
    generic message, and re-raised so SQS's redelivery gives it another
    attempt.
    """

    def __init__(self, message: str, *, category: str = "job_error") -> None:
        super().__init__(message)
        self.category = category


@dataclass(frozen=True)
class JobFailure:
    """What the preparer is told. `message` must be safe to render — worker
    exceptions' text stays in CloudWatch, never here (GLBA: a stack trace
    over case data is case data)."""

    category: str
    message: str


@dataclass(frozen=True)
class Job:
    """A job record — a child item of its case's partition.

    A job is reached only through its case, exactly as debtors and documents
    are: the routes resolve the case through `CaseStore` first on every path,
    so ownership is not re-derived here. `created_by` is the firm user who
    accepted the job — an audit fact, mirrored into the access log at accept.
    """

    id: str
    case_id: str
    kind: str
    status: str
    created_by: str
    created_at: str
    updated_at: str
    attempts: int = 0
    failure: JobFailure | None = None
    result: dict[str, Any] | None = None


@dataclass(frozen=True)
class JobMessage:
    """The decoded queue message: identifiers only, never case data."""

    job_id: str
    case_id: str
    kind: str


def _timestamp() -> str:
    """Millisecond UTC with a literal Z — the access log's format. Jobs sort
    in `list_order` by (created_at, id), a tie-break not a guarantee, and
    nothing keys on this string."""
    return datetime.now(UTC).isoformat(timespec="milliseconds").replace("+00:00", "Z")


# ── Workers ─────────────────────────────────────────────────────
# A worker is a plain Python callable: Job in, JSON-shaped result out. That
# sentence is the local story (ADR 0018) — tests call these, and `run_job`
# below, directly; no Lambda, no queue, no emulator.
#
# 9.6 (packet assembly) and 9.7 (AI review) land as entries here. Their heavy
# dependencies belong in the WORKER image (services/api/Dockerfile, `worker`
# target), never the API's — ADR 0015's rule.


def run_echo(job: Job) -> dict[str, Any]:
    """The walking skeleton's worker: proves accept → deliver → run → status
    end to end in every environment, and is the smoke-test target the deploy
    workflows invoke. It deliberately reads no case data."""
    return {"echo": job.id}


WORKERS: dict[str, Callable[[Job], dict[str, Any]]] = {
    "echo": run_echo,
}

# What the accept endpoint admits. WORKERS above holds only the DEPENDENCY-
# FREE workers; workers that read stores (packet assembly — issue #96,
# core/packet_assembly.py — and the AI petition review — issue #97,
# core/petition_review.py) are composed by the worker entrypoints, which
# build the full mapping around WORKERS with the adapters in hand. Their
# KINDS still live here, because the accept endpoint must admit them without
# importing the workers' dependencies — and `run_job`'s unknown-kind branch
# already covers a registry that lacks a kind this tuple admits (the same
# deploy-window shape its comment describes).
KINDS = (*WORKERS, "packet_assembly", "petition_review")


# ── Validation and identity ─────────────────────────────────────


def parse_job_acceptance(payload: Mapping[str, object]) -> str:
    """Validate POST /v1/cases/<id>/jobs. Unknown keys are ignored.

    Only `kind` is accepted. There is deliberately no client-supplied payload
    in v1: workers read everything from the store, so a payload would be a
    second, unvalidated path for case data to arrive by.
    """
    kind = payload.get("kind")
    if not isinstance(kind, str) or kind not in KINDS:
        raise FieldValidationError(
            {"kind": "Kind must be one of " + ", ".join(KINDS) + "."}
        )
    return kind


def new_job(kind: str, *, case_id: str, created_by: str) -> Job:
    """A freshly accepted job. Both scoping fields come from the caller's
    resolved accessor and the route's case lookup, never the request body."""
    now = _timestamp()
    return Job(
        id=str(uuid.uuid4()),
        case_id=case_id,
        kind=kind,
        status="queued",
        created_by=created_by,
        created_at=now,
        updated_at=now,
    )


def find_active(jobs: tuple[Job, ...], kind: str) -> Job | None:
    """The queued-or-running job of this kind, if any — the accept endpoint's
    idempotency rule. One active job per (case, kind): a client that cannot
    tell whether its first request landed re-POSTs and gets the same job back
    rather than a duplicate pipeline run."""
    for job in jobs:
        if job.kind == kind and job.status in ACTIVE_STATUSES:
            return job
    return None


# ── Transitions ─────────────────────────────────────────────────
# Each returns a new Job; the store's conditional `update` (expected_status)
# is what makes them safe under at-least-once delivery. Only `succeeded` is
# immutable: a `running` job may be re-started (a crashed attempt leaves the
# record saying running while SQS redelivers), and a `failed` one may too (an
# infrastructure failure that a later redelivery gets right).


def start_attempt(job: Job) -> Job:
    if job.status == "succeeded":
        raise ValidationError("a succeeded job cannot be re-run")
    return replace(
        job, status="running", attempts=job.attempts + 1, updated_at=_timestamp()
    )


def complete(job: Job, result: dict[str, Any]) -> Job:
    return replace(
        job, status="succeeded", result=result, failure=None, updated_at=_timestamp()
    )


def fail(job: Job, failure: JobFailure) -> Job:
    return replace(job, status="failed", failure=failure, updated_at=_timestamp())


# ── Stored item shape ───────────────────────────────────────────


def sort_key(job_id: str) -> str:
    return f"JOB#{job_id}"


def list_order(job: Job) -> tuple[str, str]:
    """Creation order; the SK embeds a random uuid, so neither store gets
    this ordering for free (the same note every sibling collection carries)."""
    return (job.created_at, job.id)


def job_item(job: Job) -> dict[str, Any]:
    """The exact stored item shape, shared by both JobStore implementations.

    PK  CASE#<case_id>     the case's own partition — a job is a child item,
    SK  JOB#<job_id>       like DEBTOR#/DOCUMENT#, so there is no second
                           table, no new key, and the existing table grant
                           shape covers it.

    `failure` and `result` are stored only when present, so the common row
    stays small and `job_from_item` can treat absence as None.
    """
    item: dict[str, Any] = {
        "PK": f"CASE#{job.case_id}",
        "SK": sort_key(job.id),
        "id": job.id,
        "caseId": job.case_id,
        "kind": job.kind,
        "status": job.status,
        "createdBy": job.created_by,
        "attempts": job.attempts,
        "createdAt": job.created_at,
        "updatedAt": job.updated_at,
    }
    if job.failure is not None:
        item["failure"] = {
            "category": job.failure.category,
            "message": job.failure.message,
        }
    if job.result is not None:
        item["result"] = job.result
    return item


def job_from_item(item: Mapping[str, Any]) -> Job:
    """Inverse of job_item. Raises ValidationError on a row this service did
    not write — loud beats a half-populated Job reaching a caller."""
    try:
        raw_failure = item.get("failure")
        failure = (
            JobFailure(
                category=str(raw_failure["category"]),
                message=str(raw_failure["message"]),
            )
            if isinstance(raw_failure, Mapping)
            else None
        )
        raw_result = item.get("result")
        result = dict(raw_result) if isinstance(raw_result, Mapping) else None
        return Job(
            id=str(item["id"]),
            case_id=str(item["caseId"]),
            kind=str(item["kind"]),
            status=str(item["status"]),
            created_by=str(item["createdBy"]),
            created_at=str(item["createdAt"]),
            updated_at=str(item["updatedAt"]),
            attempts=int(item["attempts"]),
            failure=failure,
            result=result,
        )
    except (KeyError, ValueError) as error:
        raise ValidationError(f"stored job item is malformed: {error}") from error


def job_json(job: Job) -> dict[str, object]:
    """The API representation. `caseId` is absent — the client named the case
    in the URL — and `createdBy` is present for the same reason a case's is:
    the firm directory resolves it to a name. `failure`/`result` appear only
    when set, so a client distinguishes "no result yet" from "empty result".
    """
    body: dict[str, object] = {
        "id": job.id,
        "kind": job.kind,
        "status": job.status,
        "createdBy": job.created_by,
        "attempts": job.attempts,
        "createdAt": job.created_at,
        "updatedAt": job.updated_at,
    }
    if job.failure is not None:
        body["failure"] = {
            "category": job.failure.category,
            "message": job.failure.message,
        }
    if job.result is not None:
        body["result"] = job.result
    return body


# ── The queue contract ──────────────────────────────────────────
# The one seam that cannot run under pytest end to end is SQS delivering into
# Lambda. Both sides of it meet HERE: SqsJobQueue serializes with
# `job_message`, the worker entrypoint parses with `parse_job_message`, and
# tests/test_jobs.py pins the exact wire shape — so producer and consumer
# cannot drift without a red test. That pin is the contract ADR 0018 requires.


def job_message(job: Job) -> dict[str, object]:
    """The exact message body. Identifiers only — see the module docstring."""
    return {
        "version": MESSAGE_VERSION,
        "jobId": job.id,
        "caseId": job.case_id,
        "kind": job.kind,
    }


def parse_job_message(payload: object) -> JobMessage:
    """Inverse of job_message, rejecting anything this service did not mint.

    The body is validated rather than trusted even though only our own API
    writes to the queue: a version bump, a cross-environment message, or a
    hand-injected test message should fail loudly here, not deep in a worker.
    """
    if not isinstance(payload, Mapping) or payload.get("version") != MESSAGE_VERSION:
        raise ValidationError("job message is not valid")
    job_id = payload.get("jobId")
    case_id = payload.get("caseId")
    kind = payload.get("kind")
    if (
        not isinstance(job_id, str)
        or not isinstance(case_id, str)
        or not isinstance(kind, str)
    ):
        raise ValidationError("job message is not valid")
    return JobMessage(job_id=job_id, case_id=case_id, kind=kind)


# ── The dispatch loop ───────────────────────────────────────────


def run_job(
    message: JobMessage,
    *,
    store: JobStore,
    workers: Mapping[str, Callable[[Job], dict[str, Any]]],
) -> None:
    """Execute one delivered message. At-least-once safe by construction:

    - the job record is the truth, and it was written BEFORE the message was
      sent, so "no record" means the message is stray (cross-environment, or
      a record deleted out of band) — logged and dropped, because retrying a
      message that can never resolve only poisons the queue;
    - a `succeeded` job is a finished job; a redelivery is a no-op;
    - the queued→running transition is conditional on the status this read
      observed, so two concurrent deliveries of one message race for a single
      winner and the loser returns without running anything.

    Failure has two shapes, split by what a retry could change. A `JobError`
    is deterministic: the job is marked failed with the worker's
    preparer-safe message and the exception is swallowed — the message is
    consumed, no retry. Anything else is infrastructure: the job is marked
    failed with a generic message and the exception RE-RAISED, so SQS
    redelivers (start_attempt allows failed→running) and, after
    maxReceiveCount, parks the message on the DLQ whose depth alarms. The
    preparer sees `failed` with a reason either way — failure surfaces
    through the status read, never a support ticket.
    """
    job = store.get(message.case_id, message.job_id)
    if job is None:
        logger.error(
            "job message has no record; dropping",
            extra={"job_id": message.job_id, "case_id": message.case_id},
        )
        return
    if job.status == "succeeded":
        return

    worker = workers.get(job.kind)
    if worker is None:
        # Reachable in the window where the API accepts a new kind before the
        # worker image carrying it has deployed. Failed-then-raise means the
        # preparer sees an honest status now, and the redelivery after the
        # worker deploy completes can still pick it up and succeed.
        store.update(
            fail(
                job,
                JobFailure(
                    category="unknown_kind",
                    message="This job type is not available yet. It will be retried.",
                ),
            ),
            expected_status=job.status,
        )
        raise ValidationError(f"no worker registered for kind {job.kind!r}")

    started = store.update(start_attempt(job), expected_status=job.status)
    if started is None:
        # A concurrent delivery won the conditional write; nothing to do.
        logger.info("job attempt lost the start race", extra={"job_id": job.id})
        return

    try:
        result = worker(started)
    except JobError as error:
        store.update(
            fail(started, JobFailure(category=error.category, message=str(error))),
            expected_status="running",
        )
        logger.info(
            "job failed deterministically",
            extra={"job_id": job.id, "category": error.category},
        )
        return
    except Exception:
        # The exception's text stays in CloudWatch; the preparer-facing
        # message is generic on purpose (GLBA — a stack trace over case data
        # is case data).
        store.update(
            fail(
                started,
                JobFailure(
                    category="internal",
                    message="The job failed unexpectedly and will be retried.",
                ),
            ),
            expected_status="running",
        )
        logger.exception("job failed unexpectedly", extra={"job_id": job.id})
        raise
    store.update(complete(started, result), expected_status="running")
    logger.info("job succeeded", extra={"job_id": job.id, "attempts": started.attempts})


def handle_sqs_event(
    event: Mapping[str, Any],
    *,
    store: JobStore,
    workers: Mapping[str, Callable[[Job], dict[str, Any]]],
) -> None:
    """One SQS→Lambda event. The event source mapping delivers batch size 1
    (infra/modules/job_pipeline), so the loop is a formality — but it holds
    for any batch, raising on the first failing record so the whole delivery
    retries; with one record per batch that is exact, not approximate.

    A body that does not parse raises: it will never parse, so retries walk
    it to the DLQ, which is where a message this service cannot explain
    belongs — visible under an alarm, not silently dropped.

    The local worker poller (entrypoints/worker_poller.py) feeds this same
    function, so a laptop runs the exact consume path the Lambda runs.
    """
    for record in event.get("Records", []):
        message = parse_job_message(json.loads(record["body"]))
        run_job(message, store=store, workers=workers)
