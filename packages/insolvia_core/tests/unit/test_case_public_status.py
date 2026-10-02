"""The client's read of a case: chapter and stage, through the binding
(ADR 0023 decision 4).

What is pinned here is the SHAPE of the read path, because the rule it
enforces is structural: a portal route never holds a `Case`. The store method
takes the `ClientAccessor` (no case id to pass wrongly), the DynamoDB adapter
asks the table for exactly the public attributes, and the projection answers
nothing for a row of another firm.

Every identifier below is obviously fake. This repo is public.
"""

from __future__ import annotations

from dataclasses import fields
from typing import Any

import pytest
from insolvia_core.access import ClientAccessor
from insolvia_core.adapters.aws import case_store as aws_case_store
from insolvia_core.adapters.aws.case_store import DynamoDbCaseStore
from insolvia_core.adapters.aws.dynamo import to_attributes
from insolvia_core.adapters.memory.case_store import MemoryCaseStore
from insolvia_core.cases import Case, CaseAssignment, case_item
from insolvia_core.clients import (
    ACTIVE,
    PUBLIC_STATUS_ATTRIBUTES,
    CasePublicStatus,
    ClientBinding,
    public_status_from_case_item,
    public_status_json,
)
from insolvia_core.errors import ValidationError
from insolvia_core.firms import Firm

FIRM = "00000000-0000-4000-8000-00000000f18a"
OTHER_FIRM = "00000000-0000-4000-8000-00000000f18b"
CASE = "00000000-0000-4000-8000-0000000ca5e1"
STAFF = "00000000-0000-4000-8000-00000000a11c"
PAT = "00000000-0000-4000-8000-0000000000a1"
NOW = "2026-09-28T12:00:00Z"


def a_case(*, firm_id: str = FIRM, chapter: int = 13, status: str = "intake") -> Case:
    return Case(
        id=CASE,
        firm_id=firm_id,
        created_by=STAFF,
        chapter=chapter,
        district="District of Example",
        status=status,
        created_at=NOW,
        updated_at=NOW,
        filed_at="2026-09-01",
    )


def a_client(*, firm_id: str = FIRM) -> ClientAccessor:
    return ClientAccessor(
        firm=Firm(
            id=firm_id,
            name="Example & Partners",
            status="active",
            created_at=NOW,
            updated_at=NOW,
        ),
        binding=ClientBinding(
            firm_id=firm_id,
            case_id=CASE,
            subject=PAT,
            email="pat@example.test",
            display_name="Pat Example",
            roles=("debtor_1",),
            status=ACTIVE,
            invited_by=STAFF,
            created_at=NOW,
            updated_at=NOW,
        ),
    )


# ── The projection ──────────────────────────────────────────────────


def test_the_projection_is_chapter_and_stage_and_nothing_else():
    assert [f.name for f in fields(CasePublicStatus)] == ["chapter", "stage"]


def test_a_case_item_projects_to_its_chapter_and_lifecycle_status():
    status = public_status_from_case_item(
        case_item(a_case(chapter=7, status="ready_to_file")), firm_id=FIRM
    )

    assert status == CasePublicStatus(chapter=7, stage="ready_to_file")
    assert public_status_json(status) == {"chapter": 7, "stage": "ready_to_file"}


def test_another_firms_row_projects_to_nothing():
    assert public_status_from_case_item(case_item(a_case()), firm_id=OTHER_FIRM) is None


@pytest.mark.parametrize(
    "item",
    [
        {"firmId": FIRM, "status": "intake"},
        {"firmId": FIRM, "chapter": True, "status": "intake"},
        {"firmId": FIRM, "chapter": "seven", "status": "intake"},
        {"firmId": FIRM, "chapter": 7, "status": "shredded"},
        {"firmId": FIRM, "chapter": 7},
    ],
)
def test_a_row_this_domain_did_not_write_raises(item):
    with pytest.raises(ValidationError):
        public_status_from_case_item(item, firm_id=FIRM)


# ── Memory store ────────────────────────────────────────────────────


def seeded_memory(case: Case) -> MemoryCaseStore:
    store = MemoryCaseStore()
    store.create(
        case,
        CaseAssignment(
            case_id=case.id,
            subject=STAFF,
            case_created_at=NOW,
            assigned_at=NOW,
            assigned_by=STAFF,
        ),
    )
    return store


def test_the_memory_store_reads_the_bound_case_without_an_assignment():
    # The client is not an assignee and holds no Accessor: the binding is
    # the whole authority, and the read needs nothing else.
    store = seeded_memory(a_case(status="filed"))

    assert store.public_status(a_client()) == CasePublicStatus(
        chapter=13, stage="filed"
    )


def test_the_memory_store_refuses_a_binding_whose_firm_does_not_own_the_case():
    store = seeded_memory(a_case())

    assert store.public_status(a_client(firm_id=OTHER_FIRM)) is None


def test_the_memory_store_answers_nothing_for_a_missing_case():
    assert MemoryCaseStore().public_status(a_client()) is None


# ── DynamoDB ────────────────────────────────────────────────────────


class FakeDynamoDb:
    def __init__(self, item: dict[str, Any] | None) -> None:
        self.item = item
        self.calls: list[dict[str, Any]] = []

    def get_item(self, **kwargs: Any) -> Any:
        self.calls.append(kwargs)
        return {} if self.item is None else {"Item": self.item}


def dynamo(monkeypatch, fake: FakeDynamoDb) -> DynamoDbCaseStore:
    monkeypatch.setattr(aws_case_store.boto3, "client", lambda _service: fake)
    return DynamoDbCaseStore("insolvia-test-cases")


def test_dynamo_asks_the_table_for_the_public_attributes_only(monkeypatch):
    public = {
        k: v for k, v in case_item(a_case()).items() if k in PUBLIC_STATUS_ATTRIBUTES
    }
    fake = FakeDynamoDb(to_attributes(public))

    status = dynamo(monkeypatch, fake).public_status(a_client())

    assert status == CasePublicStatus(chapter=13, stage="intake")
    (call,) = fake.calls
    assert call["Key"] == {"PK": {"S": f"CASE#{CASE}"}, "SK": {"S": "META"}}
    names = call["ExpressionAttributeNames"]
    assert sorted(names.values()) == sorted(PUBLIC_STATUS_ATTRIBUTES)
    assert call["ProjectionExpression"] == ", ".join(names)


def test_dynamo_refuses_a_row_of_another_firm(monkeypatch):
    fake = FakeDynamoDb(
        to_attributes({"firmId": OTHER_FIRM, "chapter": 7, "status": "intake"})
    )

    assert dynamo(monkeypatch, fake).public_status(a_client()) is None


def test_dynamo_answers_nothing_for_a_missing_case(monkeypatch):
    assert dynamo(monkeypatch, FakeDynamoDb(None)).public_status(a_client()) is None
