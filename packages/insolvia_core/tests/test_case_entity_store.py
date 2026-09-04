"""The in-memory CaseEntityStore — the same contract the DynamoDB adapter
holds, exercised at the port. The AWS adapter is not tested against a fake
DynamoDB by decision (no moto); its conditions mirror the ones asserted here
and the key construction is shared with entity_item."""

from __future__ import annotations

import pytest
from insolvia_core.adapters.memory.case_entity_store import MemoryCaseEntityStore
from insolvia_core.case_entities import (
    CaseEntity,
    create_entity,
    parse_entity,
    replace_entity,
)
from insolvia_core.creditors import CREDITOR
from insolvia_core.expenses import EXPENSE

CASE = "case-0001"
OTHER_CASE = "case-0002"


def creditor(case_id: str = CASE, name: str = "Example Bank") -> CaseEntity:
    provenance = {"name": {"source": "staff_typed"}}
    draft = parse_entity(CREDITOR, {"name": name, "provenance": provenance})
    return create_entity(CREDITOR, draft, case_id=case_id)


@pytest.fixture
def store() -> MemoryCaseEntityStore:
    return MemoryCaseEntityStore()


def test_created_entities_are_read_back(store) -> None:
    entity = creditor()
    store.create(entity)
    assert store.get(CASE, CREDITOR, entity.id) == entity


def test_creating_the_same_id_twice_raises(store) -> None:
    # Ids are server-minted uuid4s; a collision means the minting is broken,
    # and replacing would erase a record to hide it.
    entity = creditor()
    store.create(entity)
    with pytest.raises(RuntimeError):
        store.create(entity)


def test_get_is_case_scoped(store) -> None:
    # A leaked entity id is useless without its case.
    entity = creditor()
    store.create(entity)
    assert store.get(OTHER_CASE, CREDITOR, entity.id) is None


def test_get_is_kind_scoped(store) -> None:
    # An id resolved through the wrong collection does not resolve at all.
    entity = creditor()
    store.create(entity)
    assert store.get(CASE, EXPENSE, entity.id) is None


def test_put_replaces_an_existing_record(store) -> None:
    entity = creditor()
    store.create(entity)
    renamed = replace_entity(
        entity,
        parse_entity(
            CREDITOR,
            {"name": "Renamed", "provenance": {"name": {"source": "staff_typed"}}},
        ),
    )
    assert store.put(renamed) is True
    stored = store.get(CASE, CREDITOR, entity.id)
    assert stored is not None
    assert stored.body.name == "Renamed"


def test_put_refuses_to_resurrect_a_deleted_record(store) -> None:
    entity = creditor()
    store.create(entity)
    assert store.delete(CASE, CREDITOR, entity.id) is True
    assert store.put(entity) is False
    assert store.get(CASE, CREDITOR, entity.id) is None


def test_delete_reports_whether_this_call_removed_it(store) -> None:
    entity = creditor()
    store.create(entity)
    assert store.delete(CASE, CREDITOR, entity.id) is True
    assert store.delete(CASE, CREDITOR, entity.id) is False


def test_listing_is_scoped_and_in_creation_order(store) -> None:
    first = creditor(name="Alpha")
    second = creditor(name="Beta")
    foreign = creditor(case_id=OTHER_CASE, name="Gamma")
    # Inserted out of order to prove the store sorts rather than echoes.
    store.create(second)
    store.create(first)
    store.create(foreign)
    listed = store.list_for_case(CASE, CREDITOR)
    expected = sorted([first, second], key=lambda e: (e.created_at, e.id))
    assert [entity.id for entity in listed] == [entity.id for entity in expected]
    assert all(entity.case_id == CASE for entity in listed)


def test_listing_an_empty_collection_is_empty(store) -> None:
    assert store.list_for_case(CASE, CREDITOR) == ()
