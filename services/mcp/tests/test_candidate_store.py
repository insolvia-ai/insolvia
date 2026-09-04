from __future__ import annotations

from dataclasses import replace

import pytest
from insolvia_mcp.adapters.memory.candidate_store import MemoryCandidateStore
from insolvia_mcp.core.candidates import (
    CandidateOrigin,
    create_candidate,
    parse_proposals,
)

ORIGIN = CandidateOrigin(channel="mcp", client_id="client-1", subject="subject-1")


def _candidate(case_id: str = "case-1"):
    (draft,) = parse_proposals(
        [{"entityType": "creditors", "payload": {"name": "First Example Bank"}}]
    )
    return create_candidate(draft, case_id=case_id, origin=ORIGIN)


@pytest.fixture
def store() -> MemoryCandidateStore:
    return MemoryCandidateStore()


def test_created_candidates_are_read_back(store) -> None:
    candidate = _candidate()
    store.create(candidate)
    assert store.get("case-1", candidate.id) == candidate


def test_create_refuses_to_overwrite(store) -> None:
    candidate = _candidate()
    store.create(candidate)
    with pytest.raises(RuntimeError):
        store.create(candidate)


def test_a_candidate_does_not_resolve_under_another_case(store) -> None:
    candidate = _candidate()
    store.create(candidate)
    assert store.get("case-2", candidate.id) is None


def test_listing_is_case_scoped_and_creation_ordered(store) -> None:
    first = _candidate()
    second = replace(_candidate(), created_at=first.created_at + "z")
    other_case = _candidate(case_id="case-2")
    store.create(second)
    store.create(first)
    store.create(other_case)
    assert store.list_for_case("case-1") == (first, second)


def test_update_is_a_compare_and_swap_on_status(store) -> None:
    candidate = _candidate()
    store.create(candidate)
    withdrawn = replace(candidate, status="withdrawn")
    assert store.update(withdrawn, expected_status="pending") == withdrawn
    # A second withdrawal — or one racing an acceptance — loses.
    assert store.update(withdrawn, expected_status="pending") is None


def test_update_of_a_missing_row_fails(store) -> None:
    candidate = _candidate()
    assert store.update(candidate, expected_status="pending") is None
