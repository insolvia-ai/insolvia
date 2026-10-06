"""Document request checklists, staff side (ADR 0023 PR 5 / #364).

Two halves, gated as their scope is:

THE FIRM'S CHECKLIST under `/v1/firm/document-checklist` — read, save,
reset — is a firm setting, gated exactly as `/v1/firm/questionnaire` is:
`firm_administration` at view_only to read and add_edit to save or reset. No
access-log row, that route's reason: the log is per case and this is not.

A CASE'S REQUESTS under `/v1/cases/<case_id>/document-requests` are part of
the case's document work, so they ride the `documents` feature — view_only
to read, add_edit to change — and every request resolves the case through
`CaseStore.get` first and logs it (`case.read` / `case.update`, as tasks
do: a request is a working record of the case, not a disclosure of a
document). Applying the firm's checklist to a case is the explicit staff act
`POST .../document-requests/from-checklist`; `insolvia_core.document_requests`
says why it is not done on case open.

What the CLIENT reads and uploads against is `routes/portal_documents.py`,
through the portal's own decorator — the two principal classes never share a
route. A request becomes `received` only through a completed upload naming
it (`routes/documents.py::satisfy_request`), never through PATCH.
"""

from __future__ import annotations

import logging

from flask import Blueprint, jsonify, request
from flask.typing import ResponseReturnValue
from insolvia_core.access import Accessor
from insolvia_core.access_log import record_access
from insolvia_core.document_requests import (
    build_checklist,
    checklist_json,
    create_request,
    parse_checklist_update,
    parse_request_creation,
    parse_request_update,
    request_json,
    requests_from_checklist,
    requests_json,
    resolve_checklist,
    set_status,
)
from insolvia_core.errors import NotFoundError, ValidationError
from insolvia_core.firms import (
    ADD_EDIT,
    DOCUMENTS,
    FIRM_ADMINISTRATION,
    VIEW_ONLY,
)
from insolvia_core.ports import AccessLog, CaseStore, DocumentRequestStore, FirmStore

from insolvia_api.api.auth import current_accessor, require_auth, requires
from insolvia_api.api.dependencies import dependencies

logger = logging.getLogger(__name__)

blueprint = Blueprint("document_requests", __name__)

# Forty entries of a 120-character title and a 1,000-character description,
# up to four bytes a character, with room for the JSON around them.
MAX_REQUEST_BYTES = 256 * 1024


def _json_body() -> dict[str, object]:
    if request.content_length and request.content_length > MAX_REQUEST_BYTES:
        raise ValidationError("request body exceeds 256 KiB")
    payload = request.get_json(silent=True)
    if not isinstance(payload, dict):
        raise ValidationError("request body must be a JSON object")
    return payload


def _firm_store() -> FirmStore:
    store = dependencies().firm_store
    if store is None:
        raise RuntimeError("firm store is not composed")
    return store


def _case_stores() -> tuple[CaseStore, DocumentRequestStore, AccessLog]:
    deps = dependencies()
    if (
        deps.case_store is None
        or deps.document_request_store is None
        or deps.access_log is None
    ):
        raise RuntimeError("the document request stores are not composed")
    return deps.case_store, deps.document_request_store, deps.access_log


def _reachable_case_or_404(accessor: Accessor, case_id: str, action: str) -> None:
    """Resolve the case first and record the attempt either way — the
    pattern every case-scoped route module repeats (routes/tasks.py says why
    it is repeated rather than shared)."""
    case_store, _, access_log = _case_stores()
    case = case_store.get(case_id, accessor=accessor)
    access_log.record(
        record_access(
            case_id=case_id,
            principal=accessor.subject,
            action=action,
            outcome="allowed" if case is not None else "denied",
        )
    )
    if case is None:
        raise NotFoundError("case not found")


# ── The firm's checklist ────────────────────────────────────────────


@blueprint.get("/v1/firm/document-checklist")
@require_auth
@requires(FIRM_ADMINISTRATION, VIEW_ONLY)
def read_checklist_route() -> ResponseReturnValue:
    """The checklist in force and the shipped default beside it. A firm that
    never saved reads the default, `isDefault: true`."""
    accessor = current_accessor()
    return jsonify(
        checklist_json(_firm_store().get_document_checklist(accessor.firm_id))
    ), 200


@blueprint.put("/v1/firm/document-checklist")
@require_auth
@requires(FIRM_ADMINISTRATION, ADD_EDIT)
def save_checklist_route() -> ResponseReturnValue:
    """Save the whole list. Answers with what was stored."""
    accessor = current_accessor()
    items = parse_checklist_update(_json_body())
    checklist = build_checklist(
        items, firm_id=accessor.firm_id, updated_by=accessor.subject
    )
    _firm_store().put_document_checklist(checklist)
    logger.info("document checklist saved", extra={"items": len(items)})
    return jsonify(checklist_json(checklist)), 200


