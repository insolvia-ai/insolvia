"""The ports of the shared domain — implemented in `insolvia_core.adapters`.

These three protocols moved here from `insolvia_api.core.ports` when the firm
domain became shared (issue #208): they are the seams both the tenant API and
the admin service compose, so they live with the domain they serve. Ports that
only one service composes stay in that service.
"""

from __future__ import annotations

from typing import Any, Protocol

from insolvia_core.firms import Firm, FirmUser


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
