"""Document request checklists (ADR 0023 PR 5 / #364): the firm's checklist,
a case's requests and their status, and both stores' item shapes.

Every identifier below is obviously fake. This repo is public.
"""

from __future__ import annotations

from dataclasses import replace
from typing import Any

import pytest
from botocore.exceptions import ClientError
from insolvia_core.adapters.aws import document_request_store as aws_requests
from insolvia_core.adapters.aws import firm_store as aws_firm_store
from insolvia_core.adapters.aws.document_request_store import (
    DynamoDbDocumentRequestStore,
)
from insolvia_core.adapters.aws.dynamo import to_attributes
from insolvia_core.adapters.aws.firm_store import DynamoDbFirmStore
from insolvia_core.adapters.memory.document_request_store import (
    MemoryDocumentRequestStore,
)
from insolvia_core.adapters.memory.firm_store import MemoryFirmStore
from insolvia_core.document_requests import (
    DEFAULT_CHECKLIST,
    MAX_CHECKLIST_ITEMS,
    STATUS_RECEIVED,
    STATUS_REQUESTED,
    STATUS_WAIVED,
    ChecklistItem,
    DocumentRequest,
    accepts_uploads,
    build_checklist,
    checklist_from_item,
    checklist_item,
    checklist_json,
    create_request,
    parse_checklist_update,
    parse_request_creation,
    parse_request_update,
    portal_request_json,
    progress,
    request_from_item,
    request_item,
    requests_from_checklist,
    requests_json,
    resolve_checklist,
    satisfy,
    set_status,
)
from insolvia_core.documents import KINDS
from insolvia_core.errors import FieldValidationError, ValidationError

FIRM_ID = "00000000-0000-4000-8000-00000000f18a"
OTHER_FIRM_ID = "00000000-0000-4000-8000-00000000f18b"
CASE_ID = "00000000-0000-4000-8000-0000000000ca"
OTHER_CASE_ID = "00000000-0000-4000-8000-0000000000cb"
ALICE = "00000000-0000-4000-8000-00000000a11c"
DOC_1 = "00000000-0000-4000-8000-00000000d0c1"
DOC_2 = "00000000-0000-4000-8000-00000000d0c2"


def request(
    title: str = "Bank statements", status: str = STATUS_REQUESTED, **kw: Any
) -> DocumentRequest:
    made = create_request(
        ChecklistItem(title=title, kind="bank_statement"),
        case_id=kw.pop("case_id", CASE_ID),
        created_by=ALICE,
    )
    return replace(made, status=status, **kw)


# ── The checklist ───────────────────────────────────────────────────


def test_the_default_checklist_covers_the_issues_documents_with_known_kinds():
    titles = [item.title for item in DEFAULT_CHECKLIST]
    assert titles == [
        "Pay stubs for the last six months",
        "Tax returns for the last two years",
        "Bank statements",
        "Vehicle titles",
        "Mortgage statements",
        "Identification",
    ]
    assert all(item.kind in KINDS for item in DEFAULT_CHECKLIST)
    assert all(item.description for item in DEFAULT_CHECKLIST)


def test_no_stored_checklist_means_the_default():
    assert resolve_checklist(None) == DEFAULT_CHECKLIST
    assert checklist_json(None)["isDefault"] is True
    assert checklist_json(None)["updatedBy"] is None


def test_an_empty_stored_checklist_is_a_choice_not_the_default():
    stored = build_checklist((), firm_id=FIRM_ID, updated_by=ALICE)
    assert resolve_checklist(stored) == ()
    body = checklist_json(stored)
    assert body["isDefault"] is False
    assert body["items"] == []
    assert len(body["defaultItems"]) == len(DEFAULT_CHECKLIST)  # type: ignore[arg-type]


