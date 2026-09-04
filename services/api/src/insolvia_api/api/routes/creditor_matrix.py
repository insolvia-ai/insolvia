"""GET /v1/cases/<case_id>/creditor-matrix — the court's mailing list, or
every reason it cannot be produced yet (issue #94).

A synchronous endpoint on purpose: ADR 0015 keeps fast, deterministic work in
the lambdalith, and rendering a consumer case's creditor list into a few
kilobytes of text is exactly that. The generation itself is the pure function
in core/creditor_matrix.py, which 9.6's packet worker will import directly —
this route is only authorisation plus JSON.

The answer is 200 either way — a file, or a problem list — because both are
the same successful act: "generate the matrix" ran, and its outcome is the
resource. Refusing with a 4xx would make the problem report an error body,
and the problem report is precisely what the intake UI renders next to each
creditor so an attorney can fix the list.

The URL shares its prefix with the generic /v1/cases/<id>/<collection> routes
(api/routes/case_entities.py). It cannot be shadowed by them: Werkzeug ranks
static URL segments above dynamic ones, and "creditor-matrix" is not a
registered collection anyway.
"""

from __future__ import annotations

import logging

from flask import Blueprint, jsonify
from flask.typing import ResponseReturnValue
from insolvia_core.access_log import record_access
from insolvia_core.creditors import CREDITOR
from insolvia_core.errors import NotFoundError
from insolvia_core.firms import INTAKE, VIEW_ONLY
from insolvia_core.ports import AccessLog, CaseEntityStore, CaseStore

from insolvia_api.api.auth import current_accessor, require_auth, requires
from insolvia_api.api.dependencies import dependencies
from insolvia_api.core.creditor_matrix import generate_creditor_matrix, matrix_json

logger = logging.getLogger(__name__)

blueprint = Blueprint("creditor_matrix", __name__)


def _stores() -> tuple[CaseStore, CaseEntityStore, AccessLog]:
    deps = dependencies()
    if (
        deps.case_store is None
        or deps.case_entity_store is None
        or deps.access_log is None
    ):
        raise RuntimeError("case store, entity store and access log are not composed")
    return deps.case_store, deps.case_entity_store, deps.access_log


@blueprint.get("/v1/cases/<case_id>/creditor-matrix")
@require_auth
@requires(INTAKE, VIEW_ONLY)
def creditor_matrix_route(case_id: str) -> ResponseReturnValue:
    """Generate the matrix from the case's creditor records.

    VIEW_ONLY, not ADD_EDIT: generation writes nothing — it is a projection of
    records the caller can already read, exactly like listing the collection.

    The case lookup is the only authorisation there is, the same rule as every
    entity route: the entity store enforces nothing, and a foreign or unknown
    case answers the same 404 (the id-oracle argument in core/errors.py).
    """
    case_store, entity_store, access_log = _stores()
    accessor = current_accessor()

    case = case_store.get(case_id, accessor=accessor)
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

    creditors = entity_store.list_for_case(case_id, CREDITOR)
    matrix = generate_creditor_matrix(creditors)
    # GLBA: the case id and whether a file was produced. Never a creditor
    # name, a count, or a problem message — even the number of creditors
    # leaks how large somebody's schedule is.
    logger.info(
        "creditor matrix generated",
        extra={"case_id": case_id, "generated": matrix.content is not None},
    )
    return jsonify(matrix_json(matrix)), 200
