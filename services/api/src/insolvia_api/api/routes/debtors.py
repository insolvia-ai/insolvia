from __future__ import annotations

import logging

from flask import Blueprint, jsonify, request
from flask.typing import ResponseReturnValue
from insolvia_core.access import Accessor
from insolvia_core.access_log import record_access
from insolvia_core.cases import Case
from insolvia_core.debtors import (
    Debtor,
    DebtorDraft,
    create_debtor,
    debtor_json,
    link_client,
    parse_debtor,
    parse_filing_role,
    replace_debtor,
)
from insolvia_core.errors import FieldValidationError, NotFoundError, ValidationError
from insolvia_core.firm_clients import (
    FirmClient,
    debtor_from_client,
    differs_from_client,
)
from insolvia_core.firms import ADD_EDIT, CLIENTS, INTAKE, VIEW_ONLY
from insolvia_core.ports import (
    AccessLog,
    CaseStore,
    DebtorStore,
    FirmStore,
    TaxIdCipher,
    TaxIdStore,
)
from insolvia_core.tax_ids import TaxIdRef, store_tax_id

from insolvia_api.api.auth import current_accessor, require_auth, requires
from insolvia_api.api.dependencies import dependencies

logger = logging.getLogger(__name__)

blueprint = Blueprint("debtors", __name__)

# Larger than the case root's 64 KiB: a debtor carries two addresses and an
# eight-year alias list, plus a provenance entry per populated field, and
# provenance is roughly as big as the data it describes. Still small enough
# that anything over it is a mistake or an attack.
MAX_REQUEST_BYTES = 256 * 1024

# The filing roles that are always a firm client (ADR 0022). A non-filing
# spouse may be one and need not be — the firm does not represent them.
CLIENT_ROLES = ("debtor_1", "debtor_2")


def _stores() -> tuple[CaseStore, DebtorStore, AccessLog, TaxIdStore, TaxIdCipher]:
    deps = dependencies()
    if (
        deps.case_store is None
        or deps.debtor_store is None
        or deps.access_log is None
        or deps.tax_id_store is None
        or deps.tax_id_cipher is None
    ):
        raise RuntimeError(
            "case store, debtor store, access log, tax-id store and tax-id "
            "cipher are not composed"
        )
    return (
        deps.case_store,
        deps.debtor_store,
        deps.access_log,
        deps.tax_id_store,
        deps.tax_id_cipher,
    )


def _json_body() -> dict[str, object]:
    if request.content_length and request.content_length > MAX_REQUEST_BYTES:
        raise ValidationError("request body exceeds 256 KiB")
    payload = request.get_json(silent=True)
    if not isinstance(payload, dict):
        raise ValidationError("request body must be a JSON object")
    return payload


