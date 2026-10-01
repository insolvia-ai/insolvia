from __future__ import annotations

import logging
from dataclasses import replace
from datetime import date

from flask import Blueprint, jsonify, request
from flask.typing import ResponseReturnValue
from insolvia_core.access_log import record_access
from insolvia_core.cases import (
    RETAINED_STATUSES,
    CaseChanges,
    apply_changes,
    assign_case,
    case_json,
    create_case,
    is_retained_transition,
    parse_case_creation,
    parse_case_update,
    parse_list_limit,
    status_change,
)
from insolvia_core.errors import FieldValidationError, NotFoundError, ValidationError
from insolvia_core.fields import timestamp
from insolvia_core.firm_clients import FirmClient, debtor_from_client
from insolvia_core.firms import ADD_EDIT, CASES, CLIENTS, VIEW_ONLY
from insolvia_core.petitions import PETITION
from insolvia_core.ports import AccessLog, CaseStore, FirmStore

from insolvia_api.api.auth import current_accessor, require_auth, requires
from insolvia_api.api.dependencies import dependencies
from insolvia_api.api.routes.events import regenerate_deadlines
from insolvia_api.core.exemption_analysis import election_refusal, resolution_date

logger = logging.getLogger(__name__)

blueprint = Blueprint("cases", __name__)

# A case record is a handful of short fields. Anything larger is a mistake or
# an attack, and rejecting it before JSON parsing keeps both cheap.
MAX_REQUEST_BYTES = 64 * 1024

# `client_ids[0]` is Debtor 1, `client_ids[1]` Debtor 2 — the order B101
# prints them in. `parse_case_creation` caps the list at two.
OPENING_ROLES = ("debtor_1", "debtor_2")


def case_stores() -> tuple[CaseStore, AccessLog]:
    """The case store and its access log, or a loud failure.

    Both are Optional on ApiDependencies so the existing public-route tests can
    build one without them. In a deployed environment they are always present:
    entrypoints/api_lambda.py refuses to boot without them, exactly as it does
    for auth. Reaching this branch is a composition bug, so it raises rather
    than degrading — a case endpoint that quietly stopped recording access
    would be worse than one that stopped working.
    """
    deps = dependencies()
    if deps.case_store is None or deps.access_log is None:
        raise RuntimeError("case store and access log are not composed")
    return deps.case_store, deps.access_log


def composed_firm_store() -> FirmStore:
    """The firm store, for the one thing the case routes need it for directly:
    checking that an assignment names somebody in the caller's own firm.

    Everything else about firms reaches these routes through the resolved
    accessor, which is why this is a helper on two endpoints rather than a
    dependency of the module.
    """
    deps = dependencies()
    if deps.firm_store is None:
        raise RuntimeError("firm store is not composed")
    return deps.firm_store


def _refuse_forbidden_election(case_id: str, changes: CaseChanges) -> None:
    """106C line 1's opt-out rule, applied at the write (issue #346).

    A Florida or Georgia debtor cannot elect the federal § 522(d) list
    (Fla. Stat. § 222.20; O.C.G.A. § 44-13-100(b)), and the registry knows
    which states have opted out. Checked here rather than left to the
    analysis's read-time fallback because the case record is what B106C
    prints from, and a stored answer the law forbids is a record that lies
    even while every screen corrects it. The state comes from Debtor 1's
    residence address and the registry resolves as of the expected filing
    date (else today); a case that cannot yet say its state is not refused —
    the analysis reports that on read.
    """
    if changes.exemption_set is None:
        return
    deps = dependencies()
    if deps.debtor_store is None or deps.case_entity_store is None:
        raise RuntimeError("debtor store and entity store are not composed")
    debtors = deps.debtor_store.list_for_case(case_id)
    debtor_1 = next((d for d in debtors if d.filing_role == "debtor_1"), None)
    state = debtor_1.residence_address.state if debtor_1 is not None else None
    petitions = deps.case_entity_store.list_for_case(case_id, PETITION)
    as_of, _source = resolution_date(
        petitions[0].body if petitions else None, date.today()
    )
    refusal = election_refusal(
        changes.exemption_set,
        state=state.strip().upper() if state else None,
        as_of=as_of,
    )
    if refusal is not None:
        raise FieldValidationError({"exemption_set": refusal})


