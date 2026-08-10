"""Firms, the people in them, and what each of them may do.

The tenancy layer. Until this module existed a case belonged to one human
being — `case.owner_principal` was a Cognito `sub` — so two people at one firm
could not work the same matter, and a colleague opening a case got a 404. For
a bankruptcy practice that is not a limitation, it is a non-starter, and the
business plan has been selling multi-seat firms the whole time.

The vocabulary is MyCase's, deliberately, so a firm evaluating us recognises
what it is looking at: firm users with a ROLE, an ADMIN flag, per-case LINKING
with an all-cases switch, and per-feature permission LEVELS. That is where the
resemblance stops — nothing here integrates with MyCase, and the feature list
below is ours.

Everything in this module is pure: no Flask, no boto3, no clock beyond
datetime.now. The item shapes live here rather than in an adapter so the
DynamoDB and in-memory stores cannot drift apart, exactly as core/cases.py does
for the case root.

WHY A FIRM USER HAS NO ID OF ITS OWN, since every other entity in this service
has a uuid and the omission would otherwise look like a slip.

A firm user is keyed by `(firm_id, subject)`, and `subject` is the Cognito
`sub`: server-minted, globally unique, immutable, and already the only thing an
access token carries. A surrogate id would be a THIRD name for the same row,
and every lookup would then have to choose between them — with the failure mode
that the two disagree.

This is not the debtor situation in reverse. A debtor has no server-supplied
identifier at all, so a key there has to be invented from the data; a firm user
arrives with one. The rule both follow is the same: key on an identifier the
server owns, and invent one only when there is none.
"""

from __future__ import annotations

import re
import uuid
from collections.abc import Iterable, Mapping
from dataclasses import dataclass, replace
from datetime import UTC, datetime
from typing import Final

from insolvia_core.errors import FieldValidationError, ValidationError

# A job title, and — this is the part that is easy to get wrong — NOT a level
# of access. MyCase keeps them independent and so do we: role drives the
# DEFAULT permission map a new user gets (see `default_permissions`) and
# nothing else. Everything that actually decides what someone can reach is on
# the other three axes: `is_admin`, `access_all_cases`, and the permission map
# itself, all of which an admin can set to anything for anyone.
#
# The trap this avoids is the one where "attorney" quietly comes to mean "can
# see everything". If a firm wants that, they set `access_all_cases` — which is
# a thing they chose, visible in the user's record, rather than a consequence
# of a job title nobody thought of as a permission.
ROLES: Final = ("attorney", "paralegal", "staff")

FIRM_STATUSES: Final = ("active", "suspended")
USER_STATUSES: Final = ("active", "disabled")

# WHAT A PERMISSION CAN BE SET TO. Ordered weakest to strongest, and the order
# is load-bearing — `permits` compares by index, so inserting a level in the
# middle changes what every existing check means. Append, or renumber
# deliberately.
HIDDEN: Final = "hidden"
VIEW_ONLY: Final = "view_only"
ADD_EDIT: Final = "add_edit"
LEVELS: Final = (HIDDEN, VIEW_ONLY, ADD_EDIT)

# THE FEATURES, and this list is ours rather than MyCase's. Theirs enumerates
# billing, tasks, calendars and events, none of which exist here; ours
# enumerates the five things this product actually does.
#
# `extraction_review` names work that does not exist yet (issues 8.7-8.9). That
# is on purpose and is the whole argument for the fail-closed default below: it
# is listed now so it is `hidden` for everyone by default, and grows a meaning
# when the feature lands, rather than appearing one deploy later as something
# every existing user could already do.
CASES: Final = "cases"
INTAKE: Final = "intake"
DOCUMENTS: Final = "documents"
EXTRACTION_REVIEW: Final = "extraction_review"
FIRM_ADMINISTRATION: Final = "firm_administration"
FEATURES: Final = (CASES, INTAKE, DOCUMENTS, EXTRACTION_REVIEW, FIRM_ADMINISTRATION)

