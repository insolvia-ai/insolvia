"""A client's binding to one case — the portal's authorization record (ADR 0023).

A debtor using the client portal is a THIRD principal class, beside a firm
user (ADR 0009) and an Insolvia staff member (ADR 0011). Their identity is a
Cognito account in the firm pool, signed in through the portal's own app
client; what they may reach is decided HERE, by a row in our own store, never
by a claim — ADR 0009's "authorization reads a store, and the store is ours"
applied to a new class.

## What a binding is

One row that says: this subject answers for these filing roles on this case of
this firm, and whether that is still true. Invited by a firm user, it moves
`invited -> active` on the client's first portal request and `-> revoked` when
the firm withdraws it. A revoked binding is kept, not deleted: it is the record
that the person once had access, and a refiled case re-binds the same subject
through it (ADR 0023 § Binding).

## Where it lives, and why three kinds of item

    firm table   PK FIRM#<firm_id>   SK CLIENT#<subject>    THE binding
                 GSI1PK CLIENT#<subject>  GSI1SK FIRM#<firm_id>
    case table   PK CASE#<case_id>   SK CLIENT#<subject>    its mirror
    case table   PK CASE#<case_id>   SK CLIENTROLE#<role>   which subject holds a role

The FIRM-TABLE ROW is authoritative. It sits in the same by-subject index a
firm user does, so resolving a portal token is the same one keyed read
resolving a staff token is — but under a `CLIENT#` partition key rather than
`USER#`. That prefix is a deliberate refinement of ADR 0023's wording ("the
firm_user row's by-subject shape"): `FirmStore.find_user` queries
`GSI1PK = USER#<subject>`, and a pool account is signable-in through ANY app
client of the pool, so a debtor who signs in to the staff app must resolve no
firm user and answer 403 — never parse a binding as a firm user. With a
distinct prefix the two lookups cannot return each other's rows by
construction, rather than by a filter somebody could drop.

The MIRROR lets a case list its clients with one keyed query, and is what the
per-role rule below reads. It carries no GSI keys, so no listing index ever
sees it.

The ROLE CLAIM is what makes "at most one live binding per role per case"
structural: each is written with a condition that it is absent or already
held by a subject this same transaction is narrowing, so two invitations
racing for `debtor_2` cannot both commit. A rule enforced by read-then-write
alone would hold only until the first race.

## Joint cases

`roles ⊆ {debtor_1, debtor_2}`, never empty, never `non_filing_spouse` — who
has no login in v1. By default a joint case is two invitations, one role each;
the firm may instead invite one debtor holding both. Inviting the second
spouse later NARROWS the first binding's roles in the same transaction. A
narrowing that would leave a binding with no role at all is refused (409):
that is revoking someone, and revoking is its own, audited act.

Pure: no boto3, no Flask. The item shapes live here so both store
implementations write the same thing — the rule `firms.py` states.
"""

from __future__ import annotations

from collections.abc import Iterable, Mapping
from dataclasses import dataclass, replace
from typing import Final

from insolvia_core.cases import partition_key as case_partition_key
from insolvia_core.errors import ConflictError, FieldValidationError, ValidationError
from insolvia_core.fields import timestamp
from insolvia_core.firms import (
    MAX_DISPLAY_NAME,
    _parse_email,
)
from insolvia_core.firms import partition_key as firm_partition_key

# The filing roles a binding may answer for. `non_filing_spouse` is a filing
# role (core/debtors.FILING_ROLES) and deliberately NOT here: ADR 0023 gives a
# non-filing spouse no login in v1 — their income is asked of a debtor.
DEBTOR_1: Final = "debtor_1"
DEBTOR_2: Final = "debtor_2"
CLIENT_ROLES: Final = (DEBTOR_1, DEBTOR_2)

INVITED: Final = "invited"
ACTIVE: Final = "active"
REVOKED: Final = "revoked"
STATUSES: Final = (INVITED, ACTIVE, REVOKED)
# A LIVE binding holds its roles and admits its holder. `invited` counts: the
# person has been sent a way in and has not yet used it.
LIVE: Final = (INVITED, ACTIVE)


@dataclass(frozen=True)
class ClientBinding:
    """One client's standing on one case.

    `display_name` is what the firm typed at invitation, and it is what staff
    see beside the client's answers — from the BINDING, never the token
    (ADR 0023 § Writes), so a client cannot rename themselves in the review
    queue. `email` is lowercased by the parser, the key the refiled-case
    lookup matches on.
    """

    firm_id: str
    case_id: str
    subject: str
    email: str
    display_name: str
    roles: tuple[str, ...]
    status: str
    invited_by: str
    created_at: str
    updated_at: str

    @property
    def live(self) -> bool:
        return self.status in LIVE


