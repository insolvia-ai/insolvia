"""The client portal's own routes (ADR 0023) — what a CLIENT reaches.

EVERY ROUTE HERE CARRIES `@require_client` AND NOTHING ELSE AUTHENTICATES
THEM. tests/unit/test_portal_routes.py walks the whole URL map to hold that
down from both sides: every `/v1/portal/` rule admits the client class only,
and no other rule admits it at all.

NO ROUTE HERE NAMES A CASE. The case is the binding's, resolved from the
verified token — there is no case id for a client to guess, and nothing to
probe for another firm's (ADR 0023's threat model). When ADR 0022 moves the
binding from the case to a client record, nothing a client calls changes.

Reads go through projections that take the `ClientAccessor`, never
`CaseStore.get`: a client may read the firm's name and — from the routes
that follow — the questionnaire, their own candidates and their document
requests, and never the confirmed case record.
"""

from __future__ import annotations

import logging

from flask import Blueprint, jsonify, request
from flask.typing import ResponseReturnValue
from insolvia_core.access import ClientAccessor
from insolvia_core.access_log import record_access
from insolvia_core.candidates import PENDING, CandidateOrigin, withdraw
from insolvia_core.clients import (
    CasePublicStatus,
    public_status_json,
)
from insolvia_core.errors import ConflictError, ForbiddenError, ValidationError
from insolvia_core.ports import AccessLog, CandidateStore, FirmStore
from insolvia_core.questionnaire import ResolvedSection, resolve
from insolvia_core.questions import CHANNEL as ANSWER_CHANNEL
from insolvia_core.questions import (
    answer_json,
    answer_ref,
    create_answer,
    is_own_answer,
    own_answer_or_404,
    parse_answer,
    portal_sections_json,
    revise_answer,
)

from insolvia_api.api.client_auth import (
    current_client,
    current_client_principal,
    require_client,
)
from insolvia_api.api.dependencies import dependencies

logger = logging.getLogger(__name__)

blueprint = Blueprint("portal", __name__)


def portal_me_json(
    client: ClientAccessor, status: CasePublicStatus
) -> dict[str, object]:
    """Who the portal says you are — the fixed read policy's smallest piece.

    The name is the one the FIRM gave at invitation (the binding's), not
    anything the token carries; the firm block is its name alone; the case
    block is the public status — chapter and stage — and deliberately no case
    id: see the module docstring.
    """
    return {
        "subject": client.subject,
        "displayName": client.binding.display_name,
        "roles": list(client.roles),
        "firm": {"name": client.firm.name},
        "case": public_status_json(status),
    }


def _public_status(client: ClientAccessor) -> CasePublicStatus:
    """The bound case's chapter and stage, through the projection that takes
    the binding (`CaseStore.public_status`) — never `CaseStore.get`, which
    takes a staff `Accessor` and returns the whole record.

    A live binding whose case is gone is a 403, the same refusal as no
    binding at all: there is no case to reach through the portal, and which
    of the two it is belongs to the firm."""
    store = dependencies().case_store
    if store is None:
        raise RuntimeError("case store is not composed")
    status = store.public_status(client)
    if status is None:
        raise ForbiddenError(
            "you do not have access to a case through the client portal"
        )
    return status


@blueprint.get("/v1/portal/me")
@require_client
def portal_me_route() -> ResponseReturnValue:
    """The portal's "is my session good, and whose portal is this?" probe.

    Recorded as `portal.read` against the binding's case, with the client as
    principal — every portal request is on the access log (ADR 0023 § Audit).
    """
    client = current_client()
    access_log = dependencies().access_log
    if access_log is None:
        raise RuntimeError("access log is not composed")
    access_log.record(
        record_access(
            case_id=client.case_id, principal=client.subject, action="portal.read"
        )
    )
    return jsonify(portal_me_json(client, _public_status(client))), 200


@blueprint.get("/v1/portal/questionnaire")
@require_client
def portal_questionnaire_route() -> ResponseReturnValue:
    """The questionnaire's sections as this client's firm configured them —
    the enabled ones only, each with the instructions in force (ADR 0023
    PR 3). A section the firm switched off is simply absent: the answer
    says nothing about what is hidden, not even that something is.

    Read from the binding's FIRM, never from anything in the request — the
    firm id on the ClientAccessor came from the binding row. Recorded as
    `portal.read` against the binding's case, like `/v1/portal/me`.
    """
    client = current_client()
    deps = dependencies()
    if deps.access_log is None:
        raise RuntimeError("access log is not composed")
    if deps.firm_store is None:
        raise RuntimeError("firm store is not composed")
    config = deps.firm_store.get_questionnaire(client.firm_id)
    deps.access_log.record(
        record_access(
            case_id=client.case_id, principal=client.subject, action="portal.read"
        )
    )
    # Since ADR 0023 PR 4 each section carries its questions.
    return jsonify(portal_sections_json(config)), 200


# ── Answers (ADR 0023 PR 4 / #363) ──────────────────────────────────
#
# An answer is a CANDIDATE — `create_candidate` with the client origin from
# the verified token — in the case's one review queue. Nothing here writes,
# or reads, a case record: the only way an answer reaches case data is a
# person accepting it through `/v1/cases/<id>/extraction/candidates/…/review`.

# An answer is a handful of short fields; a bigger body is a mistake.
MAX_ANSWER_BYTES = 16 * 1024


def _answer_body() -> dict[str, object]:
    if request.content_length and request.content_length > MAX_ANSWER_BYTES:
        raise ValidationError("request body exceeds 16 KiB")
    payload = request.get_json(silent=True)
    if not isinstance(payload, dict):
        raise ValidationError("request body must be a JSON object")
    return payload