MAX_FIRM_NAME: Final = 200
MAX_DISPLAY_NAME: Final = 200
MAX_EMAIL: Final = 320

# Same shape as core/waitlist.py's. Deliberately loose: this address is a
# display and contact value, and the thing that actually establishes it is
# Cognito, which sends to it.
_EMAIL_RE: Final = re.compile(r"^[^@\s]+@[^@\s]+\.[^@\s]+$")

# A Cognito `sub` is a uuid, and this service keys rows on it. Validated rather
# than trusted because the value flows into a sort key (`USER#<subject>`) and
# into a GSI partition key: a `sub` carrying a `#` would produce a key that
# collides with a differently-spelled one.
_SUBJECT_RE: Final = re.compile(
    r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\Z"
)


@dataclass(frozen=True)
class Firm:
    """A law firm — the META item of its partition, and the tenant."""

    id: str
    name: str
    status: str
    created_at: str
    updated_at: str


@dataclass(frozen=True)
class FirmUser:
    """One person at one firm, and everything that decides what they may see.

    Four independent axes, which is more than it looks:

      role              a job title. Drives defaults, decides nothing.
      is_admin          MyCase's "Admin User": every feature, every case, and
                        the ability to manage the firm's users.
      access_all_cases  every case in the firm, without being linked to them
                        one by one. Separate from `is_admin` so a supervising
                        attorney can see the whole caseload without also being
                        able to add users or change anyone's permissions.
      permissions       per-feature level. Consulted through `permission_for`,
                        never read directly — see it for why.
    """

    firm_id: str
    subject: str
    email: str
    display_name: str
    role: str
    is_admin: bool
    access_all_cases: bool
    permissions: Mapping[str, str]
    status: str
    created_at: str
    updated_at: str


@dataclass(frozen=True)
class FirmDraft:
    """A validated firm creation request, before server-generated identity."""

    name: str


@dataclass(frozen=True)
class FirmUserDraft:
    """A validated request to add someone to a firm.

    No `subject`: the caller does not choose it. It comes back from Cognito
    when the administration route creates the pool user, which is what keeps a
    firm admin from attaching a row to somebody else's identity.
    """

    email: str
    display_name: str
    role: str
    is_admin: bool
    access_all_cases: bool
    permissions: Mapping[str, str]


@dataclass(frozen=True)
class FirmUserChanges:
    """A validated PATCH body. None means "leave unchanged" — a caller changing
    a role alone must not silently reset the permission map."""

    display_name: str | None = None
    role: str | None = None
    is_admin: bool | None = None
    access_all_cases: bool | None = None
    permissions: Mapping[str, str] | None = None
    status: str | None = None


def _timestamp() -> str:
    """Millisecond-precision UTC with a literal Z.

    Milliseconds rather than core/cases.py's microseconds because nothing sorts
    on this value: firm rows are reached by key, and the one index is keyed by
    subject. Same format as core/documents.py, and for the same reason.
    """
    return datetime.now(UTC).isoformat(timespec="milliseconds").replace("+00:00", "Z")


# ── Permissions ─────────────────────────────────────────────────────


def default_permissions(role: str) -> dict[str, str]:
    """What a new user of `role` gets before an admin touches anything.

    ATTORNEY AND PARALEGAL ARE IDENTICAL, and that is a statement rather than
    an oversight. Both do case work: they open matters, run intake, upload
    documents and will review extracted values. Inventing a difference — only
    attorneys may confirm extraction, say — would be this module deciding a
    firm's internal policy on its behalf, and it would decide it invisibly,
    because nobody reads a defaults table to find out why a paralegal is
    getting a 403.

    Staff is genuinely narrower: they file paperwork and chase documents, so
    documents is add_edit and the case record itself is read-only.

    These are DEFAULTS. An admin can set any user to any level on any feature,
    which is the setting a firm with a different view should use — not a change
    to this function.
    """
    if role == "staff":
        return {
            CASES: VIEW_ONLY,
            INTAKE: VIEW_ONLY,
            DOCUMENTS: ADD_EDIT,
            EXTRACTION_REVIEW: HIDDEN,
            FIRM_ADMINISTRATION: HIDDEN,
        }
    return {
        CASES: ADD_EDIT,
        INTAKE: ADD_EDIT,
        DOCUMENTS: ADD_EDIT,
        EXTRACTION_REVIEW: ADD_EDIT,
        # Never a default, for any role. Managing the firm's users is what
        # `is_admin` is, and a second route to it that arrives with a job title
        # would make "who can add users here" unanswerable without reading two
        # fields and this function.
        FIRM_ADMINISTRATION: HIDDEN,
    }