@dataclass(frozen=True)
class InvitationDraft:
    email: str
    display_name: str
    roles: tuple[str, ...]


# ── Parsing ─────────────────────────────────────────────────────────


def _parse_display_name(value: object, errors: dict[str, str]) -> str | None:
    if not isinstance(value, str) or not value.strip():
        errors["displayName"] = "A name is required."
        return None
    name = value.strip()
    if len(name) > MAX_DISPLAY_NAME:
        errors["displayName"] = f"Please keep this under {MAX_DISPLAY_NAME} characters."
        return None
    return name


def _parse_roles(value: object, errors: dict[str, str]) -> tuple[str, ...] | None:
    """`roles`, defaulting to the first debtor alone.

    The default is the common case — one debtor, one login — and it is safe
    on a joint case too: `debtor_1` already held by someone else is either a
    narrowing the store performs or a 409, never a silent second holder.
    """
    if value is None:
        return (DEBTOR_1,)
    if not isinstance(value, list) or not value:
        errors["roles"] = "Choose at least one debtor this person answers for."
        return None
    if "non_filing_spouse" in value:
        errors["roles"] = (
            "A non-filing spouse has no portal login; their details are asked "
            "of a debtor."
        )
        return None
    if any(role not in CLIENT_ROLES for role in value) or len(set(value)) != len(value):
        errors["roles"] = "Must be debtor_1, debtor_2, or both, once each."
        return None
    # Canonical order, so two invitations naming the same roles store the
    # same value.
    return tuple(role for role in CLIENT_ROLES if role in value)


def parse_invitation(payload: Mapping[str, object]) -> InvitationDraft:
    """`POST /v1/cases/<id>/portal/invitation`'s body, validated.

    Unknown keys are refused rather than ignored: a caller sending `caseId`
    or `subject` believes it controls something it does not — the case comes
    from the path the accessor may see, the subject from Cognito.
    """
    allowed = {"email", "displayName", "roles"}
    unknown = sorted(set(payload) - allowed)
    if unknown:
        raise ValidationError(f"unsupported fields: {', '.join(unknown)}")
    errors: dict[str, str] = {}
    email = _parse_email(payload.get("email"), errors)
    display_name = _parse_display_name(payload.get("displayName"), errors)
    roles = _parse_roles(payload.get("roles"), errors)
    if errors or email is None or display_name is None or roles is None:
        raise FieldValidationError(errors)
    return InvitationDraft(email=email, display_name=display_name, roles=roles)


# ── Transitions ─────────────────────────────────────────────────────


def create_binding(
    draft: InvitationDraft,
    *,
    firm_id: str,
    case_id: str,
    subject: str,
    invited_by: str,
) -> ClientBinding:
    now = timestamp()
    return ClientBinding(
        firm_id=firm_id,
        case_id=case_id,
        subject=subject,
        email=draft.email,
        display_name=draft.display_name,
        roles=draft.roles,
        status=INVITED,
        invited_by=invited_by,
        created_at=now,
        updated_at=now,
    )


def activate(binding: ClientBinding) -> ClientBinding:
    """`invited -> active`, on the client's first successful portal request.
    Anything else is returned unchanged."""
    if binding.status != INVITED:
        return binding
    return replace(binding, status=ACTIVE, updated_at=timestamp())


def revoke(binding: ClientBinding) -> ClientBinding:
    if binding.status == REVOKED:
        raise ConflictError("that client's access has already been revoked")
    return replace(binding, status=REVOKED, updated_at=timestamp())


def plan_binding(
    new: ClientBinding,
    *,
    previous: ClientBinding | None,
    on_case: Iterable[ClientBinding],
) -> tuple[ClientBinding, ...]:
    """Decide whether `new` may be written, and which bindings it narrows.

    `previous` is the firm-table row already keyed on `new.subject` in this
    firm, if any; `on_case` is every binding on `new.case_id`. Returns the
    other LIVE bindings on the case whose roles shrink, already narrowed —
    the store writes them in the same transaction as `new`.

    Refuses (409):
      - a subject that already holds a LIVE binding — anywhere in the firm.
        One active case per client (ADR 0023 § Binding); revoke first.
      - a narrowing that would empty a binding, which would be a revocation
        nobody asked for.
    """
    if previous is not None and previous.live:
        raise ConflictError(
            "that person already has portal access to a case; revoke it before "
            "inviting them again"
        )
    narrowed: list[ClientBinding] = []
    for other in on_case:
        if other.subject == new.subject or not other.live:
            continue
        kept = tuple(role for role in other.roles if role not in new.roles)
        if kept == other.roles:
            continue
        if not kept:
            raise ConflictError(
                "another client on this case already answers for "
                + " and ".join(other.roles)
                + "; revoke their access first, or invite this person for the "
                "other debtor"
            )
        narrowed.append(replace(other, roles=kept, updated_at=new.updated_at))
    return tuple(narrowed)


