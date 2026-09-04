"""The ports of the shared domain — implemented in `insolvia_core.adapters`.

The first three protocols moved here from `insolvia_api.core.ports` when the
firm domain became shared (issue #208): they are the seams both the tenant API
and the admin service compose, so they live with the domain they serve. The
case-domain ports followed for the same reason when the MCP service became
their second composer (ADR 0016, issue #262). Ports that only one service
composes stay in that service.
"""

from __future__ import annotations

from typing import Any, Protocol, TypeVar

from insolvia_core.access import Accessor
from insolvia_core.access_log import AccessEvent
from insolvia_core.candidates import Candidate
from insolvia_core.case_entities import CaseEntity, EntityKind
from insolvia_core.cases import Case, CaseAssignment, CasePage
from insolvia_core.debtors import Debtor
from insolvia_core.documents import Document, StoredBlob
from insolvia_core.firms import Firm, FirmUser

BodyT = TypeVar("BodyT")


class JwksProvider(Protocol):
    """Supplies the public key a JWT's `kid` names (issue #79).

    This port exists so `insolvia_core/auth.py` can stay pure. Verification
    needs a key; fetching one needs the network, and the domain may not have
    it. The real implementation (adapters/aws/jwks_provider.py) reads the
    Cognito pool's `<issuer>/.well-known/jwks.json` over stdlib urllib and
    caches by `kid`; the static one (adapters/memory/jwks_provider.py) is
    handed keys directly and is what the tests sign against.

    Implementations MUST raise `insolvia_core.auth.AuthenticationError`
    with `AuthFailureReason.UNKNOWN_KEY` for a `kid` they cannot resolve —
    including after a refresh. Returning None or a placeholder would push a
    "no key" case into the verifier, where the safe branch is easy to miss.
    """

    def signing_key(self, kid: str) -> Any:
        """The key for `kid`, in whatever form PyJWT's `decode` accepts."""
        ...


class UserDirectory(Protocol):
    """Creates — and re-invites — the Cognito account behind a firm user.

    THE ONLY PORT IN THIS PACKAGE THAT WRITES TO THE IDENTITY PROVIDER.
    Self-signup is off (`allow_admin_create_user_only`), so somebody has to
    mint the pool user when a firm admin adds a colleague or the admin
    service provisions a firm's first administrator, and it cannot be the
    client.

    TWO METHODS, ONE IAM ACTION, AND NOTHING THAT SETS A PASSWORD. Both map
    to `cognito-idp:AdminCreateUser` (resend is that call with
    MessageAction=RESEND) — no AdminSetUserPassword, no AdminInitiateAuth —
    so a compromised caller can create or re-invite accounts it still cannot
    authenticate as: the temporary password goes only to the invited address,
    in Cognito's own invitation email. That is a deliberately narrower grant
    than "provision a user" suggests, and it is what keeps this from being an
    impersonation primitive.

    Removing somebody from a firm is NOT here either. It deletes the membership
    row and leaves the pool account alone (see FirmStore.remove_user), so this
    port never needs delete — and the grant never needs AdminDeleteUser.
    """

    def create_user(self, email: str) -> str:
        """Create the account and return its subject (the Cognito `sub`).

        The subject is the value everything else keys on, so an implementation
        MUST return the one the provider assigned rather than minting its own —
        a made-up subject produces a firm user nobody can ever sign in as.

        MUST raise `insolvia_core.errors.ConflictError` when the address
        already has an account. Swallowing it and carrying on would attach a
        firm-user row to whatever subject a second create returned, which for
        Cognito is the EXISTING account — silently adding somebody else's
        user to this firm.
        """
        ...

    def resend_invite(self, email: str) -> None:
        """Re-send the invitation, with a fresh temporary password, to an
        account that has never completed first sign-in (issue #212).

        The admin service's answer to "the invite never arrived" — expired
        temporary password, sandbox-refused recipient, or a spam folder.

        MUST raise `insolvia_core.errors.NotFoundError` for an address with
        no account, and `ConflictError` for one that has already signed in —
        Cognito only re-invites FORCE_CHANGE_PASSWORD users, and an active
        user asking for a password is the forgot-password flow's job, not an
        operator's.
        """
        ...


