"""The case lifecycle as data (issue 14.3 / #355): intake to closed and its moves,
the post-filing docket facts, the history row a move writes, archive and
soft delete in both stores, and copy-case's provenance.

Route-level behaviour — who may do each, what the API answers — is
services/api's test_case_lifecycle_routes.py; this file pins the domain rule.
"""

from __future__ import annotations

from dataclasses import replace
from typing import Any

import pytest
from botocore.exceptions import ClientError
from insolvia_core.access import Accessor, may_see_case
from insolvia_core.adapters.aws import case_store as aws_case_store
from insolvia_core.adapters.aws.case_store import DynamoDbCaseStore
from insolvia_core.adapters.aws.dynamo import to_attributes
from insolvia_core.adapters.memory.case_store import MemoryCaseStore
from insolvia_core.case_copy import copy_case
from insolvia_core.case_entities import CaseEntity
from insolvia_core.cases import (
    STATUSES,
    TRANSITIONS,
    Case,
    apply_changes,
    case_from_item,
    case_item,
    case_json,
    create_case,
    deletion_refusal,
    is_filed,
    mark_deleted,
    parse_case_creation,
    parse_case_update,
    set_archived,
    status_change,
    status_change_from_item,
    status_change_item,
)
from insolvia_core.clients import public_status_from_case_item
from insolvia_core.creditors import CREDITOR, CreditorBody
from insolvia_core.debtors import Debtor
from insolvia_core.errors import ConflictError, FieldValidationError, ValidationError
from insolvia_core.firms import Firm, FirmUser
from insolvia_core.provenance import ProvenanceEntry, parse_provenance, provenance_json

FIRM = "00000000-0000-4000-8000-00000000f18a"
ALICE = "00000000-0000-4000-8000-00000000a11c"
CLIENT_A = "00000000-0000-4000-8000-0000000c11a0"
OPENING = {"chapter": 7, "court": "flmb", "division": "tampa", "client_ids": [CLIENT_A]}
FILING = {"status": "filed", "filed_at": "2026-09-01", "case_number": "8:26-bk-01234"}


def a_case(**overrides: object) -> Case:
    case, _ = create_case(parse_case_creation(OPENING), firm_id=FIRM, created_by=ALICE)
    return replace(case, **overrides)  # type: ignore[arg-type]


def moved(case: Case, payload: dict[str, object]) -> Case:
    return apply_changes(case, parse_case_update(payload))


def admin() -> Accessor:
    firm = Firm(
        id=FIRM, name="Example", status="active", created_at="t", updated_at="t"
    )
    user = FirmUser(
        firm_id=FIRM,
        subject=ALICE,
        email="alice@example.test",
        first_name="Alice",
        last_name="Example",
        role="attorney",
        is_admin=True,
        access_all_cases=False,
        permissions={},
        status="active",
        created_at="t",
        updated_at="t",
    )
    return Accessor(firm=firm, user=user)


# ── Opening a case: always retained ─────────────────────────────────


@pytest.mark.parametrize("asked", ["prospect", "filed", "ready_to_file"])
def test_a_case_opens_retained_whatever_the_body_asks(asked):
    # Status is not a creation field: the funnel before retention is the
    # client's (firm_clients.PROSPECT_STAGES), and a case is not born filed.
    case, _ = create_case(
        parse_case_creation({**OPENING, "status": asked}),
        firm_id=FIRM,
        created_by=ALICE,
    )
    assert case.status == "intake"


def test_prospect_is_not_a_case_status():
    assert "prospect" not in STATUSES
    with pytest.raises(FieldValidationError):
        parse_case_update({"status": "prospect"})


# ── Intake to closed ───────────────────────────────────────────────


def test_a_case_moves_from_intake_to_filed_and_closed():
    case = a_case()
    for payload in (
        {"status": "ready_to_file"},
        {**FILING, "judge": "Hon. Example Judge", "trustee": "Example Trustee"},
        {"meeting_341_at": "2026-10-05", "office_file_number": "F-1001"},
        {"status": "discharged"},
        {"status": "closed"},
    ):
        case = moved(case, payload)
    assert case.status == "closed"
    assert (case.case_number, case.judge, case.trustee, case.office_file_number) == (
        "8:26-bk-01234",
        "Hon. Example Judge",
        "Example Trustee",
        "F-1001",
    )
    assert (case.filed_at, case.meeting_341_at) == ("2026-09-01", "2026-10-05")


@pytest.mark.parametrize(
    ("start", "to"),
    [
        (start, to)
        for start in STATUSES
        for to in STATUSES
        if to != start and to not in TRANSITIONS[start]
    ],
)
def test_a_move_off_the_map_is_refused(start, to):
    case = a_case(status=start, filed_at="2026-09-01", case_number="8:26-bk-01234")
    with pytest.raises(ConflictError):
        moved(case, {"status": to})


