"""The ports only this service composes.

The firm domain's ports — FirmStore, UserDirectory, JwksProvider — live in
`insolvia_core.ports` with the domain they serve (issue #208); everything here
is tenant-API-specific.
"""

from __future__ import annotations

from typing import Protocol

from insolvia_api.core.access import Accessor
from insolvia_api.core.access_log import AccessEvent
from insolvia_api.core.cases import Case, CaseAssignment, CasePage
from insolvia_api.core.debtors import Debtor
from insolvia_api.core.documents import Document, StoredBlob
from insolvia_api.core.mail import OutboundEmail
from insolvia_api.core.waitlist import WaitlistRecord


class WaitlistStore(Protocol):
    """Persists waitlist submissions. Implemented by adapters/aws (DynamoDB)
    and adapters/memory (tests and the plain development server)."""

    def add(self, record: WaitlistRecord) -> None: ...


class CaseStore(Protocol):
    """Persists case records (issue 8.3). Implemented by adapters/aws
    (DynamoDB) and adapters/memory (tests and the plain development server).

    Every read takes an `Accessor` explicitly and every implementation MUST
    apply `core/access.may_see_case` rather than trusting the route to have
    checked. The route does check, and this is still not belt-and-braces: it is
    the only thing standing between one firm's cases and another's, and a
    scoping rule that lives in exactly one place is one refactor away from not
    existing.

    `get` and `update` return None for "no such case", for "another firm's
    case", AND for "your firm's case that you are not linked to". The three
    must not be distinguishable from outside, because a route that could tell
    them apart is an oracle for other firms' case ids — and, worse, for which
    matters a colleague is working.
    """

    def create(self, case: Case, assignment: CaseAssignment) -> None:
        """Store a new case AND link its creator, atomically.

        BOTH OR NEITHER, and it must be a transaction rather than two writes.
        A case whose assignment write failed is invisible to the person who
        just created it — they cannot list it, cannot open it, and cannot tell
        it apart from the request having failed — while still occupying its id.
        core/cases.create_case returns the pair for exactly this reason.
        """
        ...

    def get(self, case_id: str, *, accessor: Accessor) -> Case | None:
        """One case, if this accessor may see it.

        Implementations read the case AND the caller's assignment row in ONE
        round trip — they share a partition — and hand both to `may_see_case`.
        Reading the case, deciding, and only then checking linkage would be two
        round trips on the hottest path in the service.
        """
        ...

    def list_for_accessor(
        self, accessor: Accessor, *, limit: int, cursor: str | None
    ) -> CasePage:
        """The cases this accessor may see, newest first.

        TWO DIFFERENT INDEXES depending on the caller — `by-firm` when
        `sees_every_case`, `by-assignee` otherwise — which is the one genuinely
        awkward consequence of this model and is why cursors carry the index
        they were minted against (core/cases.encode_cursor). An implementation
        MUST pass its index through to encode/decode so a permission change
        mid-pagination fails loudly instead of skipping rows.
        """
        ...

    def update(self, case: Case) -> Case | None:
        """Write `case` back, but only if it still belongs to `case.firm_id`.

        Returns None if that no longer holds — closing the window between the
        route's read and this write, so a case cannot move firms underneath a
        caller mid-request.

        NO ASSIGNMENT CHECK HERE, deliberately: the route has already resolved
        the case through `get`, which applied the whole rule. Re-deriving
        linkage in the write would be a second copy of an authorization
        decision, and two copies eventually disagree.
        """
        ...

    def assign(self, assignment: CaseAssignment) -> None:
        """Link someone to a case. Idempotent — re-linking somebody already on
        the matter must succeed rather than raising, because the firm-admin UI
        cannot tell whether its first request landed."""
        ...

    def unassign(self, case_id: str, subject: str) -> bool:
        """Unlink someone. True if this call removed the link, False if there
        was nothing there."""
        ...

    def assignees(self, case_id: str) -> tuple[CaseAssignment, ...]:
        """Everyone linked to this case, oldest link first.

        Case-scoped and takes no accessor: the one caller resolves the case
        through `get` first, on every path. Same rule DocumentStore states —
        two authorisation checks that must agree are two checks that will
        eventually disagree.
        """
        ...


class DocumentStore(Protocol):
    """Persists document METADATA rows (issue 8.6) — never the bytes.

    Case-scoped rather than owner-scoped, and unlike CaseStore it takes no
    owner and enforces nothing. That is not a weaker rule, it is the same rule
    in one place: api/routes/documents.py resolves the case through CaseStore
    first, on every path, and a route that skipped that lookup would read
    another firm's documents whatever this store did. Two authorisation checks
    that must agree are two checks that will eventually disagree.

    What every method DOES enforce is the case scope: `case_id` is half the
    key, so a document id from another case does not resolve here. That is what
    keeps a leaked or guessed document id useless without its case.
    """

    def create(self, document: Document) -> None:
        """Store a new record. MUST refuse to overwrite an existing
        (case_id, document_id) rather than replacing it — a document row is
        the only record that an object exists, and nothing in this feature
        ever legitimately rewrites one."""
        ...

    def get(self, case_id: str, document_id: str) -> Document | None: ...

    def update(self, document: Document) -> Document | None:
        """Write `document` back, but only over a row that still exists.

        Returns None if it does not — the confirm route turns that into the
        same 404 a foreign document id gets, so a document deleted between the
        route's read and this write is not silently resurrected as a `stored`
        row whose case no longer lists it.

        The one caller is confirmation. `create` still refuses to overwrite,
        and that is unchanged: this is not a general "write a document row"
        method, and a second caller should be a reason to re-read the note on
        `create` before adding one.
        """
        ...

    def list_for_case(self, case_id: str) -> tuple[Document, ...]:
        """Every document of one case, newest first (core/documents.list_order
        — the sort key is a random uuid, so neither implementation gets this
        ordering for free). All of them: a caller cannot page, so an
        implementation that can truncate must not."""
        ...

    def delete(self, case_id: str, document_id: str) -> bool:
        """Remove the record. True if this call removed it, False if there was
        nothing there — the route turns False into the same 404 a foreign
        document id gets, so two concurrent deletes cannot both report success
        and then both delete the object."""
        ...