class FirmStore(Protocol):
    """Firms, the people in them, and what each of them may do.

    THE HOTTEST READ IN THE TENANT API. `find_user` runs before any case is
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

    def list_firms(self) -> tuple[Firm, ...]:
        """Every firm, ordered by name then id — the admin surface's index
        view (#212), and deliberately the ONLY cross-tenant read of firm META.

        A SCAN, and priced as one on purpose: firm META items carry no GSI
        keys (that absence is what keeps the by-subject index sparse), so
        listing them either scans or grows a second index with a backfill
        migration and a new forget-the-key failure mode. At the business
        plan's scale — tens of firms — a scan is one page. Revisit at ~1,000
        firms; the fix then is an index AND a cursor, not a silent cap here.

        The tenant API must never call this, and its IAM enforces that: the
        API role's grant deliberately lacks dynamodb:Scan — only the admin
        service's role can execute it.
        """
        ...

    def update_firm(self, firm: Firm) -> Firm | None:
        """Write `firm` back, but only over a row that still exists.

        Returns None if it does not — the admin route turns that into a 404
        rather than resurrecting a deleted firm from a stale read. The
        firm-scope half of update_user's condition has no analogue here: the
        firm IS the scope.
        """
        ...

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

    def read_for_worker(self, case_id: str) -> Case | None:
        """The case record with NO accessor and NO access rule — the pipeline
        worker's read (ADR 0018), and ONLY the pipeline worker's.

        A worker holds no firm identity: its authority derives from the job
        record it is executing, whose accept already resolved the case
        through `get` under the preparer's accessor and logged the decision.
        Re-deriving that decision here would need an accessor the worker does
        not have, and inventing one would be a second authorisation path.

        MUST NOT be called from a route. A route always has a caller, a
        caller always has an accessor, and `get` is the only read a request
        path may use — this method existing does not change that rule, it is
        the one deliberate exception for the process that has no caller.
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

    def put_bytes(self, storage_ref: str, *, content: bytes, content_type: str) -> None:
        """Write an object this SERVICE produced — the packet worker's write
        (issue #96), and the one write path that is not a presigned PUT.

        Everything about the upload flow above exists because a CLIENT moves
        the bytes; here the worker holds them, so there is no capability to
        mint, no declared size to bind, and no `upload=unconfirmed` tag —
        the record is written in the same breath (PacketStore.create), so the
        reaper the tag feeds has nothing to reap. Implementations MUST write
        with the bucket's required encryption mode, exactly as a presigned
        PUT is forced to.
        """
        ...

    def get_bytes(self, storage_ref: str) -> bytes | None:
        """Read one object whole — the extraction worker's read (8.7/8.8),
        and the one read path that is not a presigned GET.

        Everything about the download flow above exists because a CLIENT
        fetches the bytes; here the WORKER must hold them, because the model
        call reads the document itself. Only the pipeline worker's role holds
        s3:GetObject on source documents (infra/modules/case_documents) — the
        API's read path stays the brokered presigned URL, so this method must
        never be reached from a request handler.

        None means absent, under `stat`'s own rule: without s3:ListBucket a
        missing key answers 403, so "not there" and "not allowed" are not
        separable at this boundary and an implementation MUST NOT raise for
        one and return None for the other.
        """
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


class CaseEntityStore(Protocol):
    """Persists the generic case collections (issue #249) — creditors, claims,
    assets, employments, income summaries, households, expenses, dependents,
    codebtors and SOFA entries. One port for all ten, because they are one
    shape: a uuid-keyed record in its case's partition
    (core/case_entities.py).

    Ownership is NOT a parameter here, the same rule DebtorStore and
    DocumentStore state: an entity is reached only through its case, the route
    resolves the case through `CaseStore` first on every path, and a second
    authorisation path here would eventually disagree with the first.

    What every method DOES enforce is the case scope: `case_id` is half the
    key, so an entity id from another case does not resolve here. That is what
    keeps a leaked or guessed id useless without its case.
    """

    def create(self, entity: CaseEntity[Any]) -> None:
        """Store a new record. The id is server-minted per request (uuid4), so
        unlike DebtorStore.create there is no first-save race to lose — but an
        implementation MUST still refuse to overwrite an existing (case, kind,
        id) by raising, because a silent replace here would mean the id minting
        is broken and something just erased a record to prove it."""
        ...

    def get(
        self, case_id: str, kind: EntityKind[BodyT], entity_id: str
    ) -> CaseEntity[BodyT] | None: ...

    def put(self, entity: CaseEntity[Any]) -> bool:
        """Replace the record, but only over one that still exists — True when
        it did, False when it was gone. The route turns False into the same
        404 a foreign id gets, so an edit racing a delete does not silently
        resurrect the record."""
        ...

    def delete(self, case_id: str, kind: EntityKind[Any], entity_id: str) -> bool:
        """Remove the record. True if this call removed it, False if there was
        nothing there — so two concurrent deletes cannot both report
        success."""
        ...

    def list_for_case(
        self, case_id: str, kind: EntityKind[BodyT]
    ) -> tuple[CaseEntity[BodyT], ...]:
        """Every record of one collection in one case, in creation order
        (core/case_entities.list_order — the sort key is a random uuid, so
        neither implementation gets this ordering for free). All of them: a
        caller cannot page, so an implementation that can truncate must not."""
        ...


class AccessLog(Protocol):
    """Append-only record of who read or changed which case.

    Write-only by design, on both sides of the boundary: this port has no read
    method, and the API role's IAM grant is PutItem alone. See
    core/access_log.py for why.
    """

    def record(self, event: AccessEvent) -> None: ...


class CandidateStore(Protocol):
    """Persists candidate records (mcp-surface.md § Candidate writes;
    docs/reference/case-data-model.md's `extraction_candidate`).

    Moved here from services/mcp with the candidate domain when the review
    flow became its second composer (ADR 0012's admission rule; issues
    8.7-8.9): the MCP service writes proposals, the extraction workers write
    extracted candidates, and the review routes read and resolve both.

    Ownership is NOT a parameter here, the same rule every case-child store
    states: a candidate is reached only through its case, the caller resolves
    the case through `CaseStore` first on every path, and a second
    authorisation path here would eventually disagree with the first. What
    every method DOES enforce is the case scope: `case_id` is half the key,
    so a candidate id from another case does not resolve here.
    """

    def create(self, candidate: Candidate) -> None:
        """Store a new row. Ids are server-minted uuid4s, so an existing
        (case, id) means the minting is broken — implementations MUST raise
        rather than silently replace, exactly as CaseEntityStore.create
        does."""
        ...

    def get(self, case_id: str, candidate_id: str) -> Candidate | None: ...

    def list_for_case(self, case_id: str) -> tuple[Candidate, ...]:
        """Every candidate of one case, in creation order
        (core/candidates.list_order — the sort key is a random uuid, so
        neither implementation gets the ordering for free). All of them; the
        callers paginate, and status filtering happens above this port so
        implementations cannot drift on what a filter means."""
        ...

    def update(self, candidate: Candidate, *, expected_status: str) -> Candidate | None:
        """Write `candidate` back, but only if the stored status is still
        `expected_status` — the compare-and-swap withdrawal and review both
        ride on. None means the condition failed (or the row is gone): the
        caller lost a race and must not pretend otherwise — a withdrawal that
        overwrote an acceptance would silently un-review a record the human
        just confirmed, and two reviewers racing must get one winner."""
        ...