def test_a_checklist_save_is_parsed_whole_and_trimmed():
    items = parse_checklist_update(
        {
            "items": [
                {"title": "  Lease  ", "kind": "other", "description": " "},
                {"title": "Pay stubs", "kind": "pay_stub", "description": "Six months"},
            ]
        }
    )
    assert items == (
        ChecklistItem(title="Lease", kind="other", description=None),
        ChecklistItem(title="Pay stubs", kind="pay_stub", description="Six months"),
    )


def test_every_bad_checklist_entry_is_reported_by_index():
    with pytest.raises(FieldValidationError) as raised:
        parse_checklist_update(
            {
                "items": [
                    {"title": "", "kind": "pay_stub"},
                    {"title": "Car", "kind": "vehicle"},
                    "not an object",
                ]
            }
        )
    assert set(raised.value.fields) == {
        "items.0.title",
        "items.1.kind",
        "items.2",
    }


def test_two_entries_with_the_same_title_are_refused():
    with pytest.raises(FieldValidationError) as raised:
        parse_checklist_update(
            {
                "items": [
                    {"title": "Bank statements", "kind": "bank_statement"},
                    {"title": "bank  STATEMENTS", "kind": "other"},
                ]
            }
        )
    assert set(raised.value.fields) == {"items.1.title"}


def test_a_checklist_has_a_ceiling():
    entries = [{"title": f"Doc {n}", "kind": "other"} for n in range(41)]
    assert len(entries) > MAX_CHECKLIST_ITEMS
    with pytest.raises(FieldValidationError):
        parse_checklist_update({"items": entries})


def test_the_checklist_item_round_trips():
    stored = build_checklist(DEFAULT_CHECKLIST[:2], firm_id=FIRM_ID, updated_by=ALICE)
    item = checklist_item(stored)
    assert item["PK"] == f"FIRM#{FIRM_ID}"
    assert item["SK"] == "DOCUMENT_CHECKLIST"
    assert checklist_from_item(item) == stored


def test_a_malformed_checklist_row_fails_loudly():
    with pytest.raises(ValidationError):
        checklist_from_item({"firmId": FIRM_ID, "items": "nope"})


# ── Requests ────────────────────────────────────────────────────────


def test_applying_the_checklist_requests_each_entry_once():
    first = requests_from_checklist(
        DEFAULT_CHECKLIST, (), case_id=CASE_ID, created_by=ALICE
    )
    assert [r.title for r in first] == [i.title for i in DEFAULT_CHECKLIST]
    assert {r.status for r in first} == {STATUS_REQUESTED}

    # Applying again — after the firm added one — adds only the new entry,
    # and a waived request still counts as asked.
    waived = replace(first[0], status=STATUS_WAIVED)
    again = requests_from_checklist(
        (*DEFAULT_CHECKLIST, ChecklistItem(title="Lease", kind="other")),
        (waived, *first[1:]),
        case_id=CASE_ID,
        created_by=ALICE,
    )
    assert [r.title for r in again] == ["Lease"]


def test_a_request_is_parsed_like_a_checklist_entry():
    item = parse_request_creation({"title": "Lease", "kind": "other"})
    assert item == ChecklistItem(title="Lease", kind="other")
    with pytest.raises(FieldValidationError) as raised:
        parse_request_creation({"kind": "nope"})
    assert set(raised.value.fields) == {"title", "kind"}


def test_received_cannot_be_set_by_hand():
    with pytest.raises(FieldValidationError):
        parse_request_update({"status": "received"})
    with pytest.raises(FieldValidationError):
        parse_request_update({"status": "done"})
    assert parse_request_update({"status": "waived"}) == STATUS_WAIVED
    with pytest.raises(ValueError, match="not settable"):
        set_status(request(), STATUS_RECEIVED)


def test_a_completed_upload_receives_the_request_once():
    received = satisfy(request(), DOC_1)
    assert received.status == STATUS_RECEIVED
    assert received.document_ids == (DOC_1,)
    assert received.received_at is not None

    assert satisfy(received, DOC_1) == received
    second = satisfy(received, DOC_2)
    assert second.document_ids == (DOC_1, DOC_2)
    assert second.received_at == received.received_at


