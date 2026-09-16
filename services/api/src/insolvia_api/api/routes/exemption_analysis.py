"""`GET /v1/cases/{caseId}/exemption-analysis` — the Schedule C workbench's
read (issue #346): the statutes this case may claim under, what it has
claimed against each, and what every asset still leaves exposed.

ONE ENDPOINT, NOT `/exemptions`. The issue names `GET /v1/cases/<id>/exemptions`,
but that URL is already the generic collection route for the `exemption`
records themselves (core/case_collections.py registers `exemptions`), and a
static rule there would shadow the listing the panel relies on to add and
remove claims. So the registry table and the arithmetic ride together on
this sibling — the table is meaningless without the case's own claims drawn
against it, and computing both from one read of the records is what keeps
"available under this statute" and "unexempt on this asset" the same
answer.

The URL shares its prefix with `/v1/cases/<id>/<collection>`; the static
segment wins under Werkzeug's ranking — the note the summary, liens and
standards routes each carry.
"""

from __future__ import annotations

import logging
from datetime import date

from flask import Blueprint, jsonify
from flask.typing import ResponseReturnValue
from insolvia_core.access_log import record_access
from insolvia_core.assets import ASSET
from insolvia_core.claims import CLAIM
from insolvia_core.errors import NotFoundError
from insolvia_core.exemption_claims import EXEMPTION
from insolvia_core.firms import INTAKE, VIEW_ONLY
from insolvia_core.petitions import PETITION
from insolvia_core.ports import AccessLog, CaseEntityStore, CaseStore, DebtorStore
from insolvia_core.sofa import SOFA_ENTRY

from insolvia_api.api.auth import current_accessor, require_auth, requires
from insolvia_api.api.dependencies import dependencies
from insolvia_api.core.exemption_analysis import (
    ExemptionCase,
    analyse,
    analysis_json,
)

logger = logging.getLogger(__name__)

blueprint = Blueprint("exemption_analysis", __name__)


def _stores() -> tuple[CaseStore, DebtorStore, CaseEntityStore, AccessLog]:
    deps = dependencies()
    if (
        deps.case_store is None
        or deps.debtor_store is None
        or deps.case_entity_store is None
        or deps.access_log is None
    ):
        raise RuntimeError(
            "case store, debtor store, entity store and access log are not composed"
        )
    return deps.case_store, deps.debtor_store, deps.case_entity_store, deps.access_log


@blueprint.get("/v1/cases/<case_id>/exemption-analysis")
@require_auth
@requires(INTAKE, VIEW_ONLY)
def exemption_analysis_route(case_id: str) -> ResponseReturnValue:
    """The exemption figures of one case.

    `VIEW_ONLY` on `INTAKE`, like the liens route: it reveals the assets,
    the claims and the exemption records, which is what the intake
    permission already grants. Always 200 with whichever figures resolved
    and a `problems` list for the rest — a case with no debtor state or no
    election yet is early, not broken (the standards route's shape).
    Reachability is resolved under the caller's accessor and logged either
    way, and an unreachable case answers the undistinguishing 404.
    """
    case_store, debtor_store, entity_store, access_log = _stores()
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

    # Only the collections the arithmetic reads — not `read_case_data`,
    # which reads every collection for the gate this route does not run.
    petitions = entity_store.list_for_case(case_id, PETITION)
    inputs = ExemptionCase(
        case=case,
        debtors=debtor_store.list_for_case(case_id),
        petition=petitions[0].body if petitions else None,
        assets=tuple(
            (e.id, e.body) for e in entity_store.list_for_case(case_id, ASSET)
        ),
        claims=tuple(
            (e.id, e.body) for e in entity_store.list_for_case(case_id, CLAIM)
        ),
        exemptions=tuple(
            (e.id, e.body) for e in entity_store.list_for_case(case_id, EXEMPTION)
        ),
        sofa_entries=tuple(
            e.body for e in entity_store.list_for_case(case_id, SOFA_ENTRY)
        ),
    )
    return jsonify(analysis_json(analyse(inputs, today=date.today()))), 200
