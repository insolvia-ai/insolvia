from __future__ import annotations

import logging

from flask import Blueprint, jsonify, request
from flask.typing import ResponseReturnValue
from insolvia_core.errors import NotFoundError, ValidationError
from insolvia_core.firms import ADD_EDIT, INTAKE, VIEW_ONLY

from insolvia_api.api.auth import current_accessor, require_auth, requires
from insolvia_api.api.dependencies import dependencies
from insolvia_api.core.access import Accessor
from insolvia_api.core.access_log import record_access
from insolvia_api.core.debtors import (
    create_debtor,
    debtor_json,
    parse_debtor,
    parse_filing_role,
    replace_debtor,
)
from insolvia_api.core.ports import AccessLog, CaseStore, DebtorStore

logger = logging.getLogger(__name__)

blueprint = Blueprint("debtors", __name__)

# Larger than the case root's 64 KiB: a debtor carries two addresses and an
# eight-year alias list, plus a provenance entry per populated field, and
# provenance is roughly as big as the data it describes. Still small enough
# that anything over it is a mistake or an attack.
MAX_REQUEST_BYTES = 256 * 1024


def _stores() -> tuple[CaseStore, DebtorStore, AccessLog]:
    deps = dependencies()
    if deps.case_store is None or deps.debtor_store is None or deps.access_log is None:
        raise RuntimeError("case store, debtor store and access log are not composed")
    return deps.case_store, deps.debtor_store, deps.access_log


def _json_body() -> dict[str, object]:
    if request.content_length and request.content_length > MAX_REQUEST_BYTES:
        raise ValidationError("request body exceeds 256 KiB")
    payload = request.get_json(silent=True)
    if not isinstance(payload, dict):
        raise ValidationError("request body must be a JSON object")
    return payload


def _reachable_case_or_404(accessor: Accessor, case_id: str, action: str) -> None:
    """Resolve the case first, and record the attempt either way.

    EVERY debtor route goes through here, because this is the only
    authorisation check there is: `DebtorStore` takes no accessor and enforces
    nothing (see its Protocol for why one authorisation path beats two). A
    debtor route that skipped this would read another firm's clients' names
    with no error anywhere.

    IT TAKES AN ACCESSOR, NOT A SUBJECT, and the rename from
    `_owned_case_or_404` is the point rather than tidying. "Owned" described a
    model where a case belonged to one Cognito subject; it now belongs to a
    FIRM, and reaching it means being in that firm AND (an admin, or
    `access_all_cases`, or linked to this matter). `CaseStore.get` applies the
    whole rule — core/access.may_see_case — so the change here is one argument
    and nothing else, which is exactly what it should have been.
    """
    case_store, _, access_log = _stores()
    case = case_store.get(case_id, accessor=accessor)
    access_log.record(
        record_access(
            case_id=case_id,
            principal=accessor.subject,
            action=action,
            outcome="allowed" if case is not None else "denied",
        )
    )
    if case is None:
        # Identical to a case that does not exist — core/errors.py explains why
        # distinguishing them turns this into an id oracle.
        raise NotFoundError("case not found")


def _log_saved(case_id: str, debtor_id: str, role: str) -> None:
    """GLBA: ids and the role. Never a name, an address, or a field count —
    even the number of populated fields leaks how much of someone's intake
    is filled in."""
    logger.info(
        "debtor saved",
        extra={"case_id": case_id, "debtor_id": debtor_id, "role": role},
    )


@blueprint.put("/v1/cases/<case_id>/debtors/<filing_role>")
@require_auth
@requires(INTAKE, ADD_EDIT)
def put_debtor_route(case_id: str, filing_role: str) -> ResponseReturnValue:
    """Save one debtor of a case, whole.

    PUT rather than PATCH, and the reason is invariant 1 rather than taste:
    "every populated field carries provenance" can only be checked against a
    complete record. A partial write would have to merge with the stored copy
    and re-derive the rule afterwards — the same check, somewhere easier to get
    wrong. The questionnaire holds the whole record client-side anyway, so
    autosave sends it.

    Idempotent: the same body twice leaves the same record, keeping the id and
    created_at of the first write because provenance paths elsewhere may
    already name that id.
    """
    _, debtor_store, _ = _stores()
    accessor = current_accessor()

    # Body BEFORE ownership, which looks backwards and is deliberate. The body
    # check is about the caller's own request and reveals nothing about the
    # case — a 400 says "your JSON is wrong", not "that case exists". Doing it
    # first keeps the access log honest: an entry there means the case was
    # actually reached, not that someone sent a malformed request at it. The
    # log has no "attempted" outcome, only allowed and denied, so the
    # alternative would be recording changes that never happened.
    role = parse_filing_role(filing_role)
    draft = parse_debtor(_json_body())
    _reachable_case_or_404(accessor, case_id, "case.update")

    stored = debtor_store.get(case_id, filing_role=role)
    if stored is None:
        # A CONDITIONAL create, not a plain write. Two overlapping first saves
        # would otherwise both find nothing, both mint an id, and the second
        # would erase the one the first had already returned to a client —
        # where provenance paths elsewhere may already name it. Autosave plus a
        # double submit is all it takes.
        fresh = create_debtor(draft, case_id=case_id, filing_role=role)
        if debtor_store.create(fresh):
            _log_saved(case_id, fresh.id, role)
            return jsonify(debtor_json(fresh)), 201
        # Lost the race. `fresh`'s id has not left this process, so dropping it
        # costs nothing; the winner's record is the one to build on.
        stored = debtor_store.get(case_id, filing_role=role)

    if stored is None:
        # The record would have to have been removed between the refused create
        # and this read. Nothing deletes debtors, so this is a store that is
        # not behaving as its Protocol says — louder is better than a 500 with
        # an UnboundLocalError in it.
        raise RuntimeError("debtor vanished between a refused create and a read")

    debtor = replace_debtor(stored, draft)
    debtor_store.put(debtor)
    _log_saved(case_id, debtor.id, role)
    return jsonify(debtor_json(debtor)), 200


@blueprint.get("/v1/cases/<case_id>/debtors")
@require_auth
@requires(INTAKE, VIEW_ONLY)
def list_debtors_route(case_id: str) -> ResponseReturnValue:
    """Every debtor of one case, in the order the forms print them.

    Logged as a read of the CASE, unlike `GET /v1/cases`, which is not logged
    at all: this names one case, so "who saw this file" has an answer worth
    recording.
    """
    _, debtor_store, _ = _stores()
    accessor = current_accessor()

    _reachable_case_or_404(accessor, case_id, "case.read")
    debtors = debtor_store.list_for_case(case_id)
    return jsonify({"debtors": [debtor_json(debtor) for debtor in debtors]}), 200