def _json_body() -> dict[str, object]:
    if request.content_length and request.content_length > MAX_REQUEST_BYTES:
        raise ValidationError("request body exceeds 64 KiB")
    payload = request.get_json(silent=True)
    if not isinstance(payload, dict):
        raise ValidationError("request body must be a JSON object")
    return payload


def _clients_to_open_for(
    client_ids: tuple[str, ...], access_log: AccessLog
) -> list[FirmClient]:
    """The firm clients a new case is opened for, in filing-role order — or a
    400 keyed `client_ids` naming what is wrong.

    Each id is resolved IN THE CALLER'S FIRM, so another firm's client and an
    id that never existed get the same answer — the `/v1/firm/clients/<id>`
    404's anti-oracle rule, as a field error here because the fault is in the
    request body. Each read is access-logged as a `client.read`, refused ones
    as `denied`: copying a person's identity into a case is reading their
    record, and someone walking client ids through this route is what the
    log is for.

    An ARCHIVED client is refused: archiving says the firm is done with
    them, and a new matter for them starts with bringing them back — a
    visible act, rather than a case quietly opened for someone the directory
    hides. A MERGED client is archived for good and is refused naming the
    fact, and so is one being merged away right now — a case opened for it
    mid-merge could be missed by the merge and left naming a merged client
    (`insolvia_core.client_merge`).
    """
    accessor = current_accessor()
    firm_store = composed_firm_store()
    clients: list[FirmClient] = []
    for client_id in client_ids:
        client = firm_store.get_client(accessor.firm_id, client_id)
        access_log.record(
            record_access(
                client_id=client_id,
                principal=accessor.subject,
                action="client.read",
                outcome="allowed" if client is not None else "denied",
            )
        )
        if client is None:
            raise FieldValidationError({"client_ids": "No such client."})
        refused = client.refusal_for_new_case()
        if refused is not None:
            raise FieldValidationError({"client_ids": refused})
        clients.append(client)
    return clients


# The roles a firm REPRESENTS. A non-filing spouse may be linked to a client
# record and is still not the firm's client (ADR 0022), so the retained
# transition never stamps them.
RETAINED_ROLES = ("debtor_1", "debtor_2")


def stamp_first_retained(case_id: str, access_log: AccessLog) -> None:
    """THE RETAINED TRANSITION's consequence (ADR 0022, #355): each filing
    debtor's client gets `first_retained_at` — today, as a form date — if it
    has none yet. Never overwritten: "first" means the earliest engagement,
    and a hand-entered earlier date is the firm's own record of it.

    After the case write, like the deadline hook: a stamp that fails leaves
    the case right and the client a request behind, never the reverse. It
    runs whatever the caller's `clients` grant — the server recording a fact
    about the engagement, not the caller editing a client — and each stamp
    is access-logged as the `client.update` it is. A merged client is
    refused by `update_client` and simply not stamped.
    """
    deps = dependencies()
    if deps.debtor_store is None:
        # Only a test composition that serves no debtor routes: every
        # deployed one composes the debtor store (entrypoints/api_lambda.py),
        # and with no debtor store there is no debtor to name a client.
        return
    accessor = current_accessor()
    firm_store = composed_firm_store()
    today = date.today().isoformat()
    for debtor in deps.debtor_store.list_for_case(case_id):
        if debtor.filing_role not in RETAINED_ROLES or debtor.client_id is None:
            continue
        client = firm_store.get_client(accessor.firm_id, debtor.client_id)
        if client is None or client.first_retained_at is not None:
            continue
        written = firm_store.update_client(
            replace(client, first_retained_at=today, updated_at=timestamp())
        )
        access_log.record(
            record_access(
                client_id=client.id,
                principal=accessor.subject,
                action="client.update",
                outcome="allowed" if written is not None else "denied",
            )
        )


