"""`/v1/cases/{caseId}/plan-calculation` — the Chapter 13 plan calculator
(issue 16.2 / #366): the waterfall, feasibility and the § 1325(a)(4)
liquidation test, for the case's stored plan (GET) or for unsaved
alternatives (POST).

WHY THE PLAN ITSELF IS NOT HERE. The proposal is an ordinary case record —
`plans`, registered in `insolvia_core.case_collections` — written through
the generic collection routes with provenance like every other confirmed
value. This module only computes; neither verb writes.

WHY SCENARIOS ARE A POST THAT WRITES NOTHING. Comparing alternatives means
running the calculator over proposals nobody has confirmed. Storing them
would put unconfirmed figures in the case (the confirm-before-entry
invariant); sending them in the body keeps them the preparer's scratch
work. Each is parsed by the record's own parser (`parse_plan`), so a
scenario the calculator accepts is one the collection route would store —
adopting it is a PUT of the same body with its provenance. The body is
POST rather than a GET query because a plan is a nested document.

The URL shares its prefix with `/v1/cases/<id>/<collection>`; the static
segment wins under Werkzeug's ranking — the note the summary, liens,
means-test and exemption-analysis routes each carry. `VIEW_ONLY` on `CASES`
for both verbs, matching `/means-test`: they read the case and grant
nothing.
"""

from __future__ import annotations

import logging
from collections.abc import Mapping, Sequence
from datetime import date
from typing import Final

from flask import Blueprint, jsonify, request
from flask.typing import ResponseReturnValue
from insolvia_core.access_log import record_access
from insolvia_core.cases import Case
from insolvia_core.errors import FieldValidationError, NotFoundError, ValidationError
from insolvia_core.fields import text
from insolvia_core.firms import CASES, VIEW_ONLY
from insolvia_core.plans import PLAN, PlanBody, parse_plan
from insolvia_core.ports import AccessLog, CaseEntityStore, CaseStore, DebtorStore

from insolvia_api.api.auth import current_accessor, require_auth, requires
from insolvia_api.api.dependencies import dependencies
from insolvia_api.core.chapter13_plan import (
    PlanInputs,
    build_plan_inputs,
    calculate_plan,
    plan_calculation_json,
)
from insolvia_api.core.exemption_analysis import ExemptionCase
from insolvia_api.core.packet_assembly import read_case_data, to_case_file

logger = logging.getLogger(__name__)

blueprint = Blueprint("plan", __name__)

MAX_REQUEST_BYTES: Final = 256 * 1024
# Enough to compare a handful of alternatives side by side; each one runs
# the whole waterfall.
MAX_SCENARIOS: Final = 5


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


def _reachable_case(case_id: str) -> Case:
    case_store, _debtors, _entities, access_log = _stores()
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
    return case


def _inputs(case: Case) -> tuple[PlanInputs, PlanBody | None, int]:
    """The case read once — the whole file, the way packet assembly and the
    means-test route read it — and the stored plan with how many exist."""
    _cases, debtor_store, entity_store, _log = _stores()
    data = read_case_data(case, debtor_store=debtor_store, entity_store=entity_store)
    exemptions = ExemptionCase(
        case=case,
        debtors=data.debtors,
        petition=data.petitions[0].body if data.petitions else None,
        assets=tuple((e.id, e.body) for e in data.assets),
        claims=tuple((e.id, e.body) for e in data.claims),
        exemptions=tuple((e.id, e.body) for e in data.exemptions),
        sofa_entries=tuple(e.body for e in data.sofa_entries),
    )
    inputs = build_plan_inputs(to_case_file(data), exemptions, today=date.today())
    plans = entity_store.list_for_case(case.id, PLAN)
    return inputs, (plans[0].body if plans else None), len(plans)


@blueprint.get("/v1/cases/<case_id>/plan-calculation")
@require_auth
@requires(CASES, VIEW_ONLY)
def plan_calculation_route(case_id: str) -> ResponseReturnValue:
    """The stored plan's figures. Always 200 on a reachable case: a case with
    no plan yet still gets its liquidation floor, and every missing input is
    a `problem` beside whatever did compute."""
    case = _reachable_case(case_id)
    inputs, plan, count = _inputs(case)
    calculation = plan_calculation_json(calculate_plan(inputs, plan))
    if count > 1:
        # The packet gate owns the cardinality (#367); the calculator says
        # which record it read rather than refusing.
        warnings = calculation["warnings"]
        assert isinstance(warnings, list)
        warnings.insert(
            0,
            f"This case has {count} plan records; the first created is used.",
        )
    return jsonify(calculation), 200


def _scenarios(payload: object) -> list[tuple[str | None, PlanBody]]:
    if not isinstance(payload, Mapping):
        raise ValidationError("request body must be a JSON object")
    raw = payload.get("scenarios")
    if not isinstance(raw, Sequence) or isinstance(raw, (str, bytes)) or not raw:
        raise FieldValidationError({"scenarios": "Must be a non-empty list."})
    if len(raw) > MAX_SCENARIOS:
        raise FieldValidationError(
            {"scenarios": f"At most {MAX_SCENARIOS} scenarios at a time."}
        )
    errors: dict[str, str] = {}
    parsed: list[tuple[str | None, PlanBody]] = []
    for index, scenario in enumerate(raw):
        path = f"scenarios[{index}]"
        if not isinstance(scenario, Mapping):
            errors[path] = "Must be an object."
            continue
        label = text(scenario.get("label"), f"{path}.label", errors)
        plan = scenario.get("plan")
        if not isinstance(plan, Mapping):
            errors[f"{path}.plan"] = "Must be an object."
            continue
        try:
            parsed.append((label, parse_plan(plan)))
        except FieldValidationError as error:
            for field_path, message in error.fields.items():
                errors[f"{path}.plan.{field_path}"] = message
    if errors:
        raise FieldValidationError(errors)
    return parsed


@blueprint.post("/v1/cases/<case_id>/plan-calculation")
@require_auth
@requires(CASES, VIEW_ONLY)
def plan_scenarios_route(case_id: str) -> ResponseReturnValue:
    """Unsaved alternatives, each calculated against the same case.
    Reachability is resolved first, so a 400 is never an oracle for a case
    id the caller cannot see; the body is parsed before the case's records
    are read, so a malformed request costs no collection reads."""
    case = _reachable_case(case_id)
    if request.content_length and request.content_length > MAX_REQUEST_BYTES:
        raise ValidationError("request body exceeds 256 KiB")
    scenarios = _scenarios(request.get_json(silent=True))
    inputs, _plan, _count = _inputs(case)
    return (
        jsonify(
            {
                "scenarios": [
                    {
                        "label": label,
                        "calculation": plan_calculation_json(
                            calculate_plan(inputs, plan)
                        ),
                    }
                    for label, plan in scenarios
                ]
            }
        ),
        200,
    )