def _reachable_case_or_404(accessor: Accessor, case_id: str, action: str) -> Case:
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

    Returns the case because the write path needs its `firm_id`: the tax
    id's encryption context binds the FIRM (insolvia_core.tax_ids), and the
    firm comes from the resolved case, never from the request.
    """
    case_store, _, access_log, _, _ = _stores()
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
    return case


def _resolve_tax_id(
    draft: DebtorDraft, stored: Debtor | None, case: Case
) -> TaxIdRef | None:
    """What the saved debtor will carry — the draft's write sealed, kept, or
    cleared against the stored reference (tax_ids.store_tax_id owns the
    rules). Runs AFTER the case is resolved, because sealing needs the
    firm, and after the stored record is read, because keeping needs the
    reference. A refused echo is a 400 like any other malformed field; the
    case.update row already recorded above stands, because the case WAS
    reached."""
    _, _, _, tax_id_store, cipher = _stores()
    return store_tax_id(
        draft.tax_id,
        existing=stored.tax_id if stored is not None else None,
        firm_id=case.firm_id,
        case_id=case.id,
        cipher=cipher,
        store=tax_id_store,
    )


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

    The tax id (issue 13.12 / #382) rides the same PUT: the digits arrive in
    the body, are sealed under the case key BEFORE the debtor row is written,
    and only the last-four view and the sealed item's reference land on the
    record — so the response, like every read, carries `tax_id: {kind,
    last_four}` and nothing more. A client keeps a stored number across a
    save by echoing that view; it clears it by leaving the member out.
    """
    _, debtor_store, _, _, _ = _stores()
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
    case = _reachable_case_or_404(accessor, case_id, "case.update")

    stored = debtor_store.get(case_id, filing_role=role)
    if stored is None and role in CLIENT_ROLES:
        # ADR 0022: Debtor 1 and Debtor 2 are the firm's clients, so neither
        # is minted from a questionnaire save. Debtor 1 exists from the moment
        # the case is opened; Debtor 2 arrives by linking a client
        # (`PUT …/debtors/debtor_2/client`). Only the non-filing spouse — who
        # need not be a client — may still start from nothing here.
        raise FieldValidationError(
            {"client_id": "Link a client to this debtor before entering their details."}
        )
    if stored is None:
        # A CONDITIONAL create, not a plain write. Two overlapping first saves
        # would otherwise both find nothing, both mint an id, and the second
        # would erase the one the first had already returned to a client —
        # where provenance paths elsewhere may already name it. Autosave plus a
        # double submit is all it takes.
        fresh = create_debtor(
            draft,
            case_id=case_id,
            filing_role=role,
            tax_id=_resolve_tax_id(draft, None, case),
        )
        if debtor_store.create(fresh):
            _log_saved(case_id, fresh.id, role)
            return jsonify(_debtor_view(accessor, case, fresh)), 201
        # Lost the race. `fresh`'s id has not left this process, so dropping it
        # costs nothing; the winner's record is the one to build on — and so
        # is the winner's tax-id reference, which is why the tax id is
        # resolved again below against what the winner stored.
        stored = debtor_store.get(case_id, filing_role=role)

    if stored is None:
        # The record would have to have been removed between the refused create
        # and this read. Nothing deletes debtors, so this is a store that is
        # not behaving as its Protocol says — louder is better than a 500 with
        # an UnboundLocalError in it.
        raise RuntimeError("debtor vanished between a refused create and a read")

    debtor = replace_debtor(stored, draft, tax_id=_resolve_tax_id(draft, stored, case))
    debtor_store.put(debtor)
    _log_saved(case_id, debtor.id, role)
    return jsonify(_debtor_view(accessor, case, debtor)), 200


@blueprint.get("/v1/cases/<case_id>/debtors")
@require_auth
@requires(INTAKE, VIEW_ONLY)
def list_debtors_route(case_id: str) -> ResponseReturnValue:
    """Every debtor of one case, in the order the forms print them.

    Logged as a read of the CASE, unlike `GET /v1/cases`, which is not logged
    at all: this names one case, so "who saw this file" has an answer worth
    recording.
    """
    _, debtor_store, _, _, _ = _stores()
    accessor = current_accessor()

    case = _reachable_case_or_404(accessor, case_id, "case.read")
    debtors = debtor_store.list_for_case(case_id)
    return jsonify(
        {"debtors": [_debtor_view(accessor, case, debtor) for debtor in debtors]}
    ), 200