@blueprint.post("/v1/cases")
@require_auth
@requires(CASES, ADD_EDIT)
@requires(CLIENTS, VIEW_ONLY)
def create_case_route() -> ResponseReturnValue:
    """Open a case for the caller's firm, FOR ONE OR TWO OF ITS CLIENTS.

    The firm comes from the caller's resolved accessor and is never read from
    the body, so there is no request a client can make that creates a case in
    another firm.

    The creator is LINKED to the case in the same transaction. Without that a
    paralegal without `access_all_cases` would open a matter they cannot see,
    cannot list and cannot reach by id — indistinguishable, from the outside,
    from the request having failed. core/cases.create_case returns the pair.

    And the DEBTORS are written in that transaction too (ADR 0022): each
    client in `client_ids` is copied into the case — the first as Debtor 1,
    the second, when present, as Debtor 2 — every copied field with `client`
    provenance. A non-filing spouse is never required here; they are linked
    (or typed) afterwards.

    `clients >= view_only` is required ON TOP OF `cases >= add_edit`: the
    copy reads a client record into a case the caller can then read back, so
    a caller the firm has not let see its client directory must not be able
    to read one through this route.
    """
    store, access_log = case_stores()
    draft = parse_case_creation(_json_body())
    accessor = current_accessor()
    clients = _clients_to_open_for(draft.client_ids, access_log)

    case, assignment = create_case(
        draft, firm_id=accessor.firm_id, created_by=accessor.subject
    )
    debtors = [
        debtor_from_client(client, case=case, filing_role=role)
        for client, role in zip(clients, OPENING_ROLES, strict=False)
    ]
    store.create(case, assignment, debtors)
    access_log.record(
        record_access(case_id=case.id, principal=accessor.subject, action="case.create")
    )
    # A case opened RETAINED is the engagement starting: the same stamp the
    # prospect -> retained move makes. A prospect stamps nothing yet.
    if case.status in RETAINED_STATUSES:
        stamp_first_retained(case.id, access_log)

    # GLBA: the case id and nothing about its contents. The FIRM id is not
    # logged either — a request log that accumulated tenant ids would be a
    # per-firm activity record sitting in CloudWatch.
    logger.info("case created", extra={"case_id": case.id})
    return jsonify(case_json(case)), 201


@blueprint.get("/v1/cases")
@require_auth
@requires(CASES, VIEW_ONLY)
def list_cases_route() -> ResponseReturnValue:
    """The cases the caller may see, newest first.

    WHICH CASES THAT IS depends on the caller: a firm admin or anyone with
    `access_all_cases` gets the whole firm's, and everyone else gets the
    matters they are linked to. The store picks the index; see
    CaseStore.list_for_accessor.

    A cursor is only valid against the listing that minted it, so flipping a
    user's `access_all_cases` mid-pagination answers 400 rather than silently
    skipping the cases in between.

    Deliberately NOT written to the access log. That table is keyed by case,
    and a list touches no case in particular; the question it exists to answer
    is "who saw this file". Recording enumeration properly wants the
    by-principal index that infra/modules/case_store defers, and a sentinel
    partition here would be a worse answer than none.

    `?archived=true` is the ARCHIVE (#355): the archived cases instead of the
    working list. An archived case leaves the default list, never the firm's
    records; a deleted one is in neither.
    """
    store, _ = case_stores()
    limit = parse_list_limit(request.args.get("limit"))
    cursor = request.args.get("cursor") or None
    archived = _parse_archived_view(request.args.get("archived"))

    page = store.list_for_accessor(
        current_accessor(), limit=limit, cursor=cursor, archived=archived
    )

    body: dict[str, object] = {"cases": [case_json(case) for case in page.cases]}
    # Absent rather than null when there is no next page — the client contract
    # distinguishes the two.
    if page.next_cursor is not None:
        body["nextCursor"] = page.next_cursor
    return jsonify(body), 200


