"""`GET /v1/cases/{caseId}/standards` — the IRS National and Local Standards
for a case's jurisdiction and household (issue #348, the income-and-expenses
workbench), read-only.

Same shape as `case_summary.py`: a static segment sharing its prefix with the
generic `/v1/cases/<id>/<collection>` routes, so it is registered before
`case_entities_blueprint` in `app_factory.py` and Werkzeug's static-before-
dynamic ranking keeps the two apart. `VIEW_ONLY` on `CASES`, matching
`/summary`: this reads facts the caller can already see (the debtors, the
dependents) plus a published federal table, and grants nothing.
"""

from __future__ import annotations

import logging
from datetime import date

from flask import Blueprint, jsonify
from flask.typing import ResponseReturnValue
from insolvia_core.access_log import record_access
from insolvia_core.errors import NotFoundError
from insolvia_core.expenses import DEPENDENT
from insolvia_core.firms import CASES, VIEW_ONLY
from insolvia_core.ports import AccessLog, CaseEntityStore, CaseStore, DebtorStore

from insolvia_api.api.auth import current_accessor, require_auth, requires
from insolvia_api.api.dependencies import dependencies
from insolvia_api.core.standards import StandardsResult, resolve_standards

logger = logging.getLogger(__name__)

blueprint = Blueprint("standards", __name__)


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


def _standards_json(result: StandardsResult) -> dict[str, object]:
    """Money and jurisdiction facts as strings, `None` where a figure could
    not resolve — the optional-key rule these routes otherwise use does not
    apply here, because the client needs to know WHICH figure is missing to
    label its box, not just that some are."""
    return {
        "asOf": result.as_of.isoformat(),
        "state": result.state,
        "county": result.county,
        "householdSize": result.household_size,
        "jurisdictionSource": result.jurisdiction_source,
        "nationalStandards": {
            "releaseId": result.national_release_id,
            "allowance": result.national_allowance,
            "oopHealthcareUnder65": result.oop_healthcare_under_65,
            "oopHealthcare65AndOlder": result.oop_healthcare_65_and_older,
        },
        "localStandards": {
            "releaseId": result.local_release_id,
            "housingNonMortgage": result.housing_non_mortgage,
            "housingMortgageRent": result.housing_mortgage_rent,
            "transportationPublicNational": result.transportation_public_national,
            "transportationOwnershipOneCar": result.transportation_ownership_one_car,
            "transportationOwnershipTwoCars": result.transportation_ownership_two_cars,
            "transportationOperatingOneCar": result.transportation_operating_one_car,
            "transportationOperatingTwoCars": result.transportation_operating_two_cars,
        },
        "problems": list(result.problems),
    }


@blueprint.get("/v1/cases/<case_id>/standards")
@require_auth
@requires(CASES, VIEW_ONLY)
def case_standards_route(case_id: str) -> ResponseReturnValue:
    """The published allowances beside Schedule J's health-care and
    transportation lines, for the case's own jurisdiction and household.

    Always 200 with whichever figures resolved and a `problems` list
    explaining the rest — the same progressive-intake shape `/summary` uses,
    because a case with no county on file yet is not an error, it is early.
    A `LookupError` (no UST release effective on this date at all) is the one
    thing this route does NOT catch, for the same reason no other
    regulatory-dated module guesses past one: it becomes the generic 500,
    which is the honest answer to a registry that cannot describe today.
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

    debtors = debtor_store.list_for_case(case.id)
    dependents = entity_store.list_for_case(case.id, DEPENDENT)
    # The float-then-pin rule (docs/reference/effective-dating.md): the
    # case's creation date stands in for the filing date until `filed_at`
    # exists — the same reading `form_projections/shared.py`'s `_as_of` uses.
    as_of = date.fromisoformat(case.created_at[:10])

    result = resolve_standards(
        as_of=as_of,
        debtors=debtors,
        dependents=tuple(entity.body for entity in dependents),
    )
    return jsonify(_standards_json(result)), 200