def test_a_filed_case_never_returns_to_preparation():
    case = moved(a_case(), FILING)
    with pytest.raises(ConflictError, match="amendment"):
        moved(case, {"status": "ready_to_file"})


def test_a_closed_case_is_reopened_as_filed():
    case = a_case(status="closed", filed_at="2026-09-01", case_number="8:26-bk-1")
    assert moved(case, {"status": "filed"}).status == "filed"


@pytest.mark.parametrize(
    ("payload", "missing"),
    [
        ({"status": "filed"}, {"filed_at", "case_number"}),
        ({"status": "filed", "filed_at": "2026-09-01"}, {"case_number"}),
        ({"status": "filed", "case_number": "8:26-bk-1"}, {"filed_at"}),
    ],
)
def test_filing_needs_the_date_and_the_case_number(payload, missing):
    with pytest.raises(FieldValidationError) as caught:
        moved(a_case(status="ready_to_file"), payload)
    assert set(caught.value.fields) == missing


@pytest.mark.parametrize("cleared", ["filed_at", "case_number"])
def test_a_filed_case_keeps_its_anchors(cleared):
    case = moved(a_case(), FILING)
    with pytest.raises(FieldValidationError) as caught:
        moved(case, {cleared: None})
    assert set(caught.value.fields) == {cleared}


def test_a_case_filed_before_the_rule_still_takes_other_edits():
    legacy = a_case(status="filed")
    assert moved(legacy, {"meeting_341_at": "2026-10-05"}).meeting_341_at


@pytest.mark.parametrize(
    ("payload", "field"),
    [
        ({"case_number": "x" * 41}, "case_number"),
        ({"judge": 7}, "judge"),
        ({"trustee": "two\nlines"}, "trustee"),
    ],
)
def test_the_docket_facts_are_single_lines_of_bounded_text(payload, field):
    with pytest.raises(FieldValidationError) as caught:
        parse_case_update(payload)
    assert set(caught.value.fields) == {field}


def test_a_blank_docket_fact_clears_it():
    case = moved(a_case(), {"judge": "Hon. Example"})
    assert moved(case, {"judge": ""}).judge is None


@pytest.mark.parametrize("status", sorted(STATUSES))
def test_filed_means_on_the_docket_whatever_happened_since(status):
    assert is_filed(status) == (
        status in {"filed", "discharged", "dismissed", "closed"}
    )


# ── The history ────────────────────────────────────────────────────


def test_a_move_writes_a_history_row_and_an_edit_does_not():
    before = a_case()
    ready = moved(before, {"status": "ready_to_file"})
    change = status_change(before, ready, changed_by=ALICE)
    assert change is not None
    assert (change.from_status, change.to_status) == ("intake", "ready_to_file")
    assert change.changed_at == ready.updated_at
    assert status_change(ready, moved(ready, {"judge": "X"}), changed_by=ALICE) is None


def test_the_history_row_round_trips():
    before = a_case()
    change = status_change(
        before, moved(before, {"status": "ready_to_file"}), changed_by=ALICE
    )
    assert change is not None
    assert status_change_from_item(status_change_item(change)) == change
    with pytest.raises(ValidationError):
        status_change_from_item({"caseId": "x"})


# ── The stored shape ───────────────────────────────────────────────


def test_the_lifecycle_round_trips_the_item_and_reaches_the_wire():
    case = set_archived(
        moved(
            a_case(),
            {**FILING, "judge": "J", "trustee": "T", "office_file_number": "F"},
        ),
        archived=True,
        by=ALICE,
    )
    assert case_from_item(case_item(case)) == case
    body = case_json(case)
    assert body["caseNumber"] == "8:26-bk-01234"
    assert body["archivedBy"] == ALICE
    assert "archivedAt" in body


def test_a_row_written_before_the_lifecycle_reads_with_none_of_it():
    item = case_item(a_case())
    assert "caseNumber" not in item
    assert "archivedAt" not in item
    assert case_from_item(item).case_number is None


def test_the_deletion_stamp_never_reaches_the_wire():
    body = case_json(mark_deleted(a_case(), by=ALICE))
    assert "deletedAt" not in body
    assert "deletedBy" not in body


# ── Archive and delete ─────────────────────────────────────────────


def test_archiving_twice_keeps_the_first_stamp_and_restoring_clears_it():
    first = set_archived(a_case(), archived=True, by=ALICE)
    assert set_archived(first, archived=True, by="someone-else") == first
    restored = set_archived(first, archived=False, by=ALICE)
    assert (restored.archived_at, restored.archived_by) == (None, None)


