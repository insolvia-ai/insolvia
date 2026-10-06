"""The extraction review endpoints (issue #89 / 8.9): the per-case candidate
queue, and the one confirmation act that turns a candidate into case data.

WHO MAY CONFIRM IS A PERMISSION, NOT AN ASSUMPTION — the issue's own words.
The feature is `extraction_review`, which has sat in the permission list
defaulting to `hidden` since before this code existed (ADR 0009's
list-before-build rule), so it arrives invisible and a firm turns it on:

    hidden     → 403 on every route here (@requires resolves it)
    view_only  → the queue is readable; every review POST is 403
    add_edit   → the queue is readable and reviewable

The case lookup is still the FIRST gate on every path, exactly as for every
other case child — the permission decides what you may do with a queue you
can already reach, never which firm's queue you reach.

The write path is core/extraction_review.py's; this module is orchestration:
resolve, gate, CAS the candidate, write the record. See that module for why
the candidate is resolved BEFORE the entity is written (the two-reviewers
race) and for the provenance the acceptance mints.
"""

from __future__ import annotations

import logging
from collections.abc import Mapping

from flask import Blueprint, jsonify, request
from flask.typing import ResponseReturnValue
from insolvia_core.access_log import record_access
from insolvia_core.candidates import PENDING, Candidate, candidate_json
from insolvia_core.case_entities import create_entity, entity_json
from insolvia_core.debtors import debtor_json, replace_debtor
from insolvia_core.errors import ConflictError, NotFoundError, ValidationError
from insolvia_core.expenses import HOUSEHOLD
from insolvia_core.firms import ADD_EDIT, EXTRACTION_REVIEW, VIEW_ONLY
from insolvia_core.ports import (
    AccessLog,
    CandidateStore,
    CaseEntityStore,
    CaseStore,
    DebtorStore,
)
from insolvia_core.questions import answer_ref, review_question_json

from insolvia_api.api.auth import current_accessor, require_auth, requires
from insolvia_api.api.dependencies import dependencies
from insolvia_api.core.extraction_review import (
    CLIENT_LINKS,
    accept,
    answer_filing_role,
    build_accepted_debtor,
    build_accepted_draft,
    fill_client_links,
    is_debtor_answer,
    parse_review,
    parse_status_filter,
    reject,
    resolve_candidate_references,
    review_moment,
    reviewable_kind,
)

logger = logging.getLogger(__name__)

blueprint = Blueprint("extraction_review", __name__)

# The corrected payload is an entity body — the entity routes' own ceiling.
MAX_REQUEST_BYTES = 256 * 1024


def _stores() -> tuple[CaseStore, CandidateStore, CaseEntityStore, AccessLog]:
    deps = dependencies()
    if (
        deps.case_store is None
        or deps.candidate_store is None
        or deps.case_entity_store is None
        or deps.access_log is None
    ):
        raise RuntimeError(
            "the case, candidate and entity stores and access log are not composed"
        )
    return (
        deps.case_store,
        deps.candidate_store,
        deps.case_entity_store,
        deps.access_log,
    )


def _json_body() -> dict[str, object]:
    if request.content_length and request.content_length > MAX_REQUEST_BYTES:
        raise ValidationError("request body exceeds 256 KiB")
    payload = request.get_json(silent=True)
    if not isinstance(payload, dict):
        raise ValidationError("request body must be a JSON object")
    return payload


def _reachable_case_or_404(case_id: str, action: str) -> None:
    """Resolve the case first and record the attempt — the entity routes'
    rule verbatim, because a candidate holds the same case-shaped values its
    target record would."""
    case_store, _, _, access_log = _stores()
    accessor = current_accessor()
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
        raise NotFoundError("case not found")


def _client_names(case_id: str) -> dict[str, str]:
    """subject → the display name the FIRM gave at invitation, for every
    binding the case has ever had (revoked ones too: an answer outlives
    its author's access). From the binding, never a token — ADR 0023 §
    Writes: a client cannot rename themselves in the review queue."""
    store = dependencies().client_binding_store
    if store is None:
        return {}
    return {
        binding.subject: binding.display_name
        for binding in store.list_for_case(case_id)
    }


