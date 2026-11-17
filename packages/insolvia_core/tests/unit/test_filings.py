"""The filing record (ADR 0024 PR 7/8): the state machine's shape, the
stored form, the resolution of a hand-back, and the conditional writes both
stores make — including the one transaction that files the case."""

from __future__ import annotations

from dataclasses import replace
from typing import Any

import pytest
from botocore.exceptions import ClientError
from insolvia_core.adapters.aws.filing_store import DynamoDbFilingStore
from insolvia_core.adapters.memory.case_store import MemoryCaseStore
from insolvia_core.adapters.memory.filing_store import MemoryFilingStore
from insolvia_core.cases import (
    FILING_WORKER_ACTOR,
    Case,
    file_case,
    status_change_from_item,
    status_change_item,
    status_change_json,
)
from insolvia_core.errors import ConflictError, FieldValidationError
from insolvia_core.filings import (
    BEFORE_SUBMIT,
    STATES,
    TERMINAL,
    TRANSITIONS,
    Confirmation,
    FiledCase,
    Filing,
    HandBackNote,
    Resolution,
    Step,
    filing_from_item,
    filing_item,
    filing_json,
    frees_case,
    is_resolvable,
    may_transition,
)

CASE_ID = "00000000-0000-4000-8000-0000000000c1"
FILING_ID = "00000000-0000-4000-8000-0000000000f1"
FIRM = "proof-firm-00000000"
ATTORNEY = "00000000-0000-4000-8000-00000000a771"


def make_filing(**changes: object) -> Filing:
    base = Filing(
        filing_id=FILING_ID,
        case_id=CASE_ID,
        approval_id="00000000-0000-4000-8000-0000000000a1",
        firm_id=FIRM,
        attorney_id=ATTORNEY,
        credential_id="00000000-0000-4000-8000-0000000000e1",
        court="flmb",
        division="3",
        state="claimed",
        attempt_id="attempt-1",
        claimed_at="2099-01-15T12:00:00.000Z",
        lease_expires_at=4071989400,
        updated_at="2099-01-15T12:00:00.000Z",
        history=(Step("claimed", "2099-01-15T12:00:00.000Z"),),
    )
    return replace(base, **changes)  # type: ignore[arg-type]


def make_case(**changes: object) -> Case:
    base = Case(
        id=CASE_ID,
        firm_id=FIRM,
        created_by=ATTORNEY,
        chapter=7,
        district="Middle District of Florida",
        status="ready_to_file",
        created_at="2099-01-01T00:00:00.000000Z",
        updated_at="2099-01-01T00:00:00.000000Z",
        court="flmb",
        division="3",
        meeting_341_at="2099-02-20",
    )
    return replace(base, **changes)  # type: ignore[arg-type]


def filed(case: Case | None = None) -> FiledCase:
    before = case or make_case()
    after, change = file_case(
        before,
        case_number="6:26-bk-10000",
        filed_at="2099-01-15",
        changed_by=FILING_WORKER_ACTOR,
        filing_id=FILING_ID,
    )
    return FiledCase(case=after, expected_status=before.status, status_change=change)


RESOLVED = Resolution(
    outcome="filed",
    resolved_by=ATTORNEY,
    resolved_at="2099-01-16T09:00:00.000Z",
    docket_checked_at="2099-01-16T09:00:00.000Z",
    case_number="6:26-bk-10000",
    filed_at="2099-01-15",
    confirmation_document_id="00000000-0000-4000-8000-0000000000d2",
)


# ── The state machine ───────────────────────────────────────────


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


# ── The stored form ─────────────────────────────────────────────


def test_the_item_round_trips_with_every_member():
    filing = make_filing(
        state="outcome_unknown",
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
        resolution=RESOLVED,
    )

    assert filing_from_item(filing_item(filing)) == filing


def test_the_item_lives_in_the_cases_partition():
    item = filing_item(make_filing())
    assert item["PK"] == f"CASE#{CASE_ID}"
    assert item["SK"] == f"FILING#{FILING_ID}"


