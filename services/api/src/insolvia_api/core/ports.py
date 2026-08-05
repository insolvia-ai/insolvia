from __future__ import annotations

from typing import Any, Protocol

from insolvia_api.core.access import Accessor
from insolvia_api.core.access_log import AccessEvent
from insolvia_api.core.cases import Case, CaseAssignment, CasePage
from insolvia_api.core.documents import Document
from insolvia_api.core.firms import Firm, FirmUser
from insolvia_api.core.mail import OutboundEmail
from insolvia_api.core.waitlist import WaitlistRecord


class JwksProvider(Protocol):
    """Supplies the public key a JWT's `kid` names (issue #79).

    This port exists so `core/auth.py` can stay pure. Verification needs a
    key; fetching one needs the network, and `core` may not have it. The real
    implementation (adapters/aws/jwks_provider.py) reads the Cognito pool's
    `<issuer>/.well-known/jwks.json` over stdlib urllib and caches by `kid`;
    the static one (adapters/memory/jwks_provider.py) is handed keys directly
    and is what the tests sign against.

    Implementations MUST raise `insolvia_api.core.auth.AuthenticationError`
    with `AuthFailureReason.UNKNOWN_KEY` for a `kid` they cannot resolve —
    including after a refresh. Returning None or a placeholder would push a
    "no key" case into the verifier, where the safe branch is easy to miss.
    """

    def signing_key(self, kid: str) -> Any:
        """The key for `kid`, in whatever form PyJWT's `decode` accepts."""
        ...


class WaitlistStore(Protocol):
    """Persists waitlist submissions. Implemented by adapters/aws (DynamoDB)
    and adapters/memory (tests and the plain development server)."""

    def add(self, record: WaitlistRecord) -> None: ...


class UserDirectory(Protocol):
    """Creates the Cognito account behind a new firm user.

    THE ONLY PORT IN THIS SERVICE THAT WRITES TO THE IDENTITY PROVIDER, and it
    has exactly one method for that reason. Self-signup is off
    (`allow_admin_create_user_only`), so somebody has to mint the pool user
    when a firm admin adds a colleague, and it cannot be the client.

    ONE METHOD, AND NOTHING THAT SETS A PASSWORD. The AWS implementation holds
    `cognito-idp:AdminCreateUser` and nothing else — no AdminSetUserPassword,
    no AdminInitiateAuth — so a compromised API can create accounts it still
    cannot authenticate as: the temporary password goes only to the invited
    address, in Cognito's own invitation email. That is a deliberately narrower
    grant than "provision a user" suggests, and it is what keeps this from
    being an impersonation primitive.

    Removing somebody from a firm is NOT here either. It deletes the membership
    row and leaves the pool account alone (see FirmStore.remove_user), so this
    port never needs delete — and the grant never needs AdminDeleteUser.
    """

    def create_user(self, email: str) -> str:
        """Create the account and return its subject (the Cognito `sub`).

        The subject is the value everything else keys on, so an implementation
        MUST return the one the provider assigned rather than minting its own —
        a made-up subject produces a firm user nobody can ever sign in as.

        MUST raise `insolvia_api.core.errors.ConflictError` when the address
        already has an account. Swallowing it and carrying on would attach a
        firm-user row to whatever subject a second create returned, which for
        Cognito is the EXISTING account — silently adding somebody else's
        user to this firm.
        """
        ...


class FirmStore(Protocol):
    """Firms, the people in them, and what each of them may do.

    THE HOTTEST READ IN THE SERVICE. `find_user` runs before any case is
    touched, on every authenticated request, because an access token carries a
    Cognito `sub` and nothing else authorization-bearing — no groups, no custom
    attributes, no pre-token Lambda. Resolving the firm IS the read.

    That is also why its IAM grant cannot be scoped to one tenant's partition
    (infra/modules/firm_store spells this out): there is nothing to scope by
    until the read has happened. Tenant isolation here is an APPLICATION
    property, exactly as ADR 0001 has it for case data.
    """

    def create_firm(self, firm: Firm) -> None:
        """Store a new firm. MUST refuse to overwrite an existing id."""
        ...

    def get_firm(self, firm_id: str) -> Firm | None: ...

    def add_user(self, user: FirmUser) -> None:
        """Attach someone to a firm. MUST refuse to overwrite an existing
        (firm_id, subject) rather than replacing it — an overwrite would reset
        a colleague's permissions to whatever the caller sent, and the caller
        believes they are adding a new person."""
        ...

    def get_user(self, firm_id: str, subject: str) -> FirmUser | None:
        """One user, by primary key. Firm-scoped, so an admin of firm A cannot
        read a user of firm B by knowing their subject."""
        ...

    def find_user(self, subject: str) -> FirmUser | None:
        """Which firm this Cognito subject belongs to, and what they may do.

        The by-subject index, and the ONLY method that is not firm-scoped —
        necessarily, since its whole job is to discover the firm.

        EVENTUALLY CONSISTENT, unavoidably: DynamoDB global secondary indexes
        do not support ConsistentRead. A user added a moment ago may not
        resolve yet, which is why the administration route reads its own write
        back by primary key (`get_user`) rather than through this.

        An implementation MUST raise rather than choose if a subject resolves
        to more than one firm. One person, one firm is an application
        invariant — nothing in the key schema enforces it, because DynamoDB
        conditions cannot span partitions — and picking a row would make
        someone's tenancy depend on index ordering. A loud 500 for one user
        beats a silent, unstable answer.
        """
        ...

    def list_users(self, firm_id: str) -> tuple[FirmUser, ...]:
        """A firm's whole staff list, ordered by display name then subject.

        All of them: a caller cannot page, so an implementation that can
        truncate must not. A firm is 2-15 seats (the business plan's own
        sizing), so this is a small query by construction — and if a firm ever
        outgrows it, the fix is a cursor, not a silent cap.
        """
        ...

    def update_user(self, user: FirmUser) -> FirmUser | None:
        """Write `user` back, but only over a row that still exists AND still
        belongs to `user.firm_id`.

        Returns None if either no longer holds — the administration route turns
        that into the same 404 a foreign subject gets. Both halves matter: the
        existence check stops a deleted user being resurrected by a PATCH that
        was in flight, and the firm check closes the window between the route's
        read and this write.
        """
        ...

    def remove_user(self, firm_id: str, subject: str) -> bool:
        """Detach someone from a firm. True if this call removed them, False if
        there was nothing there.

        Removes the membership row ONLY. The Cognito user survives, and so does
        every case assignment naming this subject — those live in the case
        table and are cleaned by the administration route, which is the one
        place that can see both. A store that reached across would be a second
        thing to keep in step.
        """
        ...


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

    def delete(self, storage_ref: str) -> None:
        """Delete the object. Idempotent — deleting an object that is not there
        succeeds, because the route reaches here only after removing the row
        that was the record of it."""
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
