from __future__ import annotations

import logging

from flask import Blueprint, jsonify, request
from flask.typing import ResponseReturnValue
from insolvia_core.errors import NotFoundError, ValidationError
from insolvia_core.firms import ADD_EDIT, CASES, VIEW_ONLY
from insolvia_core.ports import FirmStore

from insolvia_api.api.auth import current_accessor, require_auth, requires
from insolvia_api.api.dependencies import dependencies
from insolvia_api.core.access_log import record_access
from insolvia_api.core.cases import (
    apply_changes,
    assign_case,
    case_json,
    create_case,
    parse_case_creation,
    parse_case_update,
    parse_list_limit,
)
from insolvia_api.core.ports import AccessLog, CaseStore

logger = logging.getLogger(__name__)

blueprint = Blueprint("cases", __name__)

# A case record is a handful of short fields. Anything larger is a mistake or
# an attack, and rejecting it before JSON parsing keeps both cheap.
MAX_REQUEST_BYTES = 64 * 1024


def _stores() -> tuple[CaseStore, AccessLog]:
    """The case store and its access log, or a loud failure.

    Both are Optional on ApiDependencies so the existing public-route tests can
    build one without them. In a deployed environment they are always present:
    entrypoints/api_lambda.py refuses to boot without them, exactly as it does
    for auth. Reaching this branch is a composition bug, so it raises rather
    than degrading — a case endpoint that quietly stopped recording access
    would be worse than one that stopped working.
    """
    deps = dependencies()
    if deps.case_store is None or deps.access_log is None:
        raise RuntimeError("case store and access log are not composed")
    return deps.case_store, deps.access_log


def _firm_store() -> FirmStore:
    """The firm store, for the one thing the case routes need it for directly:
    checking that an assignment names somebody in the caller's own firm.

    Everything else about firms reaches these routes through the resolved
    accessor, which is why this is a helper on two endpoints rather than a
    dependency of the module.
    """
    deps = dependencies()
    if deps.firm_store is None:
        raise RuntimeError("firm store is not composed")
    return deps.firm_store


def _json_body() -> dict[str, object]:
    if request.content_length and request.content_length > MAX_REQUEST_BYTES:
        raise ValidationError("request body exceeds 64 KiB")
    payload = request.get_json(silent=True)
    if not isinstance(payload, dict):
        raise ValidationError("request body must be a JSON object")
    return payload


@blueprint.post("/v1/cases")
@require_auth
@requires(CASES, ADD_EDIT)
def create_case_route() -> ResponseReturnValue:
    """Open a case for the caller's firm.

    The firm comes from the caller's resolved accessor and is never read from
    the body, so there is no request a client can make that creates a case in
    another firm.

    The creator is LINKED to the case in the same transaction. Without that a
    paralegal without `access_all_cases` would open a matter they cannot see,
    cannot list and cannot reach by id — indistinguishable, from the outside,
    from the request having failed. core/cases.create_case returns the pair.
    """
    store, access_log = _stores()
    draft = parse_case_creation(_json_body())
    accessor = current_accessor()

    case, assignment = create_case(
        draft, firm_id=accessor.firm_id, created_by=accessor.subject
    )
    store.create(case, assignment)
    access_log.record(
        record_access(case_id=case.id, principal=accessor.subject, action="case.create")
    )

    # GLBA: the case id and nothing about its contents. The FIRM id is not
    # logged either — a request log that accumulated tenant ids would be a
    # per-firm activity record sitting in CloudWatch.
    logger.info("case created", extra={"case_id": case.id})
    return jsonify(case_json(case)), 201