def _queue_json(candidate: Candidate, names: Mapping[str, str]) -> dict[str, object]:
    """One queue row: the candidate, and — for a client's portal answer —
    the question it answers (section, text, the debtor it is for) and who
    the firm says gave it. ADR 0023 § Writes: "From the client", the
    display name from the binding, and the section and question answered."""
    body = candidate_json(candidate)
    ref = answer_ref(candidate)
    if ref is not None:
        body["question"] = review_question_json(ref)
        name = names.get(candidate.origin.subject)
        if name is not None:
            body["client"] = {"displayName": name}
    return body


@blueprint.get("/v1/cases/<case_id>/extraction/candidates")
@require_auth
@requires(EXTRACTION_REVIEW, VIEW_ONLY)
def list_candidates_route(case_id: str) -> ResponseReturnValue:
    """The case's review queue, creation order, both streams — extraction's
    and the MCP surface's — distinguishable only by the origin each row
    displays. `?status=` narrows (the review screen reads `pending`);
    unfiltered is the full history, corrections and rejections included,
    because they are the quality feedback loop and are retained on purpose.
    """
    _, candidate_store, _, _ = _stores()
    status = parse_status_filter(request.args.get("status"))
    _reachable_case_or_404(case_id, "extraction.read")
    listed = [
        candidate
        for candidate in candidate_store.list_for_case(case_id)
        if status is None or candidate.status == status
    ]
    names = (
        _client_names(case_id)
        if any(candidate.origin.channel == "client" for candidate in listed)
        else {}
    )
    candidates = [_queue_json(candidate, names) for candidate in listed]
    return jsonify({"candidates": candidates}), 200


@blueprint.post("/v1/cases/<case_id>/extraction/candidates/<candidate_id>/review")
@require_auth
@requires(EXTRACTION_REVIEW, ADD_EDIT)
def review_candidate_route(case_id: str, candidate_id: str) -> ResponseReturnValue:
    """Accept (optionally corrected) or reject ONE pending candidate.

    Acceptance writes the case record through the same parse as every
    staff-typed write, with the provenance core/extraction_review.py mints —
    which is what makes this the only door from the queue into the case, and
    a door the store itself checks. An already-reviewed candidate answers
    409: the row exists, the caller may see it, and its state refuses — a
    second reviewer must learn they lost, not overwrite the outcome.
    """
    _, candidate_store, entity_store, _ = _stores()
    accessor = current_accessor()

    decision = parse_review(_json_body())
    _reachable_case_or_404(case_id, "extraction.review")

    candidate = candidate_store.get(case_id, candidate_id)
    if candidate is None:
        raise NotFoundError("candidate not found")
    if candidate.status != PENDING:
        raise ConflictError(
            f"candidate has already been reviewed (status: {candidate.status})"
        )
    moment = review_moment()

    if decision.action == "reject":
        rejected = reject(candidate, confirmed_by=accessor.subject, confirmed_at=moment)
        if candidate_store.update(rejected, expected_status=PENDING) is None:
            raise ConflictError("candidate was reviewed by someone else")
        _log_reviewed(rejected.case_id, rejected.id, "rejected")
        return jsonify({"candidate": candidate_json(rejected)}), 200

    if is_debtor_answer(candidate):
        return _accept_debtor_answer(candidate, decision.corrected_payload, moment)

    # Accept. Kind first (an unreviewable type is a 400 before any write),
    # then references, then the draft with its minted provenance.
    kind = reviewable_kind(candidate)
    siblings = {
        sibling.id: sibling
        for sibling in candidate_store.list_for_case(case_id)
        if sibling.id != candidate.id
    }
    payload = resolve_candidate_references(
        candidate.entity_type,
        decision.corrected_payload
        if decision.corrected_payload is not None
        else candidate.payload,
        siblings=siblings,
    )
    payload = _with_client_links(candidate, payload)
    draft = build_accepted_draft(
        candidate, payload, confirmed_by=accessor.subject, confirmed_at=moment
    )
    entity = create_entity(kind, draft, case_id=case_id)

    # CAS the candidate BEFORE the record exists: the loser of a review race
    # must conflict here, with nothing written — see the core module.
    reviewed = accept(
        candidate,
        corrected_payload=decision.corrected_payload,
        resulting_record_id=entity.id,
        confirmed_by=accessor.subject,
        confirmed_at=moment,
    )
    if candidate_store.update(reviewed, expected_status=PENDING) is None:
        raise ConflictError("candidate was reviewed by someone else")
    entity_store.create(entity)

    _log_reviewed(case_id, candidate.id, reviewed.status)
    return (
        jsonify({"candidate": candidate_json(reviewed), "record": entity_json(entity)}),
        200,
    )