@blueprint.delete("/v1/firm/document-checklist")
@require_auth
@requires(FIRM_ADMINISTRATION, ADD_EDIT)
def reset_checklist_route() -> ResponseReturnValue:
    """Reset to the shipped default: delete the stored list. Idempotent."""
    accessor = current_accessor()
    if _firm_store().delete_document_checklist(accessor.firm_id):
        logger.info("document checklist reset to the default")
    return jsonify(checklist_json(None)), 200


# ── A case's requests ───────────────────────────────────────────────


@blueprint.get("/v1/cases/<case_id>/document-requests")
@require_auth
@requires(DOCUMENTS, VIEW_ONLY)
def list_requests_route(case_id: str) -> ResponseReturnValue:
    """Every request of the case, in the order asked, with the progress the
    case overview shows — arrived against outstanding."""
    _, store, _ = _case_stores()
    _reachable_case_or_404(current_accessor(), case_id, "case.read")
    return jsonify(requests_json(store.list_for_case(case_id))), 200


@blueprint.post("/v1/cases/<case_id>/document-requests")
@require_auth
@requires(DOCUMENTS, ADD_EDIT)
def create_request_route(case_id: str) -> ResponseReturnValue:
    """Ask for one more document, outside the checklist."""
    _, store, _ = _case_stores()
    accessor = current_accessor()
    item = parse_request_creation(_json_body())
    _reachable_case_or_404(accessor, case_id, "case.update")
    made = create_request(item, case_id=case_id, created_by=accessor.subject)
    store.create(made)
    logger.info("document request created", extra={"case_id": case_id, "id": made.id})
    return jsonify(request_json(made)), 201


@blueprint.post("/v1/cases/<case_id>/document-requests/from-checklist")
@require_auth
@requires(DOCUMENTS, ADD_EDIT)
def apply_checklist_route(case_id: str) -> ResponseReturnValue:
    """Request every entry of the firm's checklist the case has not already
    requested (by title, waived included). Safe to repeat: a second call adds
    nothing, and one after the firm extended its list adds the new entries.
    Answers with the whole list, its progress, and how many were added."""
    _, store, _ = _case_stores()
    accessor = current_accessor()
    _reachable_case_or_404(accessor, case_id, "case.update")
    checklist = resolve_checklist(
        _firm_store().get_document_checklist(accessor.firm_id)
    )
    added = requests_from_checklist(
        checklist,
        store.list_for_case(case_id),
        case_id=case_id,
        created_by=accessor.subject,
    )
    for made in added:
        store.create(made)
    logger.info(
        "document checklist applied", extra={"case_id": case_id, "added": len(added)}
    )
    return jsonify(
        {**requests_json(store.list_for_case(case_id)), "added": len(added)}
    ), 200


@blueprint.patch("/v1/cases/<case_id>/document-requests/<request_id>")
@require_auth
@requires(DOCUMENTS, ADD_EDIT)
def update_request_route(case_id: str, request_id: str) -> ResponseReturnValue:
    """Waive a request, or reopen one (`{"status": "waived" | "requested"}`).
    `received` is refused: only a completed upload receives a request."""
    _, store, _ = _case_stores()
    status = parse_request_update(_json_body())
    _reachable_case_or_404(current_accessor(), case_id, "case.update")
    stored = store.get(case_id, request_id)
    if stored is None:
        raise NotFoundError("document request not found")
    written = store.update(set_status(stored, status))
    if written is None:
        raise NotFoundError("document request not found")
    logger.info(
        "document request updated",
        extra={"case_id": case_id, "id": request_id, "status": status},
    )
    return jsonify(request_json(written)), 200


@blueprint.delete("/v1/cases/<case_id>/document-requests/<request_id>")
@require_auth
@requires(DOCUMENTS, ADD_EDIT)
def delete_request_route(case_id: str, request_id: str) -> ResponseReturnValue:
    """Withdraw a request entirely — asked in error, rather than not needed
    (that is a waiver, which stays on the record). The documents uploaded
    against it stay on the case; they were never the request's to delete."""
    _, store, _ = _case_stores()
    _reachable_case_or_404(current_accessor(), case_id, "case.update")
    if not store.delete(case_id, request_id):
        raise NotFoundError("document request not found")
    logger.info(
        "document request deleted", extra={"case_id": case_id, "id": request_id}
    )
    return "", 204
