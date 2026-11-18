"""The filing record (ADR 0024 PR 7/8): the state machine's shape, the
stored form, the resolution of a hand-back, and the conditional writes both
stores make — including the one transaction that files the case."""

from __future__ import annotations

from dataclasses import replace

import pytest
from insolvia_core.adapters.memory.case_store import MemoryCaseStore
from insolvia_core.adapters.memory.filing_store import MemoryFilingStore
from insolvia_core.adapters.postgres.case_store import (
    PostgresCaseStore,
    case_from_row,
    case_values,
    write_case,
)
from insolvia_core.adapters.postgres.connection import connect
from insolvia_core.adapters.postgres.filing_store import PostgresFilingStore
from insolvia_core.cases import (
    FILING_WORKER_ACTOR,
    Case,
    StatusChange,
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
from insolvia_core.ports import FilingStore
from psycopg import sql

from tests.unit.backends import BACKENDS, postgres_url_with_cases

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


# ── The stores: memory and Postgres, the same conditions ────────


class World:
    """One case and the filing store that files it, on one backend."""

    def __init__(self, request: pytest.FixtureRequest) -> None:
        self.backend = request.param
        if self.backend == "memory":
            self.cases = MemoryCaseStore()
            self.store: FilingStore = MemoryFilingStore(self.cases)
        else:
            self.url = postgres_url_with_cases(request)
            self.store = PostgresFilingStore(self.url)
        self.put_case(make_case(), new=True)

    def put_case(self, case: Case, *, new: bool = False) -> None:
        """Store `case` whole — as something other than the filing did."""
        if self.backend == "memory":
            self.cases.cases[case.id] = case
            return
        with connect(self.url) as conn:
            if new:
                values = case_values(case)
                conn.execute(
                    sql.SQL("INSERT INTO cases ({}) VALUES ({})").format(
                        sql.SQL(", ").join(map(sql.Identifier, values)),
                        sql.SQL(", ").join([sql.Placeholder()] * len(values)),
                    ),
                    list(values.values()),
                )
            else:
                write_case(conn, case, sql.SQL("TRUE"), [])

    def case(self) -> Case:
        if self.backend == "memory":
            return self.cases.cases[CASE_ID]
        with connect(self.url) as conn:
            row = conn.execute("SELECT * FROM cases WHERE id = %s", (CASE_ID,))
            return case_from_row(row.fetchone())  # type: ignore[arg-type]

    def history(self) -> tuple[StatusChange, ...]:
        if self.backend == "memory":
            return self.cases.status_history(CASE_ID)
        return PostgresCaseStore(self.url).status_history(CASE_ID)


@pytest.fixture(params=BACKENDS)
def world(request: pytest.FixtureRequest) -> World:
    return World(request)


def test_a_filing_is_claimed_once(world):
    store = world.store
    assert store.claim(make_filing())
    assert not store.claim(make_filing(attempt_id="attempt-2"))
    assert store.get(CASE_ID, FILING_ID) == make_filing()


def test_a_transition_needs_the_state_and_the_attempt_it_read(world):
    store = world.store
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


def test_filing_the_record_files_the_case_in_the_same_write(world):
    store = world.store
    store.claim(make_filing(state="submitted"))

    assert store.transition(
        make_filing(state="filed"), expected_state="submitted", filed_case=filed()
    )
    case = world.case()
    assert (case.status, case.case_number, case.filed_at) == (
        "filed",
        "6:26-bk-10000",
        "2099-01-15",
    )
    assert [row.filing_id for row in world.history()] == [FILING_ID]
    assert store.get(CASE_ID, FILING_ID).state == "filed"  # type: ignore[union-attr]


def test_the_case_write_sets_the_lifecycle_and_keeps_every_other_edit(world):
    # Something else changed the case between the worker's read and its
    # write — the § 341 date. The filing's write must not put it back.
    store = world.store
    store.claim(make_filing(state="submitted"))
    write = filed()
    world.put_case(replace(world.case(), meeting_341_at="2099-03-01"))

    assert store.transition(
        make_filing(state="filed"), expected_state="submitted", filed_case=write
    )
    assert world.case().meeting_341_at == "2099-03-01"
    assert world.case().status == "filed"


@pytest.mark.parametrize(
    "moved",
    [
        {"status": "intake"},
        {"firm_id": "another-firm"},
        {"deleted_at": "2099-01-15T00:00:00.000000Z", "deleted_by": "x"},
    ],
)
def test_a_case_that_moved_underneath_writes_nothing_at_all(world, moved):
    if world.backend == "postgres" and "firm_id" in moved:
        pytest.skip("the schema refuses it: a case's children pin its firm (FK)")
    store = world.store
    store.claim(make_filing(state="submitted"))
    write = filed()
    world.put_case(replace(world.case(), **moved))

    assert not store.transition(
        make_filing(state="filed"), expected_state="submitted", filed_case=write
    )
    assert store.get(CASE_ID, FILING_ID).state == "submitted"  # type: ignore[union-attr]
    assert world.history() == ()


def test_a_status_row_already_at_that_instant_writes_nothing_at_all(world):
    store = world.store
    store.claim(make_filing(state="submitted"))
    write = filed()
    store.claim(replace(make_filing(state="submitted"), filing_id="other-filing"))
    assert store.transition(
        replace(make_filing(state="filed"), filing_id="other-filing"),
        expected_state="submitted",
        filed_case=write,
    )
    world.put_case(replace(world.case(), status="ready_to_file"))

    assert not store.transition(
        make_filing(state="filed"), expected_state="submitted", filed_case=write
    )
    assert store.get(CASE_ID, FILING_ID).state == "submitted"  # type: ignore[union-attr]
    assert world.case().status == "ready_to_file"


def test_a_filing_record_that_moved_writes_nothing_to_the_case(world):
    store = world.store
    store.claim(make_filing(state="outcome_unknown"))

    assert not store.transition(
        make_filing(state="filed"), expected_state="submitted", filed_case=filed()
    )
    assert world.case().status == "ready_to_file"


def test_a_hand_back_is_resolved_once(world):
    store = world.store
    store.claim(make_filing(state="handed_back"))
    resolved = make_filing(state="handed_back", resolution=RESOLVED)

    assert store.resolve(resolved, expected_state="handed_back", filed_case=filed())
    assert world.case().status == "filed"
    again = make_filing(
        state="handed_back", resolution=replace(RESOLVED, outcome="not_filed")
    )
    assert not store.resolve(again, expected_state="handed_back")
    assert store.get(CASE_ID, FILING_ID).resolution == RESOLVED  # type: ignore[union-attr]


def test_a_resolution_needs_the_state_it_read(world):
    store = world.store
    store.claim(make_filing(state="outcome_unknown"))
    assert not store.resolve(
        make_filing(state="handed_back", resolution=RESOLVED),
        expected_state="handed_back",
    )