def permission_for(user: FirmUser, feature: str) -> str:
    """This user's level for `feature`. THE ONLY WAY TO READ THE MAP.

    Two things happen here that a `user.permissions[feature]` would not do, and
    both of them are the difference between a permission system and a
    dictionary:

    FAIL CLOSED ON AN UNKNOWN FEATURE. A feature missing from the map — because
    it was added after this user's row was written, or because the row predates
    the map entirely — is `hidden`. The alternative is a KeyError in a route,
    or worse, a `.get(feature, ADD_EDIT)` somebody wrote to stop the KeyError.
    A new feature is invisible until someone grants it.

    A DISABLED USER HAS NO PERMISSIONS. The primary control for this is
    accessor resolution, which refuses to mint an Accessor for a non-active
    user at all, so in practice this branch is unreachable. It is here anyway
    because duplicating a check on the DENY side cannot produce a wrong ALLOW —
    the two can never disagree in the dangerous direction — and it means a
    future caller that gets hold of a FirmUser without going through resolution
    still fails safe.

    An admin gets `add_edit` on everything. That is what MyCase's Admin User
    means and it is why the flag exists; a firm that wants an admin restricted
    on a feature wants that person not to be an admin.
    """
    if user.status != "active":
        return HIDDEN
    if user.is_admin:
        return ADD_EDIT
    level = user.permissions.get(feature, HIDDEN)
    # A level this version does not recognise is also `hidden` — the same
    # fail-closed rule, applied to a value rather than a key. A row written by
    # a newer version with a level we cannot rank must not be ranked wrong.
    return level if level in LEVELS else HIDDEN


def is_active_admin(user: FirmUser) -> bool:
    return user.is_admin and user.status == "active"


def would_leave_no_admin(
    users: Iterable[FirmUser],
    *,
    changed: FirmUser | None = None,
    removed: str | None = None,
) -> bool:
    """Whether applying this edit leaves the firm with nobody who can administer it.

    THE ONE IRRECOVERABLE MISTAKE A FIRM ADMIN CAN MAKE, and the reason this
    check exists rather than being left to good sense: self-signup is disabled
    on the pool (`allow_admin_create_user_only`), so a firm with no active
    admin cannot add one back. Nobody inside the firm can fix it. It becomes a
    support ticket and a hand-run script against production data.

    It is easy to reach by accident, too. The admin who set the firm up demotes
    themselves after promoting a colleague — except they promoted the wrong
    person, or that colleague is `disabled`. Or they remove their own account
    because they are leaving the firm, on their last day, with nobody left
    holding the flag.

    Takes the WHOLE staff list rather than a count, because the caller is not
    necessarily the person being changed: an admin may demote another admin,
    and the answer depends on who else is left. Both edits are expressed the
    same way — `changed` is the post-edit record, `removed` is a subject that
    will no longer be there.
    """
    remaining = [
        user
        for user in users
        if user.subject != removed
        and (changed is None or user.subject != changed.subject)
    ]
    if changed is not None and changed.subject != removed:
        remaining.append(changed)
    return not any(is_active_admin(user) for user in remaining)


def permits(user: FirmUser, feature: str, required: str) -> bool:
    """Whether this user may do something needing `required` on `feature`.

    Compares by position in LEVELS, so `add_edit` satisfies a `view_only`
    requirement and nothing satisfies a requirement the caller is `hidden` for.
    """
    if required not in LEVELS:
        raise ValidationError(f"unknown permission level: {required!r}")
    return LEVELS.index(permission_for(user, feature)) >= LEVELS.index(required)


