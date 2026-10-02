"""The firm's client-questionnaire config (ADR 0023 PR 3 / #362): read,
save and reset under `/v1/firm/questionnaire`.

STAFF ROUTES. What a firm's CLIENT reads is `GET /v1/portal/questionnaire`
in `routes/portal.py`, through the portal's own decorator — the two
principal classes never share a route (the disjointness test in
`tests/unit/test_portal_routes.py`).

GATED AS FIRM SETTINGS ARE: `firm_administration` at view_only to read and
add_edit to save or reset, exactly `routes/firm.py`'s `/v1/firm`. Which
sections a firm's clients see is a firm-wide setting, not a facet of any one
case, so it belongs to whoever administers the firm — not to `client_portal`,
which is the act of inviting a client to one case.

THE STAFF VIEW IS EVERY SECTION, switched off or not, which is what "staff
still can" means in the ADR's done-when: switching a section off hides it
from clients and from nothing else. `insolvia_core.questionnaire` owns the
catalogue, the whole-record save and why reset deletes rather than writes.

Scoped to the caller's firm with no firm id in the URL, `routes/firm.py`'s
rule. No access-log row: the log is per case (`record_access` takes a
`case_id`) and this is a firm setting; the request log line carries the
route, and the stored item carries who saved it and when.
"""

from __future__ import annotations

import logging

from flask import Blueprint, jsonify, request
from flask.typing import ResponseReturnValue
from insolvia_core.errors import ValidationError
from insolvia_core.firms import ADD_EDIT, FIRM_ADMINISTRATION, VIEW_ONLY
from insolvia_core.ports import FirmStore
from insolvia_core.questionnaire import (
    build_config,
    parse_questionnaire_update,
    questionnaire_json,
)

from insolvia_api.api.auth import current_accessor, require_auth, requires
from insolvia_api.api.dependencies import dependencies

logger = logging.getLogger(__name__)

blueprint = Blueprint("firm_questionnaire", __name__)

# Six sections, each a flag and at most 2,000 characters of instructions —
# up to four bytes a character in UTF-8, with room for the JSON around them.
MAX_REQUEST_BYTES = 64 * 1024


def _firm_store() -> FirmStore:
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


@blueprint.get("/v1/firm/questionnaire")
@require_auth
@requires(FIRM_ADMINISTRATION, VIEW_ONLY)
def read_questionnaire_route() -> ResponseReturnValue:
    """Every section, with the firm's switch and instructions beside the
    defaults. A firm that never saved reads the defaults, `isDefault: true`."""
    accessor = current_accessor()
    config = _firm_store().get_questionnaire(accessor.firm_id)
    return jsonify(questionnaire_json(config)), 200


@blueprint.put("/v1/firm/questionnaire")
@require_auth
@requires(FIRM_ADMINISTRATION, ADD_EDIT)
def save_questionnaire_route() -> ResponseReturnValue:
    """Save the whole config — every section, each with its switch and its
    instructions. Answers with the staff view of what was saved, so the
    screen renders the server's normalisation (blank instructions read back
    as the default) rather than what it sent."""
    accessor = current_accessor()
    settings = parse_questionnaire_update(_json_body())
    config = build_config(
        settings, firm_id=accessor.firm_id, updated_by=accessor.subject
    )
    _firm_store().put_questionnaire(config)
    logger.info("questionnaire config saved")
    return jsonify(questionnaire_json(config)), 200


@blueprint.delete("/v1/firm/questionnaire")
@require_auth
@requires(FIRM_ADMINISTRATION, ADD_EDIT)
def reset_questionnaire_route() -> ResponseReturnValue:
    """Reset to defaults: delete the stored config. Idempotent — a firm
    already on the defaults gets the same 200 and the same body, because
    either way that is where the firm now is."""
    accessor = current_accessor()
    removed = _firm_store().delete_questionnaire(accessor.firm_id)
    if removed:
        logger.info("questionnaire config reset to defaults")
    return jsonify(questionnaire_json(None)), 200