def test_the_wire_shape_never_carries_the_page_s_storage_key():
    filing = make_filing(
        state="filed",
        confirmation=Confirmation(
            case_number="6:26-bk-10000",
            filed_at="2099-01-15T12:01:00Z",
            docket_entries=(),
            page_ref="cases/x/filings/y/z",
        ),
    )
    body = filing_json(filing)
    assert body["confirmation"] == {
        "caseNumber": "6:26-bk-10000",
        "filedAt": "2099-01-15T12:01:00Z",
        "docketEntries": [],
    }
    assert "cases/x" not in repr(body)
    # ...and the record itself still has it.
    assert filing_item(filing)["confirmation"]["pageRef"] == "cases/x/filings/y/z"  # type: ignore[index]


# ── Resolution ──────────────────────────────────────────────────


@pytest.mark.parametrize("state", sorted(STATES))
def test_only_a_hand_back_or_an_unknown_outcome_is_resolvable(state):
    filing = make_filing(state=state)
    assert is_resolvable(filing) == (state in {"handed_back", "outcome_unknown"})
    assert filing_json(filing)["resolvable"] == is_resolvable(filing)


def test_a_resolved_filing_is_not_resolvable_again():
    assert not is_resolvable(make_filing(state="handed_back", resolution=RESOLVED))


def test_only_a_not_filed_resolution_frees_the_case():
    not_filed = replace(RESOLVED, outcome="not_filed", case_number=None, filed_at=None)
    assert frees_case(make_filing(state="handed_back", resolution=not_filed))
    assert frees_case(make_filing(state="outcome_unknown", resolution=not_filed))
    assert not frees_case(make_filing(state="handed_back", resolution=RESOLVED))
    assert not frees_case(make_filing(state="handed_back"))
    assert not frees_case(make_filing(state="filed"))


# ── file_case: the lifecycle move a filing makes ────────────────


def test_a_filing_moves_the_case_through_the_lifecycle_with_its_history_row():
    write = filed()
    assert write.case.status == "filed"
    assert write.case.case_number == "6:26-bk-10000"
    assert write.case.filed_at == "2099-01-15"
    assert write.expected_status == "ready_to_file"
    change = write.status_change
    assert (change.from_status, change.to_status) == ("ready_to_file", "filed")
    assert change.changed_by == FILING_WORKER_ACTOR
    assert change.filing_id == FILING_ID
    assert change.changed_at == write.case.updated_at


def test_the_history_row_says_which_filing_and_round_trips():
    change = filed().status_change
    assert status_change_from_item(status_change_item(change)) == change
    assert status_change_json(change)["filingId"] == FILING_ID


def test_a_filed_case_is_not_filed_again():
    with pytest.raises(ConflictError):
        file_case(
            make_case(status="filed", filed_at="2099-01-01", case_number="x"),
            case_number="6:26-bk-10000",
            filed_at="2099-01-15",
            changed_by=ATTORNEY,
            filing_id=FILING_ID,
        )


@pytest.mark.parametrize(
    ("number", "date", "field"),
    [("", "2099-01-15", "case_number"), ("6:26-bk-1", "15/01/2099", "filed_at")],
)
def test_a_filing_needs_its_number_and_a_petition_date(number, date, field):
    with pytest.raises(FieldValidationError) as refused:
        file_case(
            make_case(),
            case_number=number,
            filed_at=date,
            changed_by=ATTORNEY,
            filing_id=FILING_ID,
        )
    assert field in refused.value.fields


# ── The memory store: the same conditions as DynamoDB ───────────


def test_a_filing_is_claimed_once():
    store = MemoryFilingStore()
    assert store.claim(make_filing())
    assert not store.claim(make_filing(attempt_id="attempt-2"))


def test_a_transition_needs_the_state_and_the_attempt_it_read():
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