def _parse_archived_view(raw: str | None) -> bool:
    if raw is None or raw == "" or raw == "false":
        return False
    if raw == "true":
        return True
    raise ValidationError("archived must be true or false")


@blueprint.get("/v1/cases/<case_id>")
@require_auth
@requires(CASES, VIEW_ONLY)
def get_case_route(case_id: str) -> ResponseReturnValue:
    """One case, if the caller may see it.

    Another firm's case answers 404, identically to one that does not exist —
    and so does the caller's OWN firm's case that they are not linked to. All
    three are the same answer on purpose: see core/errors.py's NotFoundError,
    and note that the third is not only about enumeration. Distinguishing "not
    linked" from "no such case" would tell any member of a firm which matters
    exist and which colleagues are on them, which is the thing per-case linking
    is for.

    The refused read IS recorded: someone walking case ids is exactly what the
    access log should show.
    """
    store, access_log = case_stores()
    accessor = current_accessor()

    case = store.get(case_id, accessor=accessor)
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
    return jsonify(case_json(case)), 200


@blueprint.patch("/v1/cases/<case_id>")
@require_auth
@requires(CASES, ADD_EDIT)
def update_case_route(case_id: str) -> ResponseReturnValue:
    """Change a case's chapter, court, status (and prospect stage),
    exemption election, filed and § 341 dates, or post-filing docket facts.

    A STATUS MOVE is checked against the lifecycle's map and recorded in the
    case's history (`GET /v1/cases/<id>/status-history`) in the same write.
    Leaving the prospect funnel is the retained transition, which stamps the
    clients' `first_retained_at`.

    Read-modify-write. The read applies the whole access rule; the store's
    conditional write closes the gap between the two, so a case cannot move
    firms out from under the caller between the read and the write.

    A `view_only` caller never reaches here — `@requires` refuses with 403,
    which is the right answer rather than a 404: they can see this case, and
    telling them it does not exist while it sits in their own listing would be
    a lie their client cannot act on.
    """
    store, access_log = case_stores()
    changes = parse_case_update(_json_body())
    accessor = current_accessor()

    existing = store.get(case_id, accessor=accessor)
    updated = None
    if existing is not None:
        _refuse_forbidden_election(case_id, changes)
        # Refuses a move off the lifecycle's map (409) or a filing without
        # its docket facts (400) before anything is written.
        changed = apply_changes(existing, changes)
        # Conditional on the status read above, with the history row in the
        # same transaction — CaseStore.update says why both.
        updated = store.update(
            changed,
            expected_status=existing.status,
            status_change=status_change(existing, changed, changed_by=accessor.subject),
        )

    access_log.record(
        record_access(
            case_id=case_id,
            principal=accessor.subject,
            action="case.update",
            outcome="allowed" if updated is not None else "denied",
        )
    )
    if updated is None:
        raise NotFoundError("case not found")

    # The deadline engine's hook (issue 14.6 / #358): a filed date, a § 341
    # date or a chapter change rewrites the case's generated events. After
    # the case write, so a regeneration that fails leaves the case correct
    # and the calendar a request behind, never the reverse.
    if changes.touches_deadline_anchors:
        regenerate_deadlines(updated, actor=accessor.subject)
    if existing is not None and is_retained_transition(existing, updated):
        stamp_first_retained(updated.id, access_log)

    logger.info("case updated", extra={"case_id": updated.id})
    return jsonify(case_json(updated)), 200


# ── Assignment: who in the firm is on this matter ───────────────
#
# These live here rather than in routes/firm.py because the resource is the
# CASE. Every one of them resolves the case through `store.get` first, which
# applies the whole access rule — so a caller who cannot see a matter cannot
# discover who is on it, and cannot put themselves on it either.
#
# WHO MAY CHANGE AN ASSIGNMENT is `cases: add_edit` rather than
# `firm_administration`, and that is a product decision worth stating. Linking
# a colleague to a matter is case work — the attorney running it does it, not
# whoever manages the firm's user accounts. An admin can do it too, because an
# admin can do everything; they just are not the only one.