@pytest.mark.parametrize("status", sorted(STATUSES))
def test_only_a_case_that_never_reached_the_court_may_be_deleted(status):
    refused = deletion_refusal(a_case(status=status)) is not None
    assert refused == is_filed(status)


def test_a_deleted_case_is_invisible_to_everyone():
    case = mark_deleted(a_case(), by=ALICE)
    assert may_see_case(admin(), case, assigned=True) is False
    assert public_status_from_case_item(case_item(case), firm_id=FIRM) is None


def test_the_working_list_and_the_archive_are_two_views_a_deleted_case_neither():
    store = MemoryCaseStore()
    cases = []
    for _ in range(5):
        case, assignment = create_case(
            parse_case_creation(OPENING), firm_id=FIRM, created_by=ALICE
        )
        store.create(case, assignment)
        cases.append(case)
    store.update(set_archived(cases[0], archived=True, by=ALICE))
    store.update(set_archived(cases[1], archived=True, by=ALICE))
    store.update(mark_deleted(cases[2], by=ALICE))

    working = store.list_for_accessor(admin(), limit=10, cursor=None)
    archive = store.list_for_accessor(admin(), limit=10, cursor=None, archived=True)
    assert {c.id for c in working.cases} == {cases[3].id, cases[4].id}
    assert {c.id for c in archive.cases} == {cases[0].id, cases[1].id}
    assert store.get(cases[2].id, accessor=admin()) is None


def test_a_cursor_from_the_archive_is_refused_by_the_working_list():
    store = MemoryCaseStore()
    for _ in range(3):
        case, assignment = create_case(
            parse_case_creation(OPENING), firm_id=FIRM, created_by=ALICE
        )
        store.create(case, assignment)
        store.update(set_archived(case, archived=True, by=ALICE))
    page = store.list_for_accessor(admin(), limit=1, cursor=None, archived=True)
    assert page.next_cursor is not None
    with pytest.raises(ValidationError):
        store.list_for_accessor(admin(), limit=1, cursor=page.next_cursor)


def test_a_write_read_before_a_status_move_is_refused():
    store = MemoryCaseStore()
    case, assignment = create_case(
        parse_case_creation(OPENING), firm_id=FIRM, created_by=ALICE
    )
    store.create(case, assignment)
    store.update(moved(case, {"status": "ready_to_file"}), expected_status="intake")
    with pytest.raises(ConflictError):
        store.update(moved(case, {"judge": "J"}), expected_status="intake")


def test_the_memory_store_keeps_the_history_with_the_write():
    store = MemoryCaseStore()
    case, assignment = create_case(
        parse_case_creation(OPENING), firm_id=FIRM, created_by=ALICE
    )
    store.create(case, assignment)
    after = moved(case, {"status": "ready_to_file"})
    store.update(
        after,
        expected_status="intake",
        status_change=status_change(case, after, changed_by=ALICE),
    )
    assert [c.to_status for c in store.status_history(case.id)] == ["ready_to_file"]


# ── Copy case ──────────────────────────────────────────────────────


def test_a_copy_is_a_new_retained_case_and_every_value_names_its_source():
    source = moved(a_case(exemption_set="federal"), {**FILING, "judge": "J"})
    staff = ProvenanceEntry(source="staff_typed")
    confirmed = ProvenanceEntry(
        source="ai_extracted",
        confirmed_by=ALICE,
        confirmed_at="2026-09-01T00:00:00.000000Z",
        document_id="doc-1",
    )
    debtor = Debtor(
        id="d1",
        case_id=source.id,
        filing_role="debtor_1",
        created_at="2026-08-01T00:00:00.000000Z",
        updated_at="2026-08-01T00:00:00.000000Z",
        email="a@example.test",
        provenance={"email": staff},
        client_id=CLIENT_A,
        case_created_at=source.created_at,
    )
    creditor = CaseEntity(
        kind=CREDITOR,
        id="c1",
        case_id=source.id,
        created_at="2026-08-02T00:00:00.000000Z",
        updated_at="2026-08-02T00:00:00.000000Z",
        body=CreditorBody(name="Example Bank"),
        provenance={"name": confirmed},
        amended=True,
    )

    copy = copy_case(source, created_by=ALICE, debtors=[debtor], entities=[creditor])

    assert copy.case.id != source.id
    assert (copy.case.status, copy.case.chapter, copy.case.court) == (
        "intake",
        source.chapter,
        source.court,
    )
    assert copy.case.exemption_set == "federal"
    assert (copy.case.filed_at, copy.case.case_number, copy.case.judge) == (None,) * 3
    assert copy.assignment.subject == ALICE
    (copied_debtor,) = copy.debtors
    assert copied_debtor.id == "d1"
    assert copied_debtor.case_id == copy.case.id
    assert copied_debtor.client_id == CLIENT_A
    assert copied_debtor.case_created_at == copy.case.created_at
    assert copied_debtor.provenance["email"] == replace(
        staff, copied_from_case_id=source.id
    )
    (copied,) = copy.entities
    assert (copied.id, copied.case_id, copied.amended) == ("c1", copy.case.id, False)
    assert copied.created_at == creditor.created_at
    # The original origin survives — confirmed by a person, from a document.
    assert copied.provenance["name"] == replace(
        confirmed, copied_from_case_id=source.id
    )


