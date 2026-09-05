"""The in-memory JobStore — the same contract the DynamoDB adapter holds,
exercised at the port. The AWS adapter is not tested against a fake DynamoDB
by decision (no moto); its conditions mirror the ones asserted here and the
key construction is shared through job_item."""

from __future__ import annotations

from dataclasses import replace

import pytest
from insolvia_api.adapters.memory.job_store import MemoryJobStore
from insolvia_api.core.jobs import Job, new_job, start_attempt

CASE = "case-0001"
OTHER_CASE = "case-0002"
ALICE = "00000000-0000-4000-8000-00000000a11c"


def job(case_id: str = CASE) -> Job:
    return new_job("echo", case_id=case_id, created_by=ALICE)


@pytest.fixture
def store() -> MemoryJobStore:
    return MemoryJobStore()


def test_created_jobs_are_read_back(store) -> None:
    created = job()
    store.create(created)
    assert store.get(CASE, created.id) == created


def test_creating_the_same_id_twice_raises(store) -> None:
    created = job()
    store.create(created)
    with pytest.raises(RuntimeError):
        store.create(created)


def test_get_is_case_scoped(store) -> None:
    # A leaked job id is useless without its case.
    created = job()
    store.create(created)
    assert store.get(OTHER_CASE, created.id) is None


def test_update_is_conditional_on_the_observed_status(store) -> None:
    created = job()
    store.create(created)
    started = start_attempt(created)
    assert store.update(started, expected_status="queued") == started
    # A second delivery that also observed "queued" must lose.
    assert store.update(start_attempt(created), expected_status="queued") is None


def test_update_refuses_a_row_that_is_not_there(store) -> None:
    assert store.update(job(), expected_status="queued") is None


def test_listing_is_scoped_and_in_creation_order(store) -> None:
    first = job()
    second = job()
    foreign = job(case_id=OTHER_CASE)
    # Inserted out of order to prove the store sorts rather than echoes.
    store.create(second)
    store.create(first)
    store.create(foreign)
    listed = store.list_for_case(CASE)
    expected = sorted([first, second], key=lambda j: (j.created_at, j.id))
    assert [j.id for j in listed] == [j.id for j in expected]
    assert all(j.case_id == CASE for j in listed)


def test_listing_an_empty_case_is_empty(store) -> None:
    assert store.list_for_case(CASE) == ()


def test_update_replaces_the_whole_record(store) -> None:
    created = job()
    store.create(created)
    renamed = replace(start_attempt(created), attempts=3)
    assert store.update(renamed, expected_status="queued") == renamed
    assert store.get(CASE, created.id) == renamed