@blueprint.get("/v1/cases")
@require_auth
@requires(CASES, VIEW_ONLY)
def list_cases_route() -> ResponseReturnValue:
    """The cases the caller may see, newest first.

    WHICH CASES THAT IS depends on the caller: a firm admin or anyone with
    `access_all_cases` gets the whole firm's, and everyone else gets the
    matters they are linked to. The store picks the index; see
    CaseStore.list_for_accessor.

    A cursor is only valid against the listing that minted it, so flipping a
    user's `access_all_cases` mid-pagination answers 400 rather than silently
    skipping the cases in between.

    Deliberately NOT written to the access log. That table is keyed by case,
    and a list touches no case in particular; the question it exists to answer
    is "who saw this file". Recording enumeration properly wants the
    by-principal index that infra/modules/case_store defers, and a sentinel
    partition here would be a worse answer than none.
    """
    store, _ = _stores()
    limit = parse_list_limit(request.args.get("limit"))
    cursor = request.args.get("cursor") or None

    page = store.list_for_accessor(current_accessor(), limit=limit, cursor=cursor)

    body: dict[str, object] = {"cases": [case_json(case) for case in page.cases]}
    # Absent rather than null when there is no next page — the client contract
    # distinguishes the two.
    if page.next_cursor is not None:
        body["nextCursor"] = page.next_cursor
    return jsonify(body), 200


@blueprint.get("/v1/cases/<case_id>")
@require_auth
@requires(CASES, VIEW_ONLY)
def get_case_route(case_id: str) -> ResponseReturnValue:
    """One case, if the caller may see it.

    Another firm's case answers 404, identically to one that does not exist —
    and so does the caller's OWN firm's case that they are not linked to. All
    three are the same answer on purpose: see core/errors.py's NotFoundError,
    and note that the third is not only about enumeration. Distinguishing "not
    linked" from "no such case" would tell any member of a firm which matters
    exist and which colleagues are on them, which is the thing per-case linking
    is for.

    The refused read IS recorded: someone walking case ids is exactly what the
    access log should show.
    """
    store, access_log = _stores()
    accessor = current_accessor()

    case = store.get(case_id, accessor=accessor)
    access_log.record(
        record_access(
            case_id=case_id,
            principal=accessor.subject,
            action="case.read",
            outcome="allowed" if case is not None else "denied",
        )
    )
    if case is None:
        raise NotFoundError("case not found")
    return jsonify(case_json(case)), 200


@blueprint.patch("/v1/cases/<case_id>")
@require_auth
@requires(CASES, ADD_EDIT)
def update_case_route(case_id: str) -> ResponseReturnValue:
    """Change a case's chapter, district or status.

    Read-modify-write. The read applies the whole access rule; the store's
    conditional write closes the gap between the two, so a case cannot move
    firms out from under the caller between the read and the write.

    A `view_only` caller never reaches here — `@requires` refuses with 403,
    which is the right answer rather than a 404: they can see this case, and
    telling them it does not exist while it sits in their own listing would be
    a lie their client cannot act on.
    """
    store, access_log = _stores()
    changes = parse_case_update(_json_body())
    accessor = current_accessor()

    existing = store.get(case_id, accessor=accessor)
    updated = (
        None if existing is None else store.update(apply_changes(existing, changes))
    )

    access_log.record(
        record_access(
            case_id=case_id,
            principal=accessor.subject,
            action="case.update",
            outcome="allowed" if updated is not None else "denied",
        )
    )
    if updated is None:
        raise NotFoundError("case not found")

    logger.info("case updated", extra={"case_id": updated.id})
    return jsonify(case_json(updated)), 200


# ── Assignment: who in the firm is on this matter ───────────────
#
# These live here rather than in routes/firm.py because the resource is the
# CASE. Every one of them resolves the case through `store.get` first, which
# applies the whole access rule — so a caller who cannot see a matter cannot
# discover who is on it, and cannot put themselves on it either.
#
# WHO MAY CHANGE AN ASSIGNMENT is `cases: add_edit` rather than
# `firm_administration`, and that is a product decision worth stating. Linking
# a colleague to a matter is case work — the attorney running it does it, not
# whoever manages the firm's user accounts. An admin can do it too, because an
# admin can do everything; they just are not the only one.