# ── Item shapes ─────────────────────────────────────────────────────


def client_sort_key(subject: str) -> str:
    return f"CLIENT#{subject}"


def client_subject_key(subject: str) -> str:
    """The by-subject index partition for a CLIENT — distinct from a firm
    user's `USER#<subject>`; see the module docstring for why."""
    return f"CLIENT#{subject}"


def role_claim_sort_key(role: str) -> str:
    return f"CLIENTROLE#{role}"


def _fields(binding: ClientBinding) -> dict[str, object]:
    return {
        "firmId": binding.firm_id,
        "caseId": binding.case_id,
        "subject": binding.subject,
        "email": binding.email,
        "displayName": binding.display_name,
        "roles": list(binding.roles),
        "status": binding.status,
        "invitedBy": binding.invited_by,
        "createdAt": binding.created_at,
        "updatedAt": binding.updated_at,
    }


def binding_item(binding: ClientBinding) -> dict[str, object]:
    """The authoritative row, in the FIRM table.

    PK      FIRM#<firm_id>        so a firm's clients are one keyed query
    SK      CLIENT#<subject>      (the refiled-case lookup by email)
    GSI1PK  CLIENT#<subject>      the by-subject index: given a portal
    GSI1SK  FIRM#<firm_id>        token's `sub`, which binding

    The GSI keys are written unconditionally, for `firm_user_item`'s reason:
    omitting one does not raise, it produces a client who cannot sign in.
    """
    return {
        "PK": firm_partition_key(binding.firm_id),
        "SK": client_sort_key(binding.subject),
        "GSI1PK": client_subject_key(binding.subject),
        "GSI1SK": firm_partition_key(binding.firm_id),
        **_fields(binding),
    }


def mirror_item(binding: ClientBinding) -> dict[str, object]:
    """The mirror, in the CASE table's partition. No GSI keys — the case
    table's listing indexes must never see a client."""
    return {
        "PK": case_partition_key(binding.case_id),
        "SK": client_sort_key(binding.subject),
        **_fields(binding),
    }


def role_claim_item(case_id: str, role: str, subject: str) -> dict[str, object]:
    """Who holds `role` on `case_id` — see the module docstring."""
    return {
        "PK": case_partition_key(case_id),
        "SK": role_claim_sort_key(role),
        "caseId": case_id,
        "role": role,
        "subject": subject,
    }


def binding_from_item(item: Mapping[str, object]) -> ClientBinding:
    """Inverse of `binding_item` / `mirror_item`. Raises ValidationError on a
    row this module did not write, rather than half-populating a binding."""
    try:
        raw_roles = item["roles"]
        if not isinstance(raw_roles, (list, tuple)):
            raise ValueError("roles is not a list")
        roles = tuple(str(role) for role in raw_roles if role in CLIENT_ROLES)
        status = str(item["status"])
        if status not in STATUSES:
            raise ValueError(f"unknown status {status!r}")
        return ClientBinding(
            firm_id=str(item["firmId"]),
            case_id=str(item["caseId"]),
            subject=str(item["subject"]),
            email=str(item["email"]),
            display_name=str(item["displayName"]),
            roles=roles,
            status=status,
            invited_by=str(item["invitedBy"]),
            created_at=str(item["createdAt"]),
            updated_at=str(item["updatedAt"]),
        )
    except (KeyError, ValueError) as error:
        raise ValidationError(f"stored client binding is malformed: {error}") from error


# ── Representations ─────────────────────────────────────────────────


def binding_json(binding: ClientBinding) -> dict[str, object]:
    """What a FIRM USER sees of a client on their case: enough to manage the
    invitation. Never shown to the client themselves — `/v1/portal/me` is its
    own, narrower shape."""
    return {
        "subject": binding.subject,
        "email": binding.email,
        "displayName": binding.display_name,
        "roles": list(binding.roles),
        "status": binding.status,
        "invitedBy": binding.invited_by,
        "createdAt": binding.created_at,
        "updatedAt": binding.updated_at,
    }