def _answer_stores() -> tuple[CandidateStore, AccessLog, FirmStore]:
    deps = dependencies()
    if deps.candidate_store is None or deps.access_log is None:
        raise RuntimeError("the candidate store and access log are not composed")
    if deps.firm_store is None:
        raise RuntimeError("firm store is not composed")
    return deps.candidate_store, deps.access_log, deps.firm_store


def _sections(
    firm_store: FirmStore, client: ClientAccessor
) -> tuple[ResolvedSection, ...]:
    """The firm's questionnaire as it stands NOW — read on every answer
    request, so a section switched off stops taking answers immediately."""
    return resolve(firm_store.get_questionnaire(client.firm_id))


def _record(access_log: AccessLog, client: ClientAccessor, action: str) -> None:
    access_log.record(
        record_access(case_id=client.case_id, principal=client.subject, action=action)
    )


def _log_answer(client: ClientAccessor, candidate_id: str, what: str) -> None:
    # GLBA: ids and the verb — never the value, never the question's text.
    logger.info(
        "portal answer",
        extra={"case_id": client.case_id, "candidate_id": candidate_id, "what": what},
    )


@blueprint.get("/v1/portal/answers")
@require_client
def list_answers_route() -> ResponseReturnValue:
    """This client's own answers, every status — the questionnaire's resume
    state (ADR 0023: reviewed rows are immutable and a change is a new
    candidate, so there is no second draft store). Only `origin.subject ==
    self`, and only in sections the firm still shows: nobody else's
    candidates, and nothing about them, not even a count."""
    client = current_client()
    candidate_store, access_log, firm_store = _answer_stores()
    sections = _sections(firm_store, client)
    enabled = {resolved.section.id for resolved in sections if resolved.enabled}
    answers: list[dict[str, object]] = []
    for candidate in candidate_store.list_for_case(client.case_id):
        if not is_own_answer(candidate, subject=client.subject):
            continue
        ref = answer_ref(candidate)
        if ref is None or ref.question.section not in enabled:
            continue
        answers.append(answer_json(candidate, ref))
    _record(access_log, client, "portal.read")
    return jsonify({"answers": answers}), 200


@blueprint.post("/v1/portal/answers")
@require_client
def create_answer_route() -> ResponseReturnValue:
    """Answer one question: `{questionId, filingRole?, value}` → 201 with
    the pending answer. The origin is the verified token's — channel
    `client`, the portal app client, the subject — never anything the body
    says. 409 when a one-answer question already has a pending answer
    from this client (change that one), or the pending bound is reached."""
    client = current_client()
    principal = current_client_principal()
    candidate_store, access_log, firm_store = _answer_stores()
    request_body = _answer_body()
    parsed = parse_answer(
        request_body, sections=_sections(firm_store, client), roles=client.roles
    )
    candidate = create_answer(
        parsed,
        case_id=client.case_id,
        origin=CandidateOrigin(
            channel=ANSWER_CHANNEL,
            client_id=principal.client_id,
            subject=client.subject,
        ),
        existing=candidate_store.list_for_case(client.case_id),
    )
    candidate_store.create(candidate)
    _record(access_log, client, "portal.answer")
    _log_answer(client, candidate.id, "created")
    ref = answer_ref(candidate)
    if ref is None:  # pragma: no cover - create_answer writes the locator
        raise RuntimeError("an answer was written without its question")
    return jsonify(answer_json(candidate, ref)), 201


@blueprint.put("/v1/portal/answers/<answer_id>")
@require_client
def update_answer_route(answer_id: str) -> ResponseReturnValue:
    """Change a PENDING answer's value: `{value}`. Edit-while-pending (ADR
    0023 § Writes) — once staff have acted on it, 409, and a change is a
    new answer. Someone else's candidate, or one in a section the firm has
    since switched off, is a 404. The CAS on `pending` means an edit that
    races a reviewer loses rather than rewriting what they just accepted."""
    client = current_client()
    candidate_store, access_log, firm_store = _answer_stores()
    request_body = _answer_body()
    candidate, ref = own_answer_or_404(
        candidate_store.get(client.case_id, answer_id),
        subject=client.subject,
        sections=_sections(firm_store, client),
    )
    revised = revise_answer(candidate, ref, request_body.get("value"))
    if candidate_store.update(revised, expected_status=PENDING) is None:
        raise ConflictError("this answer has already been reviewed")
    _record(access_log, client, "portal.answer")
    _log_answer(client, revised.id, "updated")
    return jsonify(answer_json(revised, ref)), 200


@blueprint.delete("/v1/portal/answers/<answer_id>")
@require_client
def withdraw_answer_route(answer_id: str) -> ResponseReturnValue:
    """Withdraw a PENDING answer — the proposer's retraction
    (`candidates.withdraw`), retained like every other terminal state.
    409 once reviewed; 404 for anything not this client's own."""
    client = current_client()
    candidate_store, access_log, firm_store = _answer_stores()
    candidate, ref = own_answer_or_404(
        candidate_store.get(client.case_id, answer_id),
        subject=client.subject,
        sections=_sections(firm_store, client),
    )
    withdrawn = withdraw(candidate, subject=client.subject)
    if candidate_store.update(withdrawn, expected_status=PENDING) is None:
        raise ConflictError("this answer has already been reviewed")
    _record(access_log, client, "portal.answer")
    _log_answer(client, withdrawn.id, "withdrawn")
    return jsonify(answer_json(withdrawn, ref)), 200
