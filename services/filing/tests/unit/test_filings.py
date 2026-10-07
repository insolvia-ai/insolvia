"""The state machine's shape, the record's stored form, and the conditional
writes both stores make."""

from __future__ import annotations

import pytest
from insolvia_filing.adapters.memory.filing_store import MemoryFilingStore
from insolvia_filing.core.filings import (
    BEFORE_SUBMIT,
    STATES,
    TERMINAL,
    TRANSITIONS,
    Confirmation,
    HandBackNote,
    filing_from_item,
    filing_item,
    may_transition,
)


def test_every_state_has_a_transition_row():
    assert set(TRANSITIONS) == set(STATES)


def test_terminal_states_have_no_way_out():
    for state in TERMINAL:
        assert TRANSITIONS[state] == frozenset()


def test_before_the_mark_every_state_may_hand_back():
    for state in BEFORE_SUBMIT:
        assert may_transition(state, "handed_back")
        assert not may_transition(state, "outcome_unknown")


@pytest.mark.parametrize("state", ["at_final_submit", "submitted"])
def test_after_the_mark_nothing_can_be_called_a_hand_back(state):
    assert not may_transition(state, "handed_back")
    assert may_transition(state, "outcome_unknown")


def test_the_only_way_to_submitted_is_through_the_mark():
    sources = [s for s, targets in TRANSITIONS.items() if "submitted" in targets]
    assert sources == ["at_final_submit"]


def test_nothing_goes_backwards():
    order = list(STATES)
    for state, targets in TRANSITIONS.items():
        for target in targets:
            assert order.index(target) > order.index(state)


def test_the_item_round_trips_with_every_member(make_filing):
    filing = make_filing(
        state="filed",
        driver="fake-cmecf/1",
        hand_back=HandBackNote("court_error", "uploading", "t", "a", court_said="x"),
        unknown_reason="submit_timeout",
        confirmation=Confirmation(
            case_number="6:26-bk-10000",
            filed_at="2099-01-15T12:01:00Z",
            docket_entries=("1 Voluntary Petition",),
            receipt_number="FAKE-1",
            fee_due="$338.00",
            page_ref="cases/x/y",
        ),
        receipt_document_id="00000000-0000-4000-8000-0000000000d1",
        follow_ups=("pay_fee",),
    )

    assert filing_from_item(filing_item(filing)) == filing


def test_the_item_lives_in_the_cases_partition(make_filing):
    item = filing_item(make_filing())
    assert item["PK"] == "CASE#00000000-0000-4000-8000-0000000000c1"
    assert item["SK"] == "FILING#00000000-0000-4000-8000-0000000000f1"


def test_a_filing_is_claimed_once(make_filing):
    store = MemoryFilingStore()
    assert store.claim(make_filing())
    assert not store.claim(make_filing(attempt_id="attempt-2"))


def test_a_transition_needs_the_state_and_the_attempt_it_read(make_filing):
    store = MemoryFilingStore()
    store.claim(make_filing())

    assert not store.transition(
        make_filing(state="signed_in"), expected_state="uploading"
    )
    assert not store.transition(
        make_filing(state="signed_in", attempt_id="attempt-2"), expected_state="claimed"
    )
    assert store.transition(make_filing(state="signed_in"), expected_state="claimed")
    assert not store.transition(
        make_filing(state="signed_in"), expected_state="claimed"
    )
