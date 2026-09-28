"""The firm's client directory (ADR 0022 / #353): list, create, read, edit
and archive under `/v1/firm/clients`, gated by the `clients` feature.

It mirrors `routes/library_creditors.py` on purpose — no firm id in any URL
(the caller's firm is the only firm), whole-record POST/PUT, snake_case wire,
the same anti-oracle 404 for another firm's id — and differs from it in three
ways, each of them ADR 0022's:

- **No DELETE.** Archiving is a status write, `PUT .../status`, and a client
  with cases can never be deleted at all. Deleting one that has no cases is
  #355's to add.

`GET .../<id>/cases` is the reverse edge — the client's cases, read from the
case table's `by-client` index and filtered per case (ADR 0022's PR 2).
- **Access-logged.** A client record is PII with no case to be logged under,
  so single-record reads and every write append a `client.*` row keyed
  `CLIENT#<id>` (`insolvia_core.access_log`). The LIST is not logged,
  matching `GET /v1/cases`.
- **Server-owned fields.** `status`, `created_by` and the tax-id pointer are
  never taken from a body (`insolvia_core.firm_clients` owns why).

Reaching a client is same firm AND `clients >= view_only`; the one route here
that lists cases also needs `cases >= view_only` and applies
`access.may_see_case` to every case it returns.
"""

from __future__ import annotations

import logging

from flask import Blueprint, jsonify, request
from flask.typing import ResponseReturnValue
from insolvia_core.access_log import record_access
from insolvia_core.cases import case_json
from insolvia_core.errors import NotFoundError, ValidationError
from insolvia_core.firm_clients import (
    FirmClient,
    create_firm_client,
    firm_client_json,
    parse_client_status,
    parse_firm_client,
    replace_firm_client,
    set_firm_client_status,
)
from insolvia_core.firms import ADD_EDIT, CASES, CLIENTS, VIEW_ONLY
from insolvia_core.ports import AccessLog, FirmStore

from insolvia_api.api.auth import current_accessor, require_auth, requires
from insolvia_api.api.dependencies import dependencies

logger = logging.getLogger(__name__)

blueprint = Blueprint("firm_clients", __name__)

# A name, two addresses, a handful of aliases and a few short fields — the
# library creditor's order of magnitude.
MAX_REQUEST_BYTES = 32 * 1024

_NOT_FOUND = "client not found"


def _stores() -> tuple[FirmStore, AccessLog]:
    deps = dependencies()
    if deps.firm_store is None or deps.access_log is None:
        raise RuntimeError("firm store or access log is not composed")
    return deps.firm_store, deps.access_log


def _json_body() -> dict[str, object]:
    if request.content_length and request.content_length > MAX_REQUEST_BYTES:
        raise ValidationError("request body exceeds 32 KiB")
    payload = request.get_json(silent=True)
    if not isinstance(payload, dict):
        raise ValidationError("request body must be a JSON object")
    return payload


def _log(access_log: AccessLog, client_id: str, action: str, *, found: bool) -> None:
    access_log.record(
        record_access(
            client_id=client_id,
            principal=current_accessor().subject,
            action=action,
            outcome="allowed" if found else "denied",
        )
    )


def _write(
    store: FirmStore, access_log: AccessLog, client: FirmClient
) -> ResponseReturnValue:
    """Write an edited client back and log it. A row removed or moved between
    the route's read and this write answers the same 404 as an id that never
    existed, rather than being resurrected."""
    written = store.update_client(client)
    _log(access_log, client.id, "client.update", found=written is not None)
    if written is None:
        raise NotFoundError(_NOT_FOUND)
    return jsonify(firm_client_json(written)), 200


@blueprint.get("/v1/firm/clients")
@require_auth
@requires(CLIENTS, VIEW_ONLY)
def list_clients_route() -> ResponseReturnValue:
    store, _ = _stores()
    clients = store.list_clients(current_accessor().firm_id)
    return jsonify({"clients": [firm_client_json(c) for c in clients]}), 200


@blueprint.post("/v1/firm/clients")
@require_auth
@requires(CLIENTS, ADD_EDIT)
def create_client_route() -> ResponseReturnValue:
    store, access_log = _stores()
    accessor = current_accessor()
    draft = parse_firm_client(_json_body())
    client = create_firm_client(
        draft, firm_id=accessor.firm_id, created_by=accessor.subject
    )
    store.create_client(client)
    _log(access_log, client.id, "client.create", found=True)

    # GLBA: no name, no address — that a firm added SOMEONE is all a request
    # log needs.
    logger.info("client created")
    return jsonify(firm_client_json(client)), 201


