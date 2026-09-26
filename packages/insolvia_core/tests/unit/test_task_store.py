"""The in-memory TaskStore, tested directly (issue #356 / 14.4).

Almost everything about this adapter is covered through the routes that use
it, and that is where it belongs. What CANNOT be observed from a route is the
one property this file exists for: the memory store must refuse exactly the
writes DynamoDB's `attribute_not_exists(SK)`/`attribute_exists(SK)` refuse —
see test_document_store.py, which this mirrors.

Every identifier below is obviously fake. This repo is public.
"""

from __future__ import annotations

from dataclasses import replace

import pytest
from insolvia_core.adapters.memory.task_store import MemoryTaskStore
from insolvia_core.tasks import Task

CASE_ID = "00000000-0000-4000-8000-0000000000ca"
OTHER_CASE_ID = "00000000-0000-4000-8000-0000000000cb"
TASK_ID = "00000000-0000-4000-8000-0000000000ta"
ALICE = "00000000-0000-4000-8000-00000000a11c"


def task(
    task_id: str = TASK_ID, case_id: str = CASE_ID, subject: str = "A task"
) -> Task:
    return Task(
        id=task_id,
        case_id=case_id,
        subject=subject,
        description=None,
        due_date=None,
        assignee_subject=None,
        form_series=None,
        done=False,
        completed_at=None,
        created_at="2026-01-01T00:00:00.000000Z",
        updated_at="2026-01-01T00:00:00.000000Z",
        created_by=ALICE,
    )


def test_creating_the_same_task_twice_is_refused():
    store = MemoryTaskStore()
    store.create(task())
    with pytest.raises(RuntimeError):
        store.create(task(subject="A different task"))


def test_creating_the_same_instance_twice_is_refused():
    """THE FAILURE test_document_store.py's own equivalent exists for: a
    `setdefault`-shaped check returns the same object for the same instance
    twice and would let this pass silently, where DynamoDB's
    `attribute_not_exists(SK)` — knowing nothing about Python identity — would
    raise."""
    store = MemoryTaskStore()
    only = task()
    store.create(only)
    with pytest.raises(RuntimeError):
        store.create(only)
    assert store.get(CASE_ID, TASK_ID) is only


def test_update_refuses_a_row_that_is_not_there():
    store = MemoryTaskStore()
    assert store.update(task()) is None
    assert store.tasks == {}


def test_update_replaces_an_existing_row():
    store = MemoryTaskStore()
    store.create(task())
    completed = replace(task(), done=True, completed_at="2026-01-02T00:00:00.000000Z")

    assert store.update(completed) == completed
    assert store.get(CASE_ID, TASK_ID) == completed


def test_delete_reports_whether_it_removed_anything():
    store = MemoryTaskStore()
    store.create(task())
    assert store.delete(CASE_ID, TASK_ID) is True
    assert store.delete(CASE_ID, TASK_ID) is False


def test_the_same_task_id_in_another_case_is_a_different_row():
    """The key is (case, task), as it is in the table. A task id leaked from
    one firm's case must not resolve inside another's."""
    store = MemoryTaskStore()
    store.create(task())
    store.create(task(case_id=OTHER_CASE_ID))
    assert store.get(CASE_ID, TASK_ID) is not None
    assert store.get(OTHER_CASE_ID, TASK_ID) is not None
    assert len(store.list_for_case(CASE_ID)) == 1


def test_list_for_case_is_creation_order():
    store = MemoryTaskStore()
    first = replace(
        task(task_id=f"{TASK_ID[:-1]}1"), created_at="2026-01-01T00:00:00.000000Z"
    )
    second = replace(
        task(task_id=f"{TASK_ID[:-1]}2"), created_at="2026-01-02T00:00:00.000000Z"
    )
    store.create(second)
    store.create(first)
    assert [t.id for t in store.list_for_case(CASE_ID)] == [first.id, second.id]
