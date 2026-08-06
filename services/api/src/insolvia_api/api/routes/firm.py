"""A firm's own staff list, and who may change it.

The administration surface for the tenancy model in core/firms.py. Every route
here is scoped to the CALLER'S firm — there is no path parameter naming a firm,
anywhere, and that is the design rather than a convenience. A firm id in a URL
is a value somebody will eventually pass a different one to; taking it from the
resolved accessor means the question "may I administer this firm" has exactly
one answer and it was already decided.

## Two audiences, two representations

`GET /v1/firm/directory` is for EVERY member of the firm: subject, display name,
role. A case carries `createdBy` as a Cognito subject and an assignment is a
list of subjects, so without it a case list reads "opened by
00000000-0000-4000-8000-…". It is gated on the CASES feature rather than on
firm administration, because that is what it is for.

`GET /v1/firm/users` is for administrators: the whole record, including email,
permissions and status. Gated on `firm_administration`.

Splitting them is not politeness. Folding the directory into the admin list
would mean either handing every paralegal their colleagues' permission maps, or
having no way to render a name — and the first is the one that would actually
have happened, because it is the smaller diff.
"""

from __future__ import annotations

import logging

from flask import Blueprint, jsonify, request
from flask.typing import ResponseReturnValue

from insolvia_api.api.auth import current_accessor, require_auth, requires
from insolvia_api.api.dependencies import dependencies
from insolvia_api.core.errors import (
    ConflictError,
    NotFoundError,
    ValidationError,
)
from insolvia_api.core.firms import (
    ADD_EDIT,
    CASES,
    FIRM_ADMINISTRATION,
    VIEW_ONLY,
    FirmUser,
    apply_user_changes,
    create_firm_user,
    firm_user_json,
    firm_user_summary_json,
    parse_firm_user_creation,
    parse_firm_user_update,
    would_leave_no_admin,
)
from insolvia_api.core.ports import FirmStore, UserDirectory

logger = logging.getLogger(__name__)

blueprint = Blueprint("firm", __name__)

# A firm user record is a handful of short fields plus a five-entry permission
# map. Anything larger is a mistake or an attack.
MAX_REQUEST_BYTES = 16 * 1024


def _firm_store() -> FirmStore:
    deps = dependencies()
    if deps.firm_store is None:
        raise RuntimeError("firm store is not composed")
    return deps.firm_store


def _user_directory() -> UserDirectory:
    """The identity provider, or a loud failure.

    Separate from `_firm_store` so the READ routes work without it. Only
    creating a user needs the pool, and a deployment missing it should fail on
    that one endpoint rather than take the staff list down with it.
    """
    deps = dependencies()
    if deps.user_directory is None:
        raise RuntimeError("user directory is not composed")
    return deps.user_directory


def _json_body() -> dict[str, object]:
    if request.content_length and request.content_length > MAX_REQUEST_BYTES:
        raise ValidationError("request body exceeds 16 KiB")
    payload = request.get_json(silent=True)
    if not isinstance(payload, dict):
        raise ValidationError("request body must be a JSON object")
    return payload


@blueprint.get("/v1/firm/directory")
@require_auth
@requires(CASES, VIEW_ONLY)
def firm_directory_route() -> ResponseReturnValue:
    """Everyone in the caller's firm, as names rather than records.

    Three fields per person — see the module docstring, and
    core/firms.firm_user_summary_json for what is deliberately not here.

    Disabled colleagues are INCLUDED, and that is not an oversight: a case
    opened by somebody who has since left still says `createdBy: <their
    subject>`, and dropping them from the directory would turn that into an
    unresolvable id. The client is rendering history, not offering a picker.
    """
    accessor = current_accessor()
    people = _firm_store().list_users(accessor.firm_id)
    return jsonify({"people": [firm_user_summary_json(p) for p in people]}), 200


@blueprint.get("/v1/firm/users")
@require_auth
@requires(FIRM_ADMINISTRATION, VIEW_ONLY)
def list_firm_users_route() -> ResponseReturnValue:
    accessor = current_accessor()
    users = _firm_store().list_users(accessor.firm_id)
    return jsonify({"users": [firm_user_json(user) for user in users]}), 200