def _stores(case: Case | None = None) -> tuple[MemoryCaseStore, MemoryFilingStore]:
    cases = MemoryCaseStore()
    stored = case or make_case()
    cases.cases[stored.id] = stored
    return cases, MemoryFilingStore(cases)


def test_filing_the_record_files_the_case_in_the_same_write():
    cases, store = _stores()
    store.claim(make_filing(state="submitted"))

    assert store.transition(
        make_filing(state="filed"), expected_state="submitted", filed_case=filed()
    )
    case = cases.cases[CASE_ID]
    assert (case.status, case.case_number, case.filed_at) == (
        "filed",
        "6:26-bk-10000",
        "2099-01-15",
    )
    assert [row.filing_id for row in cases.status_history(CASE_ID)] == [FILING_ID]
    assert store.get(CASE_ID, FILING_ID).state == "filed"  # type: ignore[union-attr]


def test_the_case_write_sets_the_lifecycle_and_keeps_every_other_edit():
    # Something else changed the case between the worker's read and its
    # write — the § 341 date. The filing's write must not put it back.
    cases, store = _stores()
    store.claim(make_filing(state="submitted"))
    write = filed()
    cases.cases[CASE_ID] = replace(cases.cases[CASE_ID], meeting_341_at="2099-03-01")

    assert store.transition(
        make_filing(state="filed"), expected_state="submitted", filed_case=write
    )
    assert cases.cases[CASE_ID].meeting_341_at == "2099-03-01"


@pytest.mark.parametrize(
    "moved",
    [
        {"status": "intake"},
        {"firm_id": "another-firm"},
        {"deleted_at": "2099-01-15T00:00:00Z", "deleted_by": "x"},
    ],
)
def test_a_case_that_moved_underneath_writes_nothing_at_all(moved):
    cases, store = _stores()
    store.claim(make_filing(state="submitted"))
    write = filed()
    cases.cases[CASE_ID] = replace(cases.cases[CASE_ID], **moved)  # type: ignore[arg-type]

    assert not store.transition(
        make_filing(state="filed"), expected_state="submitted", filed_case=write
    )
    assert store.get(CASE_ID, FILING_ID).state == "submitted"  # type: ignore[union-attr]
    assert cases.status_history(CASE_ID) == ()


def test_a_filing_record_that_moved_writes_nothing_to_the_case():
    cases, store = _stores()
    store.claim(make_filing(state="outcome_unknown"))

    assert not store.transition(
        make_filing(state="filed"), expected_state="submitted", filed_case=filed()
    )
    assert cases.cases[CASE_ID].status == "ready_to_file"


def test_a_hand_back_is_resolved_once():
    cases, store = _stores()
    store.claim(make_filing(state="handed_back"))
    resolved = make_filing(state="handed_back", resolution=RESOLVED)

    assert store.resolve(resolved, expected_state="handed_back", filed_case=filed())
    assert cases.cases[CASE_ID].status == "filed"
    again = make_filing(
        state="handed_back", resolution=replace(RESOLVED, outcome="not_filed")
    )
    assert not store.resolve(again, expected_state="handed_back")
    assert store.get(CASE_ID, FILING_ID).resolution == RESOLVED  # type: ignore[union-attr]


def test_a_resolution_needs_the_state_it_read():
    _, store = _stores()
    store.claim(make_filing(state="outcome_unknown"))
    assert not store.resolve(
        make_filing(state="handed_back", resolution=RESOLVED),
        expected_state="handed_back",
    )


# ── The DynamoDB adapter: the calls it makes ────────────────────


class FakeDynamoDb:
    def __init__(self, fail: str | None = None) -> None:
        self.calls: list[tuple[str, dict[str, Any]]] = []
        self.fail = fail

    def _call(self, name: str, kwargs: dict[str, Any]) -> dict[str, Any]:
        self.calls.append((name, kwargs))
        if self.fail is not None:
            raise ClientError({"Error": {"Code": self.fail}}, name)
        return {}

    def put_item(self, **kwargs: Any) -> dict[str, Any]:
        return self._call("put_item", kwargs)

    def get_item(self, **kwargs: Any) -> dict[str, Any]:
        return self._call("get_item", kwargs)

    def transact_write_items(self, **kwargs: Any) -> dict[str, Any]:
        return self._call("transact_write_items", kwargs)