@blueprint.put("/v1/cases/<case_id>/debtors/<filing_role>/client")
@require_auth
@requires(INTAKE, ADD_EDIT)
@requires(CLIENTS, VIEW_ONLY)
def link_client_route(case_id: str, filing_role: str) -> ResponseReturnValue:
    """Link a firm client to one debtor of a case — `{"client_id": "…"}`.

    THE ONLY WRITER OF A DEBTOR'S `client_id` after the case is opened
    (ADR 0022: server-owned; the questionnaire's PUT keeps it). Two cases:

    - **No debtor in that role yet** — the client is COPIED in, exactly as
      `POST /v1/cases` copies Debtor 1: a new record, every field with
      `client` provenance. 201. This is how a joint case gains its Debtor 2,
      and how a non-filing spouse who is a client is added.
    - **A debtor already there** — only the link moves. The copied fields are
      the case's and stay as they are; where they now disagree with the
      newly linked client is `differs_from_client`, and copying over is its
      own explicit act. 200.

    One client, one role per case: a client already linked to another role
    of this case is refused, or the `by-client` index would list the case
    twice. The client is resolved in the CASE's firm and must be active,
    for `POST /v1/cases`'s reasons, and the read is access-logged the same
    way.

    Known limit: the one-role check is a read before the write, so two
    concurrent links of one client to two roles can both land. Closing it
    needs a lock item per (case, client); the window is two requests racing
    on one matter's intake.
    """
    _, debtor_store, access_log, _, _ = _stores()
    accessor = current_accessor()
    role = parse_filing_role(filing_role)
    client_id = _client_id_of(_json_body())
    case = _reachable_case_or_404(accessor, case_id, "case.update")

    client = _firm_store().get_client(case.firm_id, client_id)
    access_log.record(
        record_access(
            client_id=client_id,
            principal=accessor.subject,
            action="client.read",
            outcome="allowed" if client is not None else "denied",
        )
    )
    if client is None:
        raise FieldValidationError({"client_id": "No such client."})
    if client.archived:
        raise FieldValidationError(
            {"client_id": "That client is archived — restore them first."}
        )
    for other in debtor_store.list_for_case(case_id):
        if other.client_id == client.id and other.filing_role != role:
            raise FieldValidationError(
                {"client_id": "That client is already another debtor on this case."}
            )

    stored = debtor_store.get(case_id, filing_role=role)
    if stored is None:
        fresh = debtor_from_client(client, case=case, filing_role=role)
        if debtor_store.create(fresh):
            _log_saved(case_id, fresh.id, role)
            return jsonify(_debtor_view(accessor, case, fresh, client=client)), 201
        stored = debtor_store.get(case_id, filing_role=role)
        if stored is None:
            raise RuntimeError("debtor vanished between a refused create and a read")

    linked = link_client(stored, client_id=client.id, case_created_at=case.created_at)
    debtor_store.put(linked)
    _log_saved(case_id, linked.id, role)
    return jsonify(_debtor_view(accessor, case, linked, client=client)), 200


def _client_id_of(payload: dict[str, object]) -> str:
    client_id = payload.get("client_id")
    if not isinstance(client_id, str) or not client_id.strip():
        raise FieldValidationError({"client_id": "Choose a client."})
    return client_id.strip()


def _firm_store() -> FirmStore:
    deps = dependencies()
    if deps.firm_store is None:
        raise RuntimeError("firm store is not composed")
    return deps.firm_store


def _debtor_view(
    accessor: Accessor,
    case: Case,
    debtor: Debtor,
    *,
    client: FirmClient | None = None,
) -> dict[str, object]:
    """`debtor_json`, plus `differs_from_client` when the debtor names a
    client AND the caller may see the firm's clients (ADR 0022).

    Computed on every read rather than stored: the divergence is between two
    records that change independently, and a stored flag would be stale the
    moment either did. A caller without `clients` gets the debtor without
    the member — which fields of a client record differ is a fact about that
    record. NOT access-logged as a client read: nothing of the client's
    leaves the server here except which of the case's own fields disagree,
    and logging it would write a client row on every autosave.

    A linked client that is no longer in the case's firm (nothing can move
    one today) yields no member rather than a diff against nothing."""
    if debtor.client_id is None or not accessor.may(CLIENTS, VIEW_ONLY):
        return debtor_json(debtor)
    if client is None or client.id != debtor.client_id:
        client = _firm_store().get_client(case.firm_id, debtor.client_id)
    if client is None:
        return debtor_json(debtor)
    return debtor_json(debtor, differs_from_client=differs_from_client(debtor, client))