# ── Parsing ─────────────────────────────────────────────────────────


def _parse_name(
    value: object, errors: dict[str, str], *, field: str, cap: int
) -> str | None:
    if not isinstance(value, str) or not value.strip():
        errors[field] = "A name is required."
        return None
    name = value.strip()
    if len(name) > cap:
        errors[field] = f"Please keep this under {cap} characters."
        return None
    return name


def _parse_email(value: object, errors: dict[str, str]) -> str | None:
    if not isinstance(value, str) or not value.strip():
        errors["email"] = "An email address is required."
        return None
    # Lowercased because addresses are matched, and a firm admin typing
    # "Jane@Firm.com" is not adding a second person.
    email = value.strip().lower()
    if len(email) > MAX_EMAIL:
        errors["email"] = f"Please keep this under {MAX_EMAIL} characters."
        return None
    if not _EMAIL_RE.match(email):
        errors["email"] = "That doesn't look like a valid email address."
        return None
    return email


def _parse_role(value: object, errors: dict[str, str]) -> str | None:
    if not isinstance(value, str) or value not in ROLES:
        errors["role"] = "Role must be one of " + ", ".join(ROLES) + "."
        return None
    return value


def _parse_flag(value: object, errors: dict[str, str], *, field: str) -> bool | None:
    # Strictly bool. A truthy string would be the classic way an admin flag
    # gets set by accident: JSON `"false"` is a non-empty string.
    if not isinstance(value, bool):
        errors[field] = "Must be true or false."
        return None
    return value


def _parse_permissions(value: object, errors: dict[str, str]) -> dict[str, str] | None:
    if not isinstance(value, Mapping):
        errors["permissions"] = "Permissions must be an object."
        return None
    parsed: dict[str, str] = {}
    for feature, level in value.items():
        # Unknown features are REFUSED, not dropped. Dropping would leave the
        # admin believing they granted something — the client would echo their
        # own request back and the stored row would disagree. It is also the
        # shape a typo takes ("document" for "documents"), which is the case
        # this actually catches.
        if not isinstance(feature, str) or feature not in FEATURES:
            errors["permissions"] = (
                "Unknown feature. Features are " + ", ".join(FEATURES) + "."
            )
            return None
        if not isinstance(level, str) or level not in LEVELS:
            errors["permissions"] = (
                "Each level must be one of " + ", ".join(LEVELS) + "."
            )
            return None
        parsed[feature] = level
    return parsed


def parse_firm_creation(payload: Mapping[str, object]) -> FirmDraft:
    """Validate a firm creation request. Unknown keys are ignored.

    Status is not accepted: every firm starts `active`, and a caller creating
    one already `suspended` would be describing a state nobody asked for.
    """
    errors: dict[str, str] = {}
    name = _parse_name(payload.get("name"), errors, field="name", cap=MAX_FIRM_NAME)
    if errors or name is None:
        raise FieldValidationError(errors)
    return FirmDraft(name=name)


def parse_firm_user_creation(payload: Mapping[str, object]) -> FirmUserDraft:
    """Validate POST /v1/firm/users. Unknown keys are ignored.

    `role` is required and the other three are optional, which is the split
    that matters: a firm admin adding a paralegal should not have to compose a
    permission map, and the one they get is `default_permissions(role)`. Supply
    `permissions` and it is merged OVER those defaults rather than replacing
    them, so a caller granting one feature does not silently revoke the rest.
    """
    errors: dict[str, str] = {}
    email = _parse_email(payload.get("email"), errors)
    display_name = _parse_name(
        payload.get("displayName"), errors, field="displayName", cap=MAX_DISPLAY_NAME
    )
    role = _parse_role(payload.get("role"), errors)

    is_admin = False
    if "isAdmin" in payload:
        parsed_admin = _parse_flag(payload["isAdmin"], errors, field="isAdmin")
        if parsed_admin is not None:
            is_admin = parsed_admin

    access_all_cases = False
    if "accessAllCases" in payload:
        parsed_access = _parse_flag(
            payload["accessAllCases"], errors, field="accessAllCases"
        )
        if parsed_access is not None:
            access_all_cases = parsed_access

    overrides: dict[str, str] = {}
    if "permissions" in payload:
        parsed_permissions = _parse_permissions(payload["permissions"], errors)
        if parsed_permissions is not None:
            overrides = parsed_permissions

    if errors or email is None or display_name is None or role is None:
        raise FieldValidationError(errors)

    return FirmUserDraft(
        email=email,
        display_name=display_name,
        role=role,
        is_admin=is_admin,
        access_all_cases=access_all_cases,
        permissions={**default_permissions(role), **overrides},
    )


