from __future__ import annotations

import pytest
from insolvia_core.errors import (
    ConflictError,
    FieldValidationError,
    ForbiddenError,
    ValidationError,
)
from insolvia_mcp.core.candidates import (
    MAX_PROPOSALS_PER_CALL,
    PENDING,
    PROPOSABLE_ENTITY_TYPES,
    WITHDRAWN,
    CandidateOrigin,
    candidate_from_item,
    candidate_item,
    candidate_review_json,
    create_candidate,
    parse_proposals,
    withdraw,
)

ORIGIN = CandidateOrigin(channel="mcp", client_id="client-1", subject="subject-1")

CREDITOR_PROPOSAL = {
    "entityType": "creditors",
    "payload": {"name": "First Example Bank"},
}


def _candidate(**overrides: object):
    drafts = parse_proposals([CREDITOR_PROPOSAL])
    candidate = create_candidate(drafts[0], case_id="case-1", origin=ORIGIN)
    if overrides:
        from dataclasses import replace

        candidate = replace(candidate, **overrides)  # type: ignore[arg-type]
    return candidate


# ── parse_proposals ─────────────────────────────────────────────────


def test_a_valid_batch_parses_whole() -> None:
    drafts = parse_proposals(
        [
            CREDITOR_PROPOSAL,
            {
                "entityType": "claims",
                "payload": {},
                "externalRef": {
                    "system": "mycase",
                    "externalId": "42",
                    "externalUrl": "https://example.invalid/42",
                },
                "note": "from the PMS ledger",
            },
        ]
    )
    assert [d.entity_type for d in drafts] == ["creditors", "claims"]
    assert drafts[1].external_ref is not None
    assert drafts[1].external_ref.system == "mycase"
    assert drafts[1].note == "from the PMS ledger"


def test_debtor_proposals_run_the_debtor_parser() -> None:
    (draft,) = parse_proposals(
        [{"entityType": "debtors", "payload": {"filing_role": "debtor_1"}}]
    )
    assert draft.entity_type == "debtors"


@pytest.mark.parametrize(
    "proposals",
    [
        "not-a-list",
        [],
        [CREDITOR_PROPOSAL] * (MAX_PROPOSALS_PER_CALL + 1),
    ],
)
def test_batch_shape_violations_are_rejected(proposals: object) -> None:
    with pytest.raises(ValidationError):
        parse_proposals(proposals)


@pytest.mark.parametrize(
    ("proposal", "failing_field"),
    [
        ("not-an-object", "proposals[0]"),
        ({"entityType": "documents", "payload": {}}, "proposals[0].entityType"),
        ({"entityType": "cases", "payload": {}}, "proposals[0].entityType"),
        ({"entityType": "creditors"}, "proposals[0].payload"),
        (
            {"entityType": "creditors", "payload": {"name": 42}},
            "proposals[0].payload.name",
        ),
        (
            {"entityType": "creditors", "payload": {}, "note": 7},
            "proposals[0].note",
        ),
        (
            {"entityType": "creditors", "payload": {}, "externalRef": "x"},
            "proposals[0].externalRef",
        ),
        (
            {"entityType": "creditors", "payload": {}, "externalRef": {"system": "s"}},
            "proposals[0].externalRef.externalId",
        ),
    ],
)
def test_malformed_proposals_name_the_field(
    proposal: object, failing_field: str
) -> None:
    with pytest.raises(FieldValidationError) as excinfo:
        parse_proposals([proposal])
    assert failing_field in excinfo.value.fields


def test_a_bad_third_proposal_rejects_the_whole_batch() -> None:
    with pytest.raises(FieldValidationError) as excinfo:
        parse_proposals([CREDITOR_PROPOSAL, CREDITOR_PROPOSAL, {"entityType": "nope"}])
    assert "proposals[2].entityType" in excinfo.value.fields


def test_documents_and_the_case_root_are_not_proposable() -> None:
    assert "documents" not in PROPOSABLE_ENTITY_TYPES
    assert "cases" not in PROPOSABLE_ENTITY_TYPES


# ── withdrawal ──────────────────────────────────────────────────────


def test_the_proposer_withdraws_a_pending_candidate() -> None:
    withdrawn = withdraw(_candidate(), subject="subject-1")
    assert withdrawn.status == WITHDRAWN


def test_only_the_proposer_may_withdraw() -> None:
    with pytest.raises(ForbiddenError):
        withdraw(_candidate(), subject="somebody-else")


@pytest.mark.parametrize("status", ["accepted", "corrected", "rejected", "withdrawn"])
def test_a_reviewed_candidate_refuses_withdrawal(status: str) -> None:
    with pytest.raises(ConflictError):
        withdraw(_candidate(status=status), subject="subject-1")


# ── the stored item shape ───────────────────────────────────────────


def test_candidate_round_trips_through_its_item() -> None:
    drafts = parse_proposals(
        [
            {
                **CREDITOR_PROPOSAL,
                "externalRef": {"system": "mycase", "externalId": "42"},
                "note": "check the address",
            }
        ]
    )
    candidate = create_candidate(drafts[0], case_id="case-1", origin=ORIGIN)
    item = candidate_item(candidate)
    assert item["PK"] == "CASE#case-1"
    assert item["SK"] == f"CANDIDATE#{candidate.id}"
    assert candidate_from_item(item) == candidate


def test_new_candidates_are_pending_with_the_verified_origin() -> None:
    candidate = _candidate()
    assert candidate.status == PENDING
    assert candidate.origin == ORIGIN
    assert candidate.document_id is None


def test_review_json_omits_absent_outcomes() -> None:
    row = candidate_review_json(_candidate())
    assert set(row) == {"candidateId", "entityType", "status"}


def test_review_json_carries_the_humans_outcome() -> None:
    candidate = _candidate(
        status="corrected",
        confirmed_by="reviewer-1",
        confirmed_at="2026-09-01T00:00:00.000Z",
        corrected_payload={"name": "First Example Bank, N.A."},
        resulting_record_id="record-9",
    )
    row = candidate_review_json(candidate)
    assert row["status"] == "corrected"
    assert row["correctedPayload"] == {"name": "First Example Bank, N.A."}
    assert row["resultingRecordId"] == "record-9"