@blueprint.get("/v1/firm/clients/<client_id>")
@require_auth
@requires(CLIENTS, VIEW_ONLY)
def get_client_route(client_id: str) -> ResponseReturnValue:
    """One client. The refused read IS logged, as `GET /v1/cases/<id>` logs
    one: someone walking client ids is what the access log should show."""
    store, access_log = _stores()
    client = store.get_client(current_accessor().firm_id, client_id)
    _log(access_log, client_id, "client.read", found=client is not None)
    if client is None:
        raise NotFoundError(_NOT_FOUND)
    return jsonify(firm_client_json(client)), 200


@blueprint.get("/v1/firm/clients/<client_id>/cases")
@require_auth
@requires(CLIENTS, VIEW_ONLY)
@requires(CASES, VIEW_ONLY)
def list_client_cases_route(client_id: str) -> ResponseReturnValue:
    """The client's cases THAT THE CALLER MAY SEE, newest first, each with
    the role the client holds on it (ADR 0022).

    `{"cases": [{"filing_role": "debtor_1", "case": {…case_json…}}]}` — the
    case in `GET /v1/cases/<id>`'s own shape, so a client reads it with the
    decoder it already has. A joint case appears in each of its clients'
    lists, once in each.

    FILTERED PER CASE, AND SILENT ABOUT THE REST. Every case goes through
    `access.may_see_case` (the store applies it, as `CaseStore.get` does),
    and one the caller may not see is simply absent — no count, no "N more".
    A count would tell a paralegal which clients have matters they are not
    on, which is exactly what ADR 0009's 404 hides.

    The client itself is resolved first, in the caller's firm, and the read
    is access-logged as a `client.read` (refused ones `denied`): another
    firm's id and an unknown one are the same 404.
    """
    store, access_log = _stores()
    accessor = current_accessor()
    client = store.get_client(accessor.firm_id, client_id)
    _log(access_log, client_id, "client.read", found=client is not None)
    if client is None:
        raise NotFoundError(_NOT_FOUND)
    case_store = dependencies().case_store
    if case_store is None:
        raise RuntimeError("case store is not composed")
    entries = case_store.list_for_client(client.id, accessor=accessor)
    return jsonify(
        {
            "cases": [
                {"filing_role": entry.filing_role, "case": case_json(entry.case)}
                for entry in entries
            ]
        }
    ), 200


@blueprint.put("/v1/firm/clients/<client_id>")
@require_auth
@requires(CLIENTS, ADD_EDIT)
def update_client_route(client_id: str) -> ResponseReturnValue:
    """Replace a client's whole editable record. Keeps the status, the
    tax-id pointer and the `created_*` stamps; see `firm_clients`.

    Editing a client never touches a case: once cases copy from clients
    (ADR 0022's PR 2) the copy keeps its own values, and divergence is
    shown, not prevented."""
    store, access_log = _stores()
    draft = parse_firm_client(_json_body())
    existing = store.get_client(current_accessor().firm_id, client_id)
    if existing is None:
        _log(access_log, client_id, "client.update", found=False)
        raise NotFoundError(_NOT_FOUND)
    response = _write(store, access_log, replace_firm_client(existing, draft))
    logger.info("client updated")
    return response


@blueprint.put("/v1/firm/clients/<client_id>/status")
@require_auth
@requires(CLIENTS, ADD_EDIT)
def set_client_status_route(client_id: str) -> ResponseReturnValue:
    """Archive a client (`{"status": "archived"}`), or bring one back
    (`"active"`). A status write, never a delete — ADR 0022. Logged as a
    `client.update`: archiving is an edit to the record, not a new verb."""
    store, access_log = _stores()
    status = parse_client_status(_json_body())
    existing = store.get_client(current_accessor().firm_id, client_id)
    if existing is None:
        _log(access_log, client_id, "client.update", found=False)
        raise NotFoundError(_NOT_FOUND)
    response = _write(store, access_log, set_firm_client_status(existing, status))
    logger.info("client status changed", extra={"status": status})
    return response