def test_the_copy_marker_round_trips_the_provenance_wire():
    entry = ProvenanceEntry(source="staff_typed", copied_from_case_id="case-1")
    assert parse_provenance(provenance_json({"name": entry})) == {"name": entry}
    with pytest.raises(FieldValidationError):
        parse_provenance({"name": {"source": "staff_typed", "copied_from_case_id": 7}})


# ── DynamoDbCaseStore, over a recording transport ───────────────────


class FakeDynamoDb:
    """Records calls and replays canned responses in order; conditions are
    not simulated — the integration tier runs the real table."""

    def __init__(self) -> None:
        self.calls: list[tuple[str, dict[str, Any]]] = []
        self.responses: dict[str, list[Any]] = {}

    def _record(self, name: str, kwargs: dict[str, Any]) -> Any:
        self.calls.append((name, kwargs))
        queue = self.responses.get(name) or [{}]
        response = queue.pop(0) if len(queue) > 1 else queue[0]
        if isinstance(response, Exception):
            raise response
        return response

    def __getattr__(self, name: str) -> Any:
        return lambda **kwargs: self._record(name, kwargs)


def dynamo_store(monkeypatch, fake: FakeDynamoDb) -> DynamoDbCaseStore:
    monkeypatch.setattr(aws_case_store.boto3, "client", lambda _service: fake)
    return DynamoDbCaseStore("insolvia-test-cases")


def test_the_dynamo_listing_reads_on_until_the_page_is_full(monkeypatch):
    fake = FakeDynamoDb()
    store = dynamo_store(monkeypatch, fake)
    archived = set_archived(a_case(), archived=True, by=ALICE)
    live_1, live_2 = a_case(), a_case()
    fake.responses["query"] = [
        {
            "Items": [
                to_attributes(case_item(archived)),
                to_attributes(case_item(live_1)),
            ],
            "LastEvaluatedKey": {"PK": {"S": "k1"}},
        },
        {
            "Items": [to_attributes(case_item(live_2))],
            "LastEvaluatedKey": {"PK": {"S": "k2"}},
        },
    ]

    page = store.list_for_accessor(admin(), limit=2, cursor=None)

    assert [case.id for case in page.cases] == [live_1.id, live_2.id]
    limits = [kwargs["Limit"] for name, kwargs in fake.calls if name == "query"]
    assert limits == [2, 1]
    # The next page starts where the last read stopped, tagged with its view.
    with pytest.raises(ValidationError):
        store.list_for_accessor(
            admin(), limit=2, cursor=page.next_cursor, archived=True
        )


def test_a_dynamo_status_move_writes_the_record_and_its_history_together(
    monkeypatch,
):
    fake = FakeDynamoDb()
    store = dynamo_store(monkeypatch, fake)
    before = a_case()
    after = moved(before, {"status": "ready_to_file"})

    store.update(
        after,
        expected_status="intake",
        status_change=status_change(before, after, changed_by=ALICE),
    )

    [(name, kwargs)] = fake.calls
    assert name == "transact_write_items"
    case_put, history_put = (item["Put"] for item in kwargs["TransactItems"])
    assert "#status = :expected" in case_put["ConditionExpression"]
    assert case_put["ExpressionAttributeValues"][":expected"] == {"S": "intake"}
    assert history_put["Item"]["SK"] == {"S": f"STATUS#{after.updated_at}"}


def test_a_dynamo_write_that_lost_to_a_status_move_is_a_conflict(monkeypatch):
    fake = FakeDynamoDb()
    store = dynamo_store(monkeypatch, fake)
    case = a_case()
    fake.responses["put_item"] = [
        ClientError({"Error": {"Code": "ConditionalCheckFailedException"}}, "PutItem")
    ]
    fake.responses["get_item"] = [
        {"Item": to_attributes(case_item(replace(case, status="ready_to_file")))}
    ]

    with pytest.raises(ConflictError):
        store.update(moved(case, {"judge": "J"}), expected_status="intake")
