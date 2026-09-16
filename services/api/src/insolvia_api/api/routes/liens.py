"""`GET /v1/cases/{caseId}/liens` — what each secured claim is secured by,
and what each asset carries (issue #345).

THE CLAIM READ PATH FOR THE DERIVED FIGURES. The generic collection routes
never special-case a kind (routes/case_entities.py says why), so a claim's
secured and unsecured portions — arithmetic over the claim, its asset and
its senior liens — cannot ride on `GET .../claims/<id>`. They come from here
instead: one read of the assets and the claims, one pass of
`core/liens.py`, the same function B106D prints Column B from and the
summary reports. The intake screens fetch this beside a claim or an asset
and show the figures read-only; the client never adds them up (ADR 0001).

It is its own endpoint rather than a member of the summary because the
summary runs the whole completeness gate — every projection — and a form
under a preparer's cursor should not pay for that on every save. The
summary carries the same block for the overview; both are `liens_json`.

The URL shares its prefix with `/v1/cases/<id>/<collection>`; "liens" is a
static segment and not a registered collection, so Werkzeug's ranking keeps
them apart — the same note the summary and creditor-matrix routes carry.
"""

from __future__ import annotations

import logging

from flask import Blueprint, jsonify
from flask.typing import ResponseReturnValue
from insolvia_core.access_log import record_access
from insolvia_core.assets import ASSET
from insolvia_core.claims import CLAIM
from insolvia_core.errors import NotFoundError
from insolvia_core.firms import INTAKE, VIEW_ONLY
from insolvia_core.ports import AccessLog, CaseEntityStore, CaseStore

from insolvia_api.api.auth import current_accessor, require_auth, requires
from insolvia_api.api.dependencies import dependencies
from insolvia_api.core.form_projections import CaseFile
from insolvia_api.core.liens import derive_liens, liens_json

logger = logging.getLogger(__name__)

blueprint = Blueprint("liens", __name__)


def _stores() -> tuple[CaseStore, CaseEntityStore, AccessLog]:
    deps = dependencies()
    if (
        deps.case_store is None
        or deps.case_entity_store is None
        or deps.access_log is None
    ):
        raise RuntimeError("case store, entity store and access log are not composed")
    return deps.case_store, deps.case_entity_store, deps.access_log


@blueprint.get("/v1/cases/<case_id>/liens")
@require_auth
@requires(INTAKE, VIEW_ONLY)
def liens_route(case_id: str) -> ResponseReturnValue:
    """The lien figures of one case.

    `VIEW_ONLY` on `INTAKE`, like the collection listings this is a read
    over: it reveals the claims and the assets, which is what the intake
    permission already grants. Reachability is resolved under the caller's
    accessor and logged either way, and an unreachable case answers the
    undistinguishing 404 the id-oracle rule requires.
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

    # Only the two collections the arithmetic reads — not `read_case_data`,
    # which reads every collection for the gate this route does not run.
    case_file = CaseFile(
        case=case,
        assets=tuple(
            (e.id, e.body) for e in entity_store.list_for_case(case_id, ASSET)
        ),
        claims=tuple(
            (e.id, e.body) for e in entity_store.list_for_case(case_id, CLAIM)
        ),
    )
    return jsonify(liens_json(derive_liens(case_file))), 200