def parse_firm_user_update(payload: Mapping[str, object]) -> FirmUserChanges:
    """Validate PATCH /v1/firm/users/<subject>.

    `email` is NOT changeable here and its absence is the point: the address on
    this row is the one Cognito authenticates and sends to, and letting a firm
    admin rewrite it in our store would leave two systems disagreeing about who
    someone is. Changing it is a pool operation.

    `permissions` REPLACES the map rather than merging into it, the opposite of
    creation. A PATCH that merged could only ever grant — there would be no way
    to express "take documents away" — so the caller sends the map it wants.
    """
    errors: dict[str, str] = {}
    changes: dict[str, object] = {}

    if "displayName" in payload:
        display_name = _parse_name(
            payload["displayName"],
            errors,
            field="displayName",
            cap=MAX_DISPLAY_NAME,
        )
        if display_name is not None:
            changes["display_name"] = display_name
    if "role" in payload:
        role = _parse_role(payload["role"], errors)
        if role is not None:
            changes["role"] = role
    if "isAdmin" in payload:
        is_admin = _parse_flag(payload["isAdmin"], errors, field="isAdmin")
        if is_admin is not None:
            changes["is_admin"] = is_admin
    if "accessAllCases" in payload:
        access_all_cases = _parse_flag(
            payload["accessAllCases"], errors, field="accessAllCases"
        )
        if access_all_cases is not None:
            changes["access_all_cases"] = access_all_cases
    if "permissions" in payload:
        permissions = _parse_permissions(payload["permissions"], errors)
        if permissions is not None:
            changes["permissions"] = permissions
    if "status" in payload:
        status = payload["status"]
        if not isinstance(status, str) or status not in USER_STATUSES:
            errors["status"] = "Status must be one of " + ", ".join(USER_STATUSES) + "."
        else:
            changes["status"] = status

    if errors:
        raise FieldValidationError(errors)
    if not changes:
        raise ValidationError("no supported fields to update")
    return FirmUserChanges(**changes)  # type: ignore[arg-type]


# ── Construction ────────────────────────────────────────────────────


def create_firm(draft: FirmDraft) -> Firm:
    now = _timestamp()
    return Firm(
        id=str(uuid.uuid4()),
        name=draft.name,
        status="active",
        created_at=now,
        updated_at=now,
    )


def create_firm_user(draft: FirmUserDraft, *, firm_id: str, subject: str) -> FirmUser:
    """Attach a validated draft to a firm and a Cognito identity.

    Both keys come from the server: `firm_id` from the admin's own resolved
    accessor, `subject` from the pool. Neither is ever read from a request
    body — a firm admin who could name either would be able to add a user to
    another firm, or to bind a row to an identity that is not the one they just
    created.
    """
    if not _SUBJECT_RE.match(subject):
        raise ValidationError("a firm user's subject must be a Cognito sub")
    now = _timestamp()
    return FirmUser(
        firm_id=firm_id,
        subject=subject,
        email=draft.email,
        display_name=draft.display_name,
        role=draft.role,
        is_admin=draft.is_admin,
        access_all_cases=draft.access_all_cases,
        permissions=dict(draft.permissions),
        status="active",
        created_at=now,
        updated_at=now,
    )


