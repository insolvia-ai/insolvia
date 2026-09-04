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

from flask import Blueprint, jsonify, request
from flask.typing import ResponseReturnValue
from insolvia_core.access_log import record_access
from insolvia_core.candidates import PENDING, candidate_json
from insolvia_core.case_entities import create_entity, entity_json
from insolvia_core.errors import ConflictError, NotFoundError, ValidationError
from insolvia_core.firms import ADD_EDIT, EXTRACTION_REVIEW, VIEW_ONLY
from insolvia_core.ports import AccessLog, CandidateStore, CaseEntityStore, CaseStore

from insolvia_api.api.auth import current_accessor, require_auth, requires
from insolvia_api.api.dependencies import dependencies
from insolvia_api.core.extraction_review import (
    accept,
    build_accepted_draft,
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
    candidates = [
        candidate_json(candidate)
        for candidate in candidate_store.list_for_case(case_id)
        if status is None or candidate.status == status
    ]
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


def _log_reviewed(case_id: str, candidate_id: str, outcome: str) -> None:
    # GLBA: ids and the outcome word — never a payload, never a field count.
    logger.info(
        "candidate reviewed",
        extra={"case_id": case_id, "candidate_id": candidate_id, "outcome": outcome},
    )
