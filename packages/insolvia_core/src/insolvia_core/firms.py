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
# The cap on a WHOLE display name. It survives the first/last split because the
# transition arm in the parsers below still accepts one — see `split_legacy_name`.
MAX_DISPLAY_NAME: Final = 200
# The cap on one HALF of a name. Deliberately lower than MAX_DISPLAY_NAME: two
# parts at 200 each would allow a 401-character display string, which is not
# what the old cap meant. Reads never run through a parser, so no stored row can
# fail this — only somebody actively editing a part longer than it, which has
# never happened.
MAX_NAME_PART: Final = 100
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
    """A law firm — the META item of its partition, and the tenant.

    `created_by` / `created_by_email` are provisioning provenance (#212): the
    staff principal who created the firm through the admin service, straight
    off their verified token. TOLERATED-ABSENT, as None — firms seeded before
    the portal existed carry neither, and a portal renders that honestly as
    "seeded" rather than inventing an author. They are facts about creation,
    not authorization: nothing anywhere grants on them (the same rule
    `case.createdBy` follows).
    """

    id: str
    name: str
    status: str
    created_at: str
    updated_at: str
    created_by: str | None = None
    created_by_email: str | None = None


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

    THE NAME IS TWO FIELDS AND NO STORED DISPLAY STRING. `full_name` composes
    the one a screen renders, so there is exactly one place that decides how the
    halves join. Storing the composed value beside them would be a second owner
    of the same fact, and `apply_user_changes` is where it would go wrong: a
    PATCH that set `first_name` and forgot to recompute would leave a row whose
    two name fields disagreed, with nothing in the type system to notice.

    `""` IS A REAL VALUE on either half, and load-bearing — it means "never
    recorded". Rows written before the split carry one display string, and
    `firm_user_from_item` derives what it can from it; a name it cannot split
    (a single token) yields an empty surname, which is the honest answer and
    what the client's first-run prompt keys on.
    """

    firm_id: str
    subject: str
    email: str
    first_name: str
    last_name: str
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
    first_name: str
    last_name: str
    role: str
    is_admin: bool
    access_all_cases: bool
    permissions: Mapping[str, str]


@dataclass(frozen=True)
class FirmChanges:
    """A validated PATCH of the firm's own record (#217). None means "leave
    unchanged".

    `status` is deliberately not here and never will be: `set_firm_status` is
    the ONLY legal status write, and it belongs to the admin service — a firm
    suspending itself is a lockout with no self-service recovery, because
    self-signup is off. Future firm-profile fields join this class; the status
    axis does not.
    """

    name: str | None = None


@dataclass(frozen=True)
class FirmUserChanges:
    """A validated PATCH body. None means "leave unchanged" — a caller changing
    a role alone must not silently reset the permission map."""

    first_name: str | None = None
    last_name: str | None = None
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


def full_name(user: FirmUser) -> str:
    """The display string, composed from the two halves.

    THE ONLY PLACE THAT DECIDES HOW A NAME READS. Every serializer and every
    store adapter calls this rather than joining the halves itself, which is
    what makes "a name is two fields" a decision with one consequence instead of
    a rule six call sites have to remember.

    Drops the separator when a half is empty, so a row that only ever recorded
    one part reads as that part rather than as a name with a stray space.
    """
    return " ".join(part for part in (user.first_name, user.last_name) if part)


def split_legacy_name(value: str) -> tuple[str, str]:
    """Split a single stored display name into a first and last half.

    THE MIGRATION, and it is a guess — which is why it lives in one named
    function that both the read path and the parsers' transition arm call, so
    they cannot disagree about what a given string becomes.

    Splits on the LAST space, so "Mary Anne Smith" keeps "Mary Anne" together
    and takes "Smith" as the surname. That is right far more often than
    splitting on the first space, and wrong for a compound surname ("Mary van
    der Berg" gives "Mary van der"). Wrong is recoverable in ten seconds on the
    account screen; the alternative — refusing to guess and blanking every
    colleague's name until each person next signs in — reads as data loss to a
    whole firm at once.

    A single token yields an empty surname rather than a duplicated one. We do
    not know that person's surname, and saying so is what lets the client ask.
    """
    trimmed = value.strip()
    if not trimmed:
        return ("", "")
    first, _, last = trimmed.rpartition(" ")
    # `rpartition` puts the whole string in the LAST element when there is no
    # separator, which is the single-token case: that token is the first name.
    return (first.strip(), last.strip()) if first else (last.strip(), "")


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


def _parse_legacy_name(value: object, errors: dict[str, str]) -> tuple[str, str] | None:
    """Accept a pre-split `displayName` and split it. THE TRANSITION ARM.

    Every parser below takes `firstName`/`lastName`, and also still takes the
    single `displayName` a client written before the split sends. That is not
    politeness — it is the deploy window. The release order puts the API live
    before the new bundle exists, but a browser holding the OLD bundle keeps
    sending the old shape for as long as that tab stays open, and the admin
    service redeploys a step behind the API. Without this arm, "Save name" 400s
    for those callers.

    ONE RELEASE ONLY. The follow-up deletes this function and its three call
    sites; `displayName` stays in RESPONSES indefinitely, where it is derived
    and free.

    A single token is REFUSED rather than stored with an empty surname. An old
    client must not be able to write a row that puts its own user in front of
    the first-run prompt on their next load — that would look like the new
    release had lost their name.
    """
    name = _parse_name(value, errors, field="displayName", cap=MAX_DISPLAY_NAME)
    if name is None:
        return None
    first, last = split_legacy_name(name)
    if not last:
        errors["displayName"] = "Please give both a first and a last name."
        return None
    return (first, last)


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
    # Both halves are required to add somebody. The explicit pair wins outright
    # when it is present, so a client sending both spellings is not ambiguous —
    # the same "unknown keys are ignored" rule this docstring already states.
    names: tuple[str, str] | None
    if "firstName" in payload or "lastName" in payload:
        first = _parse_name(
            payload.get("firstName"), errors, field="firstName", cap=MAX_NAME_PART
        )
        last = _parse_name(
            payload.get("lastName"), errors, field="lastName", cap=MAX_NAME_PART
        )
        names = (first, last) if first is not None and last is not None else None
    else:
        names = _parse_legacy_name(payload.get("displayName"), errors)
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

    if errors or email is None or names is None or role is None:
        raise FieldValidationError(errors)

    return FirmUserDraft(
        email=email,
        first_name=names[0],
        last_name=names[1],
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

    # Each half is independently optional here, unlike creation: an admin
    # correcting a misspelled surname sends that half alone, and the other must
    # not be reset by its absence — the same "None means leave unchanged" rule
    # every other field on this parser follows.
    if "firstName" in payload:
        first = _parse_name(
            payload["firstName"], errors, field="firstName", cap=MAX_NAME_PART
        )
        if first is not None:
            changes["first_name"] = first
    if "lastName" in payload:
        last = _parse_name(
            payload["lastName"], errors, field="lastName", cap=MAX_NAME_PART
        )
        if last is not None:
            changes["last_name"] = last
    if (
        "displayName" in payload
        and "firstName" not in payload
        and "lastName" not in payload
    ):
        legacy = _parse_legacy_name(payload["displayName"], errors)
        if legacy is not None:
            changes["first_name"], changes["last_name"] = legacy
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


def parse_self_update(payload: Mapping[str, object]) -> FirmUserChanges:
    """Validate PATCH /v1/me. Unknown keys are ignored.

    Their own name is the ONE thing a member may change about themselves.
    Everything else on the row is somebody else's statement about them — role,
    permissions, the admin flag and status are an administrator's writes
    (parse_firm_user_update), and email is a pool fact, for the reason that
    parser records. So this is not parse_firm_user_update with a smaller
    allowlist by accident: a payload carrying `role` here is ignored the same
    way one carrying `email` is there, and what the caller can rely on is that
    the only thing this parser ever produces is a rename.

    EITHER HALF ALONE IS ACCEPTED. The account screen sends both; somebody
    fixing only their surname — the common case for a row whose halves were
    derived from a legacy display name — sends one. Requiring both would make
    the correction a rewrite of a value that was already right.
    """
    errors: dict[str, str] = {}
    changes: dict[str, str] = {}

    if "firstName" in payload:
        first = _parse_name(
            payload["firstName"], errors, field="firstName", cap=MAX_NAME_PART
        )
        if first is not None:
            changes["first_name"] = first
    if "lastName" in payload:
        last = _parse_name(
            payload["lastName"], errors, field="lastName", cap=MAX_NAME_PART
        )
        if last is not None:
            changes["last_name"] = last

    if not changes and not errors and "displayName" in payload:
        legacy = _parse_legacy_name(payload["displayName"], errors)
        if legacy is not None:
            changes["first_name"], changes["last_name"] = legacy

    if errors:
        raise FieldValidationError(errors)
    if not changes:
        raise ValidationError("no supported fields to update")
    return FirmUserChanges(**changes)  # type: ignore[arg-type]


def parse_firm_update(payload: Mapping[str, object]) -> FirmChanges:
    """Validate PATCH /v1/firm. Unknown keys are ignored.

    `name` is the one field today. `status` lands in the "no supported fields"
    branch on purpose — see FirmChanges for why it never joins, and the admin
    service's PATCH /v1/firms/<id> for where suspend/reactivate lives.
    """
    errors: dict[str, str] = {}
    if "name" not in payload:
        raise ValidationError("no supported fields to update")
    name = _parse_name(payload["name"], errors, field="name", cap=MAX_FIRM_NAME)
    if errors or name is None:
        raise FieldValidationError(errors)
    return FirmChanges(name=name)


# ── Construction ────────────────────────────────────────────────────


def create_firm(
    draft: FirmDraft,
    *,
    created_by: str | None = None,
    created_by_email: str | None = None,
) -> Firm:
    """A new firm from a validated draft.

    Provenance is keyword-only and optional: the admin service passes the
    staff caller's subject and email from its verified token (#212); the
    seeder passes nothing, which is the truth about a fixture.
    """
    now = _timestamp()
    return Firm(
        id=str(uuid.uuid4()),
        name=draft.name,
        status="active",
        created_at=now,
        updated_at=now,
        created_by=created_by,
        created_by_email=created_by_email,
    )


def set_firm_status(firm: Firm, status: str) -> Firm:
    """A new Firm with `status` applied and updated_at refreshed.

    The ONLY legal write to a firm's status (#212) — suspend and reactivate,
    nothing else. Validated here rather than trusted from a route so an
    unknown status can never reach `firm_item`: accessor resolution reads
    this value on every authenticated request, and a typo'd status would
    fail OPEN or CLOSED depending on which side of the comparison it landed.
    Idempotent — re-suspending a suspended firm refreshes updated_at and
    nothing else, because the admin portal cannot tell whether its first
    request landed.
    """
    if status not in FIRM_STATUSES:
        raise ValidationError(
            "Firm status must be one of " + ", ".join(FIRM_STATUSES) + "."
        )
    return replace(firm, status=status, updated_at=_timestamp())


def apply_firm_changes(firm: Firm, changes: FirmChanges) -> Firm:
    """A new Firm with a validated PATCH applied and updated_at refreshed.

    The firm-side sibling of `apply_user_changes`, and like it this touches
    nothing the changes do not name — provenance survives a rename, and status
    cannot appear here at all (FirmChanges has no such field, by decision).
    """
    updates: dict[str, object] = {}
    if changes.name is not None:
        updates["name"] = changes.name
    return replace(firm, updated_at=_timestamp(), **updates)  # type: ignore[arg-type]


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
        first_name=draft.first_name,
        last_name=draft.last_name,
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
            # Two entries, not one, and independently applied: a PATCH carrying
            # only a surname leaves the first name alone. This is the loop the
            # FirmUser docstring names as the reason there is no stored display
            # string — a third entry here that had to be kept in step with these
            # two is exactly how the halves and the whole would drift apart.
            ("first_name", changes.first_name),
            ("last_name", changes.last_name),
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
    item: dict[str, FirmItemValue] = {
        "PK": partition_key(firm.id),
        "SK": "META",
        "id": firm.id,
        "name": firm.name,
        "status": firm.status,
        "createdAt": firm.created_at,
        "updatedAt": firm.updated_at,
    }
    # Provenance is SPARSE, not null-valued: a pre-portal firm has no author,
    # and an attribute that says so by absence reads the same in the console
    # as it does through firm_from_item's tolerant read (#212).
    if firm.created_by is not None:
        item["createdBy"] = firm.created_by
    if firm.created_by_email is not None:
        item["createdByEmail"] = firm.created_by_email
    return item


def firm_from_item(item: Mapping[str, FirmItemValue]) -> Firm:
    """Inverse of firm_item. Raises ValidationError on an item this service did
    not write — a corrupt row should fail loudly here rather than become a
    half-populated Firm."""
    try:
        created_by = item.get("createdBy")
        created_by_email = item.get("createdByEmail")
        return Firm(
            id=str(item["id"]),
            name=str(item["name"]),
            status=str(item["status"]),
            created_at=str(item["createdAt"]),
            updated_at=str(item["updatedAt"]),
            # Absent is a real state (a firm seeded before the portal), so
            # these two are the one part of this inverse that does not raise.
            created_by=str(created_by) if created_by is not None else None,
            created_by_email=(
                str(created_by_email) if created_by_email is not None else None
            ),
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

    `displayName` IS STILL WRITTEN, and it is derived rather than stored — a
    transition attribute, for ONE release. The release order redeploys the API
    a step ahead of the admin service, and both read these rows through
    `firm_user_from_item`; the older of the two still does `item["displayName"]`
    and would `KeyError` into a 500 on every firm-detail page for the couple of
    minutes between the two legs. The follow-up release drops this line.

    There is no backfill script and there should not be one. Both stores write
    a whole item (`put_item`), so the first edit of any kind converges a legacy
    row on its own; a migration script would be a third writer of this shape,
    which this package's rules forbid.
    """
    return {
        "PK": partition_key(user.firm_id),
        "SK": user_sort_key(user.subject),
        "GSI1PK": subject_key(user.subject),
        "GSI1SK": partition_key(user.firm_id),
        "firmId": user.firm_id,
        "subject": user.subject,
        "email": user.email,
        "firstName": user.first_name,
        "lastName": user.last_name,
        "displayName": full_name(user),
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

    THE NAME IS THE ONE TOLERANT PAIR HERE, the same shape `firm_from_item`'s
    provenance already has. Every other field raises on absence, because its
    absence means a row this service did not write. A row with no `firstName`
    is different: it is a row written before the name was two fields, and there
    are as many of them as there are existing users. It is read by deriving the
    halves from the legacy `displayName` — see `split_legacy_name` for why we
    guess rather than blank it, and for what a single-token name yields.

    A row carrying ONE half and not the other is also tolerated rather than
    treated as corrupt: `PATCH /v1/me` accepts either half alone, so
    half-populated is a state the write path can legitimately produce.
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
        if "firstName" in item or "lastName" in item:
            first_name = str(item.get("firstName", ""))
            last_name = str(item.get("lastName", ""))
        else:
            legacy = item.get("displayName")
            first_name, last_name = split_legacy_name(
                str(legacy) if legacy is not None else ""
            )
        return FirmUser(
            firm_id=str(item["firmId"]),
            subject=str(item["subject"]),
            email=str(item["email"]),
            first_name=first_name,
            last_name=last_name,
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
    """The API representation. Provenance is explicit-null rather than absent:
    a JSON consumer reading `createdBy: null` learns "nobody recorded" (a
    seeded firm), where a missing key would read as "this API version does not
    carry the field"."""
    return {
        "id": firm.id,
        "name": firm.name,
        "status": firm.status,
        "createdAt": firm.created_at,
        "updatedAt": firm.updated_at,
        "createdBy": firm.created_by,
        "createdByEmail": firm.created_by_email,
    }


def firm_summary_json(firm: Firm) -> dict[str, object]:
    """The firm's record as its OWN members see it (#217).

    DELIBERATELY THINNER THAN `firm_json`, the same split firm_user_summary_json
    makes below and for the same kind of reason: `created_by` / `created_by_email`
    name the Insolvia staff member who provisioned the firm, and a staff
    identity is operational metadata for the admin portal — not something a
    tenant response should carry. A firm admin renaming their firm has no use
    for it; an operator auditing provisioning does.
    """
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

    `displayName` is DERIVED and stays on the wire indefinitely — it is what
    turns a subject into a rendered name, and it costs nothing to compose. The
    two halves ride alongside it for a client that needs to edit them; a client
    that only renders a name reads the one field and does not change at all.
    """
    return {
        "subject": user.subject,
        "firstName": user.first_name,
        "lastName": user.last_name,
        "displayName": full_name(user),
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
        "firstName": user.first_name,
        "lastName": user.last_name,
        "displayName": full_name(user),
        "role": user.role,
        "isAdmin": user.is_admin,
        "accessAllCases": user.access_all_cases,
        "permissions": dict(user.permissions),
        "status": user.status,
        "createdAt": user.created_at,
        "updatedAt": user.updated_at,
    }