def apply_user_changes(user: FirmUser, changes: FirmUserChanges) -> FirmUser:
    """A new FirmUser with the supplied changes applied and updated_at refreshed.

    CHANGING A ROLE DOES NOT RESET THE PERMISSION MAP. Roles supply defaults at
    creation and nothing after that (`default_permissions`), so promoting a
    paralegal to attorney leaves whatever an admin has since set in place. The
    alternative would quietly undo hand-tuned permissions as a side effect of a
    job-title edit, which is exactly the kind of change nobody attributes to
    the thing that caused it. A caller that wants the new role's defaults sends
    them in the same PATCH.
    """
    updates = {
        field: value
        for field, value in (
            ("display_name", changes.display_name),
            ("role", changes.role),
            ("is_admin", changes.is_admin),
            ("access_all_cases", changes.access_all_cases),
            ("permissions", changes.permissions),
            ("status", changes.status),
        )
        if value is not None
    }
    return replace(user, updated_at=_timestamp(), **updates)  # type: ignore[arg-type]


# ── Keys and the stored item shapes ─────────────────────────────────

# What an item value may be in this table, and it is a wider set than the case
# table's `str | int`: a firm user carries two booleans and a map. The adapter's
# converter handles exactly these four, and BOOL IS CHECKED BEFORE INT there,
# because in Python `True` is an int and would otherwise be stored as the
# number 1.
FirmItemValue = str | bool | dict[str, str]


def partition_key(firm_id: str) -> str:
    return f"FIRM#{firm_id}"


def user_sort_key(subject: str) -> str:
    return f"USER#{subject}"


def subject_key(subject: str) -> str:
    return f"USER#{subject}"


def firm_item(firm: Firm) -> dict[str, FirmItemValue]:
    """The exact stored item shape, shared by both FirmStore implementations.

    PK  FIRM#<id>
    SK  META            the firm itself; its users are USER#<subject> in the
                        same partition

    NO GSI KEYS, and that is what makes the by-subject index sparse: it holds
    one entry per user and none for the firm, so a query on it returns people
    rather than a mix of people and the firm they belong to.
    """
    return {
        "PK": partition_key(firm.id),
        "SK": "META",
        "id": firm.id,
        "name": firm.name,
        "status": firm.status,
        "createdAt": firm.created_at,
        "updatedAt": firm.updated_at,
    }


def firm_from_item(item: Mapping[str, FirmItemValue]) -> Firm:
    """Inverse of firm_item. Raises ValidationError on an item this service did
    not write — a corrupt row should fail loudly here rather than become a
    half-populated Firm."""
    try:
        return Firm(
            id=str(item["id"]),
            name=str(item["name"]),
            status=str(item["status"]),
            created_at=str(item["createdAt"]),
            updated_at=str(item["updatedAt"]),
        )
    except KeyError as error:
        raise ValidationError(f"stored firm item is malformed: {error}") from error


def firm_user_item(user: FirmUser) -> dict[str, FirmItemValue]:
    """The exact stored item shape, shared by both FirmStore implementations.

    PK      FIRM#<firm_id>      so one Query returns a firm's whole staff list
    SK      USER#<subject>
    GSI1PK  USER#<subject>      the by-subject index: given a token's `sub`,
    GSI1SK  FIRM#<firm_id>      which firm, and what may they do

    THE GSI KEYS ARE THE SHARP EDGE OF THE SPARSE INDEX. DynamoDB indexes an
    item only when it carries every key attribute, so omitting GSI1PK here does
    not raise anything — it produces a user who simply cannot sign in, with no
    error in any log. That is why they are written here, unconditionally,
    rather than by an adapter that might have a branch.
    """
    return {
        "PK": partition_key(user.firm_id),
        "SK": user_sort_key(user.subject),
        "GSI1PK": subject_key(user.subject),
        "GSI1SK": partition_key(user.firm_id),
        "firmId": user.firm_id,
        "subject": user.subject,
        "email": user.email,
        "displayName": user.display_name,
        "role": user.role,
        "isAdmin": user.is_admin,
        "accessAllCases": user.access_all_cases,
        # Stored whole rather than as a set of granted features. A full map is
        # what an operator sees in the console when they ask why someone got a
        # 403, and a feature absent from it still reads as `hidden` through
        # `permission_for` — so the two encodings agree and this one explains
        # itself.
        "permissions": dict(user.permissions),
        "status": user.status,
        "createdAt": user.created_at,
        "updatedAt": user.updated_at,
    }