@blueprint.get("/v1/cases/<case_id>/assignees")
@require_auth
@requires(CASES, VIEW_ONLY)
def list_assignees_route(case_id: str) -> ResponseReturnValue:
    """Who is linked to this case, oldest link first.

    Subjects, not names: turning one into a person is GET /v1/firm/directory's
    job, and duplicating the display name here would be a copy that goes stale
    the moment somebody is renamed.
    """
    store, _ = _stores()
    accessor = current_accessor()

    if store.get(case_id, accessor=accessor) is None:
        raise NotFoundError("case not found")

    return jsonify(
        {
            "assignees": [
                {
                    "subject": a.subject,
                    "assignedAt": a.assigned_at,
                    "assignedBy": a.assigned_by,
                }
                for a in store.assignees(case_id)
            ]
        }
    ), 200


@blueprint.put("/v1/cases/<case_id>/assignees/<subject>")
@require_auth
@requires(CASES, ADD_EDIT)
def assign_case_route(case_id: str, subject: str) -> ResponseReturnValue:
    """Link a colleague to this case.

    PUT rather than POST because it is idempotent — the firm-admin UI cannot
    tell whether its first request landed, and re-linking somebody already on
    the matter must succeed rather than 409.

    THE SUBJECT MUST BE SOMEBODY IN THE CALLER'S FIRM, checked here against the
    firm store. Without that check this endpoint writes an assignment row for
    an arbitrary Cognito subject — which grants nothing today, because
    `may_see_case` tests the firm first and a stranger has no firm — but it
    would be a row in our case table naming a person who is not our tenant's,
    and it would put them on the case the moment they joined some firm.
    A 404, not a 403: a subject in another firm and a subject that does not
    exist are the same answer, or this becomes a probe for who works where.
    """
    store, access_log = _stores()
    accessor = current_accessor()
    firm_store = _firm_store()

    case = store.get(case_id, accessor=accessor)
    if case is None:
        raise NotFoundError("case not found")

    colleague = firm_store.get_user(accessor.firm_id, subject)
    if colleague is None or colleague.status != "active":
        raise NotFoundError("firm user not found")

    store.assign(assign_case(case, subject=subject, assigned_by=accessor.subject))
    # Recorded as an update to the case, because it is one: it changes who may
    # read the file. The actor is the person doing the linking, which is the
    # question this log exists to answer.
    access_log.record(
        record_access(case_id=case_id, principal=accessor.subject, action="case.update")
    )
    logger.info("case assignee added", extra={"case_id": case_id})
    return "", 204


@blueprint.delete("/v1/cases/<case_id>/assignees/<subject>")
@require_auth
@requires(CASES, ADD_EDIT)
def unassign_case_route(case_id: str, subject: str) -> ResponseReturnValue:
    """Unlink a colleague from this case.

    UNLINKING THE LAST PERSON IS ALLOWED, unlike removing a firm's last admin,
    and the asymmetry is the point. A case with nobody on it is still the
    firm's: its admins and anyone with `access_all_cases` still reach it, so
    the firm can always assign somebody new. A firm with no admin has no such
    route back, which is why that one is refused.

    A caller CAN unlink themselves, and then lose access to the case they were
    just editing. That is the honest consequence of "I am no longer on this
    matter" and the alternative — refusing it — would leave someone unable to
    hand a case over without asking an admin.
    """
    store, access_log = _stores()
    accessor = current_accessor()

    if store.get(case_id, accessor=accessor) is None:
        raise NotFoundError("case not found")
    if not store.unassign(case_id, subject):
        raise NotFoundError("assignee not found")

    access_log.record(
        record_access(case_id=case_id, principal=accessor.subject, action="case.update")
    )
    logger.info("case assignee removed", extra={"case_id": case_id})
    return "", 204