def _debtor_store() -> DebtorStore:
    store = dependencies().debtor_store
    if store is None:
        raise RuntimeError("the debtor store is not composed")
    return store


def _with_client_links(
    candidate: Candidate, payload: Mapping[str, object]
) -> Mapping[str, object]:
    """A client record answer's link, from the case: the answering debtor's
    record for an employment, the main household for an expense
    (`core.extraction_review.CLIENT_LINKS`). Read here, on the STAFF side,
    because the portal never reads case records and so knows no ids."""
    field = CLIENT_LINKS.get(candidate.entity_type)
    if candidate.origin.channel != "client" or field is None:
        return payload
    debtor_id: str | None = None
    household_id: str | None = None
    if field == "debtor_id":
        debtor = _debtor_store().get(
            candidate.case_id, filing_role=answer_filing_role(candidate)
        )
        debtor_id = debtor.id if debtor is not None else None
    else:
        _, _, entity_store, _ = _stores()
        household_id = next(
            (
                household.id
                for household in entity_store.list_for_case(
                    candidate.case_id, HOUSEHOLD
                )
                if household.body.which_household == "main"
            ),
            None,
        )
    return fill_client_links(
        candidate, payload, debtor_id=debtor_id, household_id=household_id
    )


def _accept_debtor_answer(
    candidate: Candidate,
    corrected_payload: Mapping[str, object] | None,
    moment: str,
) -> ResponseReturnValue:
    """Accept a client's answer about a debtor's own fields by MERGING it
    into that debtor's record (core.extraction_review.build_accepted_debtor)
    — the debtor is keyed by filing role and already exists from the moment
    the case was opened (ADR 0022), so an answer is a change to it, never a
    new record. Debtor 2 that has not been linked yet is a 409 naming the
    fix, exactly as the debtor PUT refuses to mint one.

    The candidate is CAS'd before the debtor is written, the route's rule
    for the two-reviewers race. Two different answers accepted at the same
    moment are last-write-wins on the debtor row, the same as two staff
    saves of the questionnaire."""
    _, candidate_store, _, _ = _stores()
    accessor = current_accessor()
    debtor_store = _debtor_store()
    role = answer_filing_role(candidate)
    stored = debtor_store.get(candidate.case_id, filing_role=role)
    if stored is None:
        raise ConflictError(
            "link a client to this debtor before accepting answers about them"
        )
    draft = build_accepted_debtor(
        candidate,
        corrected_payload if corrected_payload is not None else candidate.payload,
        stored=stored,
        confirmed_by=accessor.subject,
        confirmed_at=moment,
    )
    debtor = replace_debtor(stored, draft, tax_id=stored.tax_id)
    reviewed = accept(
        candidate,
        corrected_payload=corrected_payload,
        resulting_record_id=debtor.id,
        confirmed_by=accessor.subject,
        confirmed_at=moment,
    )
    if candidate_store.update(reviewed, expected_status=PENDING) is None:
        raise ConflictError("candidate was reviewed by someone else")
    debtor_store.put(debtor)
    _log_reviewed(candidate.case_id, candidate.id, reviewed.status)
    return (
        jsonify({"candidate": candidate_json(reviewed), "record": debtor_json(debtor)}),
        200,
    )


def _log_reviewed(case_id: str, candidate_id: str, outcome: str) -> None:
    # GLBA: ids and the outcome word — never a payload, never a field count.
    logger.info(
        "candidate reviewed",
        extra={"case_id": case_id, "candidate_id": candidate_id, "outcome": outcome},
    )