def firm_user_from_item(item: Mapping[str, FirmItemValue]) -> FirmUser:
    """Inverse of firm_user_item.

    The permission map is filtered to features and levels this version knows,
    which is the same fail-closed rule `permission_for` applies, moved to the
    boundary where the value enters the service. A row carrying a feature name
    we do not recognise is not corrupt — it is a row from a newer version — and
    dropping the entry is the reading that cannot over-grant.
    """
    try:
        raw = item["permissions"]
        permissions = (
            {
                feature: level
                for feature, level in raw.items()
                if feature in FEATURES and level in LEVELS
            }
            if isinstance(raw, dict)
            else {}
        )
        return FirmUser(
            firm_id=str(item["firmId"]),
            subject=str(item["subject"]),
            email=str(item["email"]),
            display_name=str(item["displayName"]),
            role=str(item["role"]),
            # bool() rather than a cast: these are stored as DynamoDB BOOL and
            # come back as Python bools, and a row that somehow holds anything
            # else must not make `is_admin` truthy by accident.
            is_admin=item["isAdmin"] is True,
            access_all_cases=item["accessAllCases"] is True,
            permissions=permissions,
            status=str(item["status"]),
            created_at=str(item["createdAt"]),
            updated_at=str(item["updatedAt"]),
        )
    except KeyError as error:
        raise ValidationError(f"stored firm user item is malformed: {error}") from error


def firm_json(firm: Firm) -> dict[str, object]:
    return {
        "id": firm.id,
        "name": firm.name,
        "status": firm.status,
        "createdAt": firm.created_at,
        "updatedAt": firm.updated_at,
    }


def firm_user_summary_json(user: FirmUser) -> dict[str, object]:
    """A colleague, as anyone in the firm may see them.

    DELIBERATELY THINNER THAN `firm_user_json`, and the difference is the whole
    reason there are two. A case carries `createdBy` as a Cognito subject and
    an assignment list is a list of subjects, so every member of a firm needs
    to be able to turn a subject into a name — otherwise the case list reads
    "opened by 00000000-0000-4000-8000-…".

    That need is satisfied by three fields. It does NOT justify handing every
    paralegal their colleagues' email addresses, permission maps, or whether
    somebody has been disabled — those are the firm's administration, and
    `firm_user_json` is what an administrator gets.
    """
    return {
        "subject": user.subject,
        "displayName": user.display_name,
        "role": user.role,
    }


def firm_user_json(user: FirmUser) -> dict[str, object]:
    """The API representation, for the firm's own administration screens.

    `subject` IS exposed here, unlike `ownerPrincipal` on a case, and the
    difference is who is looking. A case response goes to its owner, for whom
    the subject is their own and carries nothing; this response goes to a firm
    admin managing named colleagues, and the subject is the id every other
    endpoint addresses them by — case assignment takes it in the path. Withheld,
    the screen could list users and not act on them.

    `permissions` is the STORED map, not the effective one. An admin's row may
    say `firm_administration: hidden` while `permission_for` answers `add_edit`
    for them, and showing the resolved value would make the admin flag look
    like it had rewritten the map. `isAdmin` is in the same object; the client
    renders the override.
    """
    return {
        "subject": user.subject,
        "email": user.email,
        "displayName": user.display_name,
        "role": user.role,
        "isAdmin": user.is_admin,
        "accessAllCases": user.access_all_cases,
        "permissions": dict(user.permissions),
        "status": user.status,
        "createdAt": user.created_at,
        "updatedAt": user.updated_at,
    }
