"""`GET /v1/cases/<id>/filing-set` — the filing set and hand-off checklist
for the case's district (ADR 0024 build PR 3; core/filing_set.py).

Same access and permission path as the forms hub and the packet list:
`VIEW_ONLY` on `CASES`, the case resolved under the caller's accessor, the
read recorded either way and an undistinguishing 404 for a case the caller
cannot see. It writes nothing, renders nothing and reads no tax identifier —
it is a projection of records the caller can already read, the packet
listing among them.

The response is in the request because it is cheap: no PDF is opened here.
What the packet's files measured was recorded by the assembly worker on the
packet record (core/pdf_measure.py).
"""

from __future__ import annotations

from datetime import UTC, datetime

from flask import Blueprint, jsonify
from flask.typing import ResponseReturnValue
from insolvia_core import courts
from insolvia_core.access_log import record_access
from insolvia_core.errors import NotFoundError
from insolvia_core.firms import CASES, VIEW_ONLY

from insolvia_api.api.auth import current_accessor, require_auth, requires
from insolvia_api.api.dependencies import dependencies
from insolvia_api.core.filing_set import (
    ChecklistItem,
    FilingDocument,
    FilingSet,
    build_filing_set,
)
from insolvia_api.core.packet_assembly import read_case_data

blueprint = Blueprint("filing_set", __name__)


def _document_json(document: FilingDocument) -> dict[str, object]:
    body: dict[str, object] = {
        "key": document.key,
        "title": document.title,
        "fileName": document.file_name,
        "source": document.source,
        "handling": document.handling,
        "checks": [
            {"check": c.check, "outcome": c.outcome, "message": c.message}
            for c in document.checks
        ],
    }
    if document.note:
        body["note"] = document.note
    return body


def _item_json(item: ChecklistItem) -> dict[str, object]:
    body: dict[str, object] = {
        "id": item.id,
        "status": item.status,
        "title": item.title,
        "detail": item.detail,
    }
    if item.link is not None:
        body["link"] = item.link
    return body


def filing_set_json(filing_set: FilingSet) -> dict[str, object]:
    """The wire shape. Optional keys absent, never null (the matrix route's
    rule). Nothing here can carry a tax identifier: documents are named and
    measured, never read."""
    body: dict[str, object] = {
        "registryRelease": filing_set.registry_release,
        "filingMethod": filing_set.filing_method,
        "orderBasis": filing_set.order_basis,
        "namesBasis": filing_set.names_basis,
        "maxBytes": filing_set.max_bytes,
        "maxBytesBasis": filing_set.max_bytes_basis,
        "documents": [_document_json(d) for d in filing_set.documents],
        "checklist": [_item_json(i) for i in filing_set.checklist],
    }
    if filing_set.court_code is not None and filing_set.court_name is not None:
        court: dict[str, object] = {
            "code": filing_set.court_code,
            "name": filing_set.court_name,
        }
        if filing_set.division_name is not None:
            court["divisionName"] = filing_set.division_name
        body["court"] = court
    if filing_set.packet is not None:
        body["packet"] = {
            "id": filing_set.packet.id,
            "createdAt": filing_set.packet.created_at,
        }
    return body


@blueprint.get("/v1/cases/<case_id>/filing-set")
@require_auth
@requires(CASES, VIEW_ONLY)
def filing_set_route(case_id: str) -> ResponseReturnValue:
    deps = dependencies()
    if (
        deps.case_store is None
        or deps.debtor_store is None
        or deps.case_entity_store is None
        or deps.packet_store is None
        or deps.access_log is None
    ):
        raise RuntimeError(
            "case store, debtor store, entity store, packet store and access"
            " log are not composed"
        )
    accessor = current_accessor()
    case = deps.case_store.get(case_id, accessor=accessor)
    deps.access_log.record(
        record_access(
            case_id=case_id,
            principal=accessor.subject,
            action="case.read",
            outcome="allowed" if case is not None else "denied",
        )
    )
    if case is None:
        raise NotFoundError("case not found")

    data = read_case_data(
        case, debtor_store=deps.debtor_store, entity_store=deps.case_entity_store
    )
    today = datetime.now(UTC).date()
    filing_set = build_filing_set(
        data,
        packets=deps.packet_store.list_for_case(case_id),
        release=courts.resolve(today),
        as_of=today,
    )
    return jsonify(filing_set_json(filing_set)), 200
