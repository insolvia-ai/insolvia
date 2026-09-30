"""The client portal's own routes (ADR 0023) — what a CLIENT reaches.

EVERY ROUTE HERE CARRIES `@require_client` AND NOTHING ELSE AUTHENTICATES
THEM. tests/unit/test_portal_routes.py walks the whole URL map to hold that
down from both sides: every `/v1/portal/` rule admits the client class only,
and no other rule admits it at all.

NO ROUTE HERE NAMES A CASE. The case is the binding's, resolved from the
verified token — there is no case id for a client to guess, and nothing to
probe for another firm's (ADR 0023's threat model). When ADR 0022 moves the
binding from the case to a client record, nothing a client calls changes.

Reads go through projections that take the `ClientAccessor`, never
`CaseStore.get`: a client may read the firm's name and — from the routes
that follow — the questionnaire, their own candidates and their document
requests, and never the confirmed case record.
"""

from __future__ import annotations

from flask import Blueprint, jsonify
from flask.typing import ResponseReturnValue
from insolvia_core.access import ClientAccessor
from insolvia_core.access_log import record_access
from insolvia_core.clients import (
    CasePublicStatus,
    public_status_json,
)
from insolvia_core.errors import ForbiddenError

from insolvia_api.api.client_auth import current_client, require_client
from insolvia_api.api.dependencies import dependencies

blueprint = Blueprint("portal", __name__)


def portal_me_json(
    client: ClientAccessor, status: CasePublicStatus
) -> dict[str, object]:
    """Who the portal says you are — the fixed read policy's smallest piece.

    The name is the one the FIRM gave at invitation (the binding's), not
    anything the token carries; the firm block is its name alone; the case
    block is the public status — chapter and stage — and deliberately no case
    id: see the module docstring.
    """
    return {
        "subject": client.subject,
        "displayName": client.binding.display_name,
        "roles": list(client.roles),
        "firm": {"name": client.firm.name},
        "case": public_status_json(status),
    }


def _public_status(client: ClientAccessor) -> CasePublicStatus:
    """The bound case's chapter and stage, through the projection that takes
    the binding (`CaseStore.public_status`) — never `CaseStore.get`, which
    takes a staff `Accessor` and returns the whole record.

    A live binding whose case is gone is a 403, the same refusal as no
    binding at all: there is no case to reach through the portal, and which
    of the two it is belongs to the firm."""
    store = dependencies().case_store
    if store is None:
        raise RuntimeError("case store is not composed")
    status = store.public_status(client)
    if status is None:
        raise ForbiddenError(
            "you do not have access to a case through the client portal"
        )
    return status


@blueprint.get("/v1/portal/me")
@require_client
def portal_me_route() -> ResponseReturnValue:
    """The portal's "is my session good, and whose portal is this?" probe.

    Recorded as `portal.read` against the binding's case, with the client as
    principal — every portal request is on the access log (ADR 0023 § Audit).
    """
    client = current_client()
    access_log = dependencies().access_log
    if access_log is None:
        raise RuntimeError("access log is not composed")
    access_log.record(
        record_access(
            case_id=client.case_id, principal=client.subject, action="portal.read"
        )
    )
    return jsonify(portal_me_json(client, _public_status(client))), 200