@blueprint.get("/v1/cases/<case_id>/assignees")
@require_auth
@requires(CASES, VIEW_ONLY)
def list_assignees_route(case_id: str) -> ResponseReturnValue:
    """Who is linked to this case, oldest link first.

    Subjects, not names: turning one into a person is GET /v1/firm/directory's
    job, and duplicating the display name here would be a copy that goes stale
    the moment somebody is renamed.
    """
    store, _ = case_stores()
    accessor = current_accessor()

    if store.get(case_id, accessor=accessor) is None:
        raise NotFoundError("case not found")

    return jsonify(
        {
            "assignees": [
                {
                    "subject": a.subject,
                    "assignedAt": a.assigned_at,
                    "assignedBy": a.assigned_by,
                }
                for a in store.assignees(case_id)
            ]
        }
    ), 200


@blueprint.put("/v1/cases/<case_id>/assignees/<subject>")
@require_auth
@requires(CASES, ADD_EDIT)
def assign_case_route(case_id: str, subject: str) -> ResponseReturnValue:
    """Link a colleague to this case.

    PUT rather than POST because it is idempotent — the firm-admin UI cannot
    tell whether its first request landed, and re-linking somebody already on
    the matter must succeed rather than 409.

    THE SUBJECT MUST BE SOMEBODY IN THE CALLER'S FIRM, checked here against the
    firm store. Without that check this endpoint writes an assignment row for
    an arbitrary Cognito subject — which grants nothing today, because
    `may_see_case` tests the firm first and a stranger has no firm — but it
    would be a row in our case table naming a person who is not our tenant's,
    and it would put them on the case the moment they joined some firm.
    A 404, not a 403: a subject in another firm and a subject that does not
    exist are the same answer, or this becomes a probe for who works where.
    """
    store, access_log = case_stores()
    accessor = current_accessor()
    firm_store = composed_firm_store()

    case = store.get(case_id, accessor=accessor)
    if case is None:
        raise NotFoundError("case not found")

    colleague = firm_store.get_user(accessor.firm_id, subject)
    if colleague is None or colleague.status != "active":
        raise NotFoundError("firm user not found")

    store.assign(assign_case(case, subject=subject, assigned_by=accessor.subject))
    # Recorded as an update to the case, because it is one: it changes who may
    # read the file. The actor is the person doing the linking, which is the
    # question this log exists to answer.
    access_log.record(
        record_access(case_id=case_id, principal=accessor.subject, action="case.update")
    )
    logger.info("case assignee added", extra={"case_id": case_id})
    return "", 204


@blueprint.delete("/v1/cases/<case_id>/assignees/<subject>")
@require_auth
@requires(CASES, ADD_EDIT)
def unassign_case_route(case_id: str, subject: str) -> ResponseReturnValue:
    """Unlink a colleague from this case.

    UNLINKING THE LAST PERSON IS ALLOWED, unlike removing a firm's last admin,
    and the asymmetry is the point. A case with nobody on it is still the
    firm's: its admins and anyone with `access_all_cases` still reach it, so
    the firm can always assign somebody new. A firm with no admin has no such
    route back, which is why that one is refused.

    A caller CAN unlink themselves, and then lose access to the case they were
    just editing. That is the honest consequence of "I am no longer on this
    matter" and the alternative — refusing it — would leave someone unable to
    hand a case over without asking an admin.
    """
    store, access_log = case_stores()
    accessor = current_accessor()

    if store.get(case_id, accessor=accessor) is None:
        raise NotFoundError("case not found")
    if not store.unassign(case_id, subject):
        raise NotFoundError("assignee not found")

    access_log.record(
        record_access(case_id=case_id, principal=accessor.subject, action="case.update")
    )
    logger.info("case assignee removed", extra={"case_id": case_id})
    return "", 204
