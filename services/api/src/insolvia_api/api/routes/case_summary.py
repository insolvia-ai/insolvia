"""`GET /v1/cases/{caseId}/summary` — one case from above.

The case overview's whole read. It exists as ONE endpoint rather than as
fields bolted onto `GET /v1/cases/{id}` because it is expensive in a way the
case record is not: every total is computed from the case's collections, so
this reads all of them, and a caller who only wanted the chapter and district
should not pay for that.

The URL shares its prefix with the generic `/v1/cases/<id>/<collection>`
routes; "summary" is a static segment and not a registered collection, so
Werkzeug ranking keeps them apart — the same note the creditor-matrix and
packets routes carry.
"""

from __future__ import annotations

import logging

from flask import Blueprint, jsonify
from flask.typing import ResponseReturnValue
from insolvia_core.access_log import record_access
from insolvia_core.errors import NotFoundError
from insolvia_core.firms import CASES, VIEW_ONLY
from insolvia_core.ports import AccessLog, CaseEntityStore, CaseStore, DebtorStore

from insolvia_api.api.auth import current_accessor, require_auth, requires
from insolvia_api.api.dependencies import dependencies
from insolvia_api.core.case_summary import CaseTotals, summarise
from insolvia_api.core.packet_assembly import problem_json, read_case_data

logger = logging.getLogger(__name__)

blueprint = Blueprint("case_summary", __name__)


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


def _totals_json(totals: CaseTotals) -> dict[str, str]:
    """Money as STRINGS, never JSON numbers.

    These are Decimals on a bankruptcy filing. Serialised as numbers they would
    reach the client as IEEE doubles, and `1234.05` does not survive that trip
    unchanged — the same reason `ClaimBody.amount` is a `str` in the domain.
    The client renders what it is given and does no arithmetic on it.
    """
    return {
        "realEstate": str(totals.real_estate),
        "personalProperty": str(totals.personal_property),
        "assets": str(totals.assets),
        "secured": str(totals.secured),
        "priorityUnsecured": str(totals.priority_unsecured),
        "nonpriorityUnsecured": str(totals.nonpriority_unsecured),
        "liabilities": str(totals.liabilities),
    }


@blueprint.get("/v1/cases/<case_id>/summary")
@require_auth
@requires(CASES, VIEW_ONLY)
def case_summary_route(case_id: str) -> ResponseReturnValue:
    """What this case is worth, what it owes, and whether it could be filed.

    `readyToFile` and `problems` come from the SAME completeness gate packet
    assembly runs, not from a cheaper approximation — an overview that says
    "ready" over a case the assembler then refuses is worse than one that says
    nothing at all.

    `VIEW_ONLY` on `CASES`, matching the packets route: this is a read of the
    case's own contents and grants nothing. Reachability is resolved under the
    caller's accessor and logged either way, and an unreachable case answers
    the undistinguishing 404 the id-oracle rule requires.
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

    summary = summarise(
        read_case_data(case, debtor_store=debtor_store, entity_store=entity_store)
    )
    return (
        jsonify(
            {
                "readyToFile": summary.ready_to_file,
                "problems": [problem_json(p) for p in summary.problems],
                "totals": _totals_json(summary.totals),
            }
        ),
        200,
    )