def test_an_upload_racing_a_waiver_does_not_overturn_it():
    after = satisfy(request(status=STATUS_WAIVED), DOC_1)
    assert after.status == STATUS_WAIVED
    assert after.document_ids == (DOC_1,)


def test_reopening_a_received_request_keeps_its_documents():
    reopened = set_status(satisfy(request(), DOC_1), STATUS_REQUESTED)
    assert reopened.status == STATUS_REQUESTED
    assert reopened.document_ids == (DOC_1,)


def test_only_a_waived_request_refuses_uploads():
    assert accepts_uploads(request())
    assert accepts_uploads(request(status=STATUS_RECEIVED))
    assert not accepts_uploads(request(status=STATUS_WAIVED))


def test_progress_counts_arrived_against_outstanding_and_leaves_waived_out():
    counted = progress(
        [
            request("A", STATUS_RECEIVED),
            request("B"),
            request("C"),
            request("D", STATUS_WAIVED),
        ]
    )
    assert (counted.total, counted.received, counted.outstanding, counted.waived) == (
        3,
        1,
        2,
        1,
    )


def test_the_staff_listing_carries_progress_from_the_same_list():
    body = requests_json([request("A", STATUS_RECEIVED), request("B")])
    assert body["progress"] == {
        "total": 2,
        "received": 1,
        "outstanding": 1,
        "waived": 0,
    }
    first = body["requests"][0]  # type: ignore[index]
    assert "createdBy" not in first


def test_the_clients_view_names_no_case_and_no_staff_documents():
    stored = satisfy(request(), DOC_1)
    body = portal_request_json(stored, [])
    assert set(body) == {"id", "title", "kind", "description", "status", "uploads"}
    assert CASE_ID not in str(body)
    assert DOC_1 not in str(body)


def test_the_request_item_round_trips_with_its_documents():
    stored = satisfy(request(description="Six months"), DOC_1)
    item = request_item(stored)
    assert item["PK"] == f"CASE#{CASE_ID}"
    assert item["SK"] == f"DOCREQUEST#{stored.id}"
    assert request_from_item(item) == stored
    # Sparse: an unreceived request writes no receivedAt.
    assert "receivedAt" not in request_item(request())


def test_a_request_row_with_an_unknown_status_fails_loudly():
    item = request_item(request())
    item["status"] = "lost"
    with pytest.raises(ValidationError):
        request_from_item(item)


# ── The memory request store ───────────────────────────────────────


def test_the_memory_store_refuses_a_second_create_and_a_ghost_update():
    store = MemoryDocumentRequestStore()
    made = request()
    store.create(made)
    with pytest.raises(RuntimeError):
        store.create(made)
    assert store.update(request("Other")) is None


def test_the_memory_store_scopes_by_case_and_lists_in_creation_order():
    store = MemoryDocumentRequestStore()
    first, second = requests_from_checklist(
        DEFAULT_CHECKLIST[:2], (), case_id=CASE_ID, created_by=ALICE
    )
    store.create(second)
    store.create(first)
    store.create(request(case_id=OTHER_CASE_ID))

    assert [r.id for r in store.list_for_case(CASE_ID)] == [first.id, second.id]
    assert store.get(OTHER_CASE_ID, first.id) is None
    assert store.delete(CASE_ID, first.id) is True
    assert store.delete(CASE_ID, first.id) is False


# ── The DynamoDB stores ────────────────────────────────────────────