@blueprint.post("/v1/firm/users")
@require_auth
@requires(FIRM_ADMINISTRATION, ADD_EDIT)
def add_firm_user_route() -> ResponseReturnValue:
    """Add a colleague: create their pool account, then their firm-user row.

    THE ORDER IS NOT ARBITRARY. Cognito first, because the subject it returns
    is half the key of the row — there is nothing to write until it exists.

    The cost of that order is the failure in the middle: the account is created
    and the row write fails. What that leaves is a pool user in no firm, which
    is a state the system already handles (`/v1/me` reports no firm, everything
    else answers 403) and which a retry of this same request resolves —
    Cognito answers 409 for the address, the admin sees "already has an
    account", and support can attach the row. The other order has no such
    story: a firm-user row keyed on a subject that does not exist is invisible
    to everyone and repairs nothing.

    The invitation email is Cognito's, and no password is generated, returned
    or logged anywhere in this service.
    """
    store = _firm_store()
    accessor = current_accessor()
    draft = parse_firm_user_creation(_json_body())

    subject = _user_directory().create_user(draft.email)
    user = create_firm_user(draft, firm_id=accessor.firm_id, subject=subject)
    store.add_user(user)

    # GLBA and then some: no email, no subject, no display name. That a user
    # was added to SOME firm is all a request log needs.
    logger.info("firm user added")
    return jsonify(firm_user_json(user)), 201


@blueprint.patch("/v1/firm/users/<subject>")
@require_auth
@requires(FIRM_ADMINISTRATION, ADD_EDIT)
def update_firm_user_route(subject: str) -> ResponseReturnValue:
    """Change a colleague's role, admin flag, access scope, permissions or status.

    Firm-scoped by key, so an admin of one firm cannot reach a user of another
    by knowing their subject — and gets the same 404 as for a subject that does
    not exist.
    """
    store = _firm_store()
    accessor = current_accessor()
    changes = parse_firm_user_update(_json_body())

    existing = store.get_user(accessor.firm_id, subject)
    if existing is None:
        raise NotFoundError("firm user not found")

    updated = apply_user_changes(existing, changes)
    _refuse_if_last_admin(store, accessor.firm_id, changed=updated)

    written = store.update_user(updated)
    if written is None:
        # Removed between the read and the write. Same 404 as a foreign
        # subject, rather than resurrecting the row.
        raise NotFoundError("firm user not found")

    logger.info("firm user updated")
    return jsonify(firm_user_json(written)), 200


@blueprint.delete("/v1/firm/users/<subject>")
@require_auth
@requires(FIRM_ADMINISTRATION, ADD_EDIT)
def remove_firm_user_route(subject: str) -> ResponseReturnValue:
    """Remove a colleague from the firm.

    REMOVES THE MEMBERSHIP, NOT THE PERSON. The Cognito account survives — this
    service holds no AdminDeleteUser and wants none — so what the removed user
    gets is a working sign-in and a 403 everywhere, which is the same state as
    somebody who has not been added yet. That is deliberate: deleting an
    identity is not reversible and is not a firm admin's call to make.

    Their case ASSIGNMENTS survive too, in the case table, and are cleaned up
    by unassigning them from the matters they were on. Nothing here reaches
    across into that table — see FirmStore.remove_user for why one store does
    not clean up another's rows.

    Disabling is usually what a firm actually wants (`PATCH … {"status":
    "disabled"}`), because it keeps the record and the history. Removal is for
    a row created by mistake.
    """
    store = _firm_store()
    accessor = current_accessor()

    _refuse_if_last_admin(store, accessor.firm_id, removed=subject)

    if not store.remove_user(accessor.firm_id, subject):
        raise NotFoundError("firm user not found")

    logger.info("firm user removed")
    return "", 204


def _refuse_if_last_admin(
    store: FirmStore,
    firm_id: str,
    *,
    changed: FirmUser | None = None,
    removed: str | None = None,
) -> None:
    """Refuse an edit that would leave the firm with no active administrator.

    THE ONE IRRECOVERABLE MISTAKE available here. Self-signup is disabled on
    the pool, so a firm with no active admin cannot appoint one — nobody inside
    it can fix it, and it becomes a hand-run script against production data.

    409 rather than 403: the caller has the permission, and it is the firm's
    STATE that does not admit the request. A 403 would read as "you may not
    administer this firm", which is exactly wrong for the person who currently
    is the only one who may.
    """
    everyone = store.list_users(firm_id)
    if would_leave_no_admin(everyone, changed=changed, removed=removed):
        raise ConflictError(
            "a firm must keep at least one active administrator; "
            "appoint another before changing this one"
        )