class DocumentBlobStore(Protocol):
    """Mints the short-lived, single-object capabilities that move the bytes,
    and deletes objects. The one port in this service that reaches S3.

    Why the client touches S3 at all, given ADR 0001, is answered at length in
    api/routes/documents.py — read that before adding a method here. The
    constraint it puts on this port: every URL is minted server-side for ONE
    object, ONE verb, with the encryption header the bucket requires and a
    lifetime measured in minutes. Nothing here takes a bucket, a key prefix, or
    a method from a caller, because a method that did would be handing out
    authority over the store rather than a ticket to one object in it.
    """

    def upload_url(
        self, storage_ref: str, *, content_type: str, byte_size: int, expires_in: int
    ) -> str:
        """A URL that accepts exactly one PUT of exactly this object.

        `content_type` and `byte_size` are the values the server validated, and
        an implementation MUST bind them into the capability rather than merely
        remembering them: unbound, they are a suggestion the client is free to
        ignore, and the allowlist and the size cap stop being enforcement.
        """
        ...

    def download_url(self, storage_ref: str, *, expires_in: int) -> str:
        """A URL that serves exactly this object, once it exists."""
        ...

    def stat(self, storage_ref: str) -> StoredBlob | None:
        """What the store reports about this object, or None if it is not
        there.

        The ONLY way this service ever learns that an upload actually landed.
        Nothing else in the feature reads the object store — the case store is
        the record of what should be there — so an implementation that guessed
        here would be inventing the one fact the confirm route exists to
        establish.

        None means absent, and an implementation MUST NOT distinguish "no such
        object" from "not allowed to see it" by raising for one and returning
        None for the other: without s3:ListBucket, S3 itself answers 403 for a
        missing key, so the two are not separable at this boundary. See
        adapters/aws/document_blobs.py for what that costs.
        """
        ...

    def clear_upload_tag(self, storage_ref: str) -> None:
        """Remove the `upload=unconfirmed` tag, taking the object out of the
        bucket's expire-unconfirmed-uploads filter.

        THE ONLY THING THAT MAKES THAT LIFECYCLE RULE SAFE. Every presigned PUT
        writes the tag, and the rule deletes tagged objects after a day, so an
        object nothing calls this for is an object that will be reaped —
        correct for an abandoned upload, and data loss for a real document.
        Idempotent, because the confirm route is.
        """
        ...

    def delete(self, storage_ref: str) -> None:
        """Delete the object. Idempotent — deleting an object that is not there
        succeeds, because the route reaches here only after removing the row
        that was the record of it."""
        ...


class DebtorStore(Protocol):
    """Persists the debtor records of a case (issue 8.5).

    Ownership is NOT a parameter here, and that is the one thing to understand
    before implementing it. A debtor is reached only through its case, so the
    route resolves the case through `CaseStore` first — which enforces
    ownership and answers None for "not yours" — and a debtor call happens only
    after that succeeded. Threading `owner_principal` down here too would imply
    a second, independent authorisation path, and two paths that must agree are
    how they stop agreeing.

    `put` replaces the whole record for one filing role. See parse_debtor for
    why whole rather than partial: invariant 1 can only be checked against a
    complete record.
    """

    def create(self, debtor: Debtor) -> bool:
        """Write `debtor` ONLY if that case has no record for its role yet.
        Returns False when one already exists, having written nothing.

        Separate from `put` because the id has to be stable: the route hands
        a freshly minted id back to the client, and provenance paths
        elsewhere may already name it. Two overlapping first saves would
        otherwise both see nothing stored, both mint an id, and the second
        would erase the one the first already returned."""
        ...

    def put(self, debtor: Debtor) -> None:
        """Replace the record for `debtor`'s role outright."""
        ...

    def get(self, case_id: str, *, filing_role: str) -> Debtor | None: ...

    def list_for_case(self, case_id: str) -> tuple[Debtor, ...]:
        """Every debtor of one case, ordered by filing role so debtor_1 comes
        before debtor_2 — the order the forms print them in."""
        ...


class AccessLog(Protocol):
    """Append-only record of who read or changed which case.

    Write-only by design, on both sides of the boundary: this port has no read
    method, and the API role's IAM grant is PutItem alone. See
    core/access_log.py for why.
    """

    def record(self, event: AccessEvent) -> None: ...


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