class FakeDynamoDb:
    """Records calls and replays canned responses; conditions are not
    simulated (test_firm_store.py's fake, trimmed to what these use)."""

    def __init__(self, responses: dict[str, Any] | None = None) -> None:
        self.calls: list[tuple[str, dict[str, Any]]] = []
        self.responses = responses or {}
        self.raises: Exception | None = None

    def _record(self, name: str, kwargs: dict[str, Any]) -> Any:
        self.calls.append((name, kwargs))
        if self.raises is not None:
            raise self.raises
        return self.responses.get(name, {})

    def put_item(self, **kwargs: Any) -> Any:
        return self._record("put_item", kwargs)

    def get_item(self, **kwargs: Any) -> Any:
        return self._record("get_item", kwargs)

    def query(self, **kwargs: Any) -> Any:
        return self._record("query", kwargs)

    def delete_item(self, **kwargs: Any) -> Any:
        return self._record("delete_item", kwargs)


def conditional_check_failed() -> ClientError:
    return ClientError(
        {"Error": {"Code": "ConditionalCheckFailedException", "Message": "no"}},
        "PutItem",
    )


def request_store(monkeypatch, fake: FakeDynamoDb) -> DynamoDbDocumentRequestStore:
    monkeypatch.setattr(aws_requests.boto3, "client", lambda _service: fake)
    return DynamoDbDocumentRequestStore("insolvia-test-cases")


def test_a_request_is_written_into_its_cases_partition(monkeypatch):
    fake = FakeDynamoDb()
    store = request_store(monkeypatch, fake)
    made = satisfy(request(), DOC_1)

    store.create(made)

    name, kwargs = fake.calls[0]
    assert name == "put_item"
    assert kwargs["Item"]["PK"] == {"S": f"CASE#{CASE_ID}"}
    assert kwargs["Item"]["documentIds"] == {"L": [{"S": DOC_1}]}
    assert kwargs["ConditionExpression"] == "attribute_not_exists(SK)"


def test_the_request_listing_reads_only_request_rows(monkeypatch):
    made = request()
    fake = FakeDynamoDb({"query": {"Items": [to_attributes(request_item(made))]}})
    store = request_store(monkeypatch, fake)

    assert store.list_for_case(CASE_ID) == (made,)
    assert fake.calls[0][1]["ExpressionAttributeValues"][":prefix"] == {
        "S": "DOCREQUEST#"
    }


def test_a_request_update_over_a_deleted_row_is_none(monkeypatch):
    fake = FakeDynamoDb()
    fake.raises = conditional_check_failed()
    store = request_store(monkeypatch, fake)

    assert store.update(request()) is None
    assert fake.calls[0][1]["ConditionExpression"] == "attribute_exists(SK)"


# ── The firm store's checklist ─────────────────────────────────────


def test_the_memory_firm_store_holds_one_checklist_per_firm():
    store = MemoryFirmStore()
    saved = build_checklist(DEFAULT_CHECKLIST[:1], firm_id=FIRM_ID, updated_by=ALICE)

    assert store.get_document_checklist(FIRM_ID) is None
    store.put_document_checklist(saved)
    assert store.get_document_checklist(FIRM_ID) == saved
    assert store.get_document_checklist(OTHER_FIRM_ID) is None
    assert store.delete_document_checklist(FIRM_ID) is True
    assert store.delete_document_checklist(FIRM_ID) is False


def test_the_checklist_round_trips_through_the_firm_table(monkeypatch):
    saved = build_checklist(DEFAULT_CHECKLIST, firm_id=FIRM_ID, updated_by=ALICE)
    fake = FakeDynamoDb({"get_item": {"Item": to_attributes(checklist_item(saved))}})
    monkeypatch.setattr(aws_firm_store.boto3, "client", lambda _service: fake)
    store = DynamoDbFirmStore("insolvia-test-firms")

    assert store.get_document_checklist(FIRM_ID) == saved
    assert fake.calls[0][1]["Key"]["SK"] == {"S": "DOCUMENT_CHECKLIST"}
    assert fake.calls[0][1]["ConsistentRead"] is True

    fake.raises = conditional_check_failed()
    assert store.delete_document_checklist(FIRM_ID) is False