def test_a_claim_is_conditional_on_no_record():
    fake = FakeDynamoDb()
    DynamoDbFilingStore("t", client=fake).claim(make_filing())
    assert fake.calls[0][1]["ConditionExpression"] == "attribute_not_exists(PK)"


def test_a_transition_is_conditional_on_the_state_and_the_attempt():
    fake = FakeDynamoDb()
    DynamoDbFilingStore("t", client=fake).transition(
        make_filing(state="signed_in"), expected_state="claimed"
    )
    name, call = fake.calls[0]
    assert name == "put_item"
    assert call["ConditionExpression"] == "#state = :expected AND attemptId = :attempt"
    assert call["ExpressionAttributeValues"] == {
        ":expected": {"S": "claimed"},
        ":attempt": {"S": "attempt-1"},
    }


def test_a_resolution_is_also_conditional_on_there_being_none():
    fake = FakeDynamoDb()
    DynamoDbFilingStore("t", client=fake).resolve(
        make_filing(state="handed_back", resolution=RESOLVED),
        expected_state="handed_back",
    )
    assert fake.calls[0][1]["ConditionExpression"].endswith(
        "AND attribute_not_exists(resolution)"
    )


def test_filing_the_case_is_one_transaction_of_three_conditional_items():
    fake = FakeDynamoDb()
    write = filed()
    DynamoDbFilingStore("t", client=fake).transition(
        make_filing(state="filed"), expected_state="submitted", filed_case=write
    )
    name, call = fake.calls[0]
    assert name == "transact_write_items"
    record, case, history = call["TransactItems"]
    assert record["Put"]["Item"]["SK"] == {"S": f"FILING#{FILING_ID}"}
    update = case["Update"]
    assert update["Key"] == {"PK": {"S": f"CASE#{CASE_ID}"}, "SK": {"S": "META"}}
    assert update["ConditionExpression"] == (
        "attribute_exists(PK) AND firmId = :firm AND #status = :read"
        " AND attribute_not_exists(deletedAt)"
    )
    assert update["ExpressionAttributeValues"][":read"] == {"S": "ready_to_file"}
    # Exactly the lifecycle attributes — the rest of the case is untouched.
    set_names = {
        update["ExpressionAttributeNames"][token.split(" = ")[0]]
        for token in update["UpdateExpression"].removeprefix("SET ").split(", ")
    }
    assert set_names == {
        "status",
        "filedAt",
        "caseNumber",
        "caseNumberKey",
        "updatedAt",
    }
    assert update["ExpressionAttributeValues"][":key"] == {"S": "flmb:6:26-bk-10000"}
    assert history["Put"]["Item"]["filingId"] == {"S": FILING_ID}
    assert history["Put"]["ConditionExpression"] == "attribute_not_exists(SK)"


@pytest.mark.parametrize(
    "code", ["ConditionalCheckFailedException", "TransactionCanceledException"]
)
def test_a_lost_condition_is_false_not_an_error(code):
    store = DynamoDbFilingStore("t", client=FakeDynamoDb(fail=code))
    assert store.claim(make_filing()) is False
    assert store.transition(make_filing(), expected_state="claimed") is False
    assert (
        store.transition(
            make_filing(state="filed"), expected_state="submitted", filed_case=filed()
        )
        is False
    )


def test_another_failure_is_raised():
    store = DynamoDbFilingStore("t", client=FakeDynamoDb(fail="ThrottlingException"))
    with pytest.raises(ClientError):
        store.claim(make_filing())


def test_reads_are_strongly_consistent():
    fake = FakeDynamoDb()
    assert DynamoDbFilingStore("t", client=fake).get(CASE_ID, FILING_ID) is None
    assert fake.calls[0][1]["ConsistentRead"] is True
