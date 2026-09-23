"""`GET /v1/cases/{caseId}/means-test` — the § 707(b) trace for one case
(issue #349), read-only.

The engine (core/cmi.py, core/means_test.py) used to run only inside packet
assembly, so the attorney learned whether the presumption arose when the
packet was assembled — too late to advise the client. This route runs the
same code path on demand (core/means_test_trace.py says exactly which
calls, and that it adds no arithmetic of its own) and returns the full
trace: CMI by line and column with the window it used, the median
comparison, and for an above-median debtor the B122A-2 lines and the
presumption result, every figure naming the rule or dataset release it came
from.

Same shape as `case_summary.py`: a static segment sharing its prefix with
the generic `/v1/cases/<id>/<collection>` routes, registered before
`case_entities_blueprint` so Werkzeug's static-before-dynamic ranking keeps
the two apart. `VIEW_ONLY` on `CASES`, matching `/summary`: this reads the
case's own contents plus published federal tables, and grants nothing.
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
from insolvia_api.core.means_test_trace import means_test_json, trace_means_test
from insolvia_api.core.packet_assembly import read_case_data, to_case_file

logger = logging.getLogger(__name__)

blueprint = Blueprint("means_test", __name__)


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


@blueprint.get("/v1/cases/<case_id>/means-test")
@require_auth
@requires(CASES, VIEW_ONLY)
def means_test_route(case_id: str) -> ResponseReturnValue:
    """The means test as of the case's expected filing date.

    Always 200 with whichever figures resolved and a `problems` list
    explaining the rest — a case with no household entered yet is early,
    not broken, and an expected filing date the datasets do not yet cover
    is a fact about the entered date (core/means_test_trace.py says why
    that one differs from `/standards`).

    Reachability is resolved under the caller's accessor and logged either
    way, and an unreachable case answers the undistinguishing 404 the
    id-oracle rule requires.
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

    # The whole case file, the way packet assembly reads it — the trace's
    # inputs (income history, claims, dependents, petition, means-test
    # inputs) span most of the collections, and one read path is what keeps
    # this endpoint and the packet reading the same records.
    case_file = to_case_file(
        read_case_data(case, debtor_store=debtor_store, entity_store=entity_store)
    )
    return jsonify(means_test_json(trace_means_test(case_file))), 200
