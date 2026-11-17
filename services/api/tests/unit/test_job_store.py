"""The JobStore contract, held by the memory twin and the Postgres adapter
alike (ADR 0030)."""

from __future__ import annotations

from dataclasses import replace

import pytest
from insolvia_api.adapters.memory.job_store import MemoryJobStore
from insolvia_api.adapters.postgres.job_store import PostgresJobStore
from insolvia_api.core.jobs import Job, new_job, start_attempt
from insolvia_core.adapters.postgres.connection import connect

CASE = "case-0001"
OTHER_CASE = "case-0002"
ALICE = "00000000-0000-4000-8000-00000000a11c"


def job(case_id: str = CASE) -> Job:
    return new_job("echo", case_id=case_id, created_by=ALICE)


@pytest.fixture(params=("memory", "postgres"))
def store(request):
    if request.param == "memory":
        return MemoryJobStore()
    request.getfixturevalue("postgres")
    url = request.getfixturevalue("postgres_url")
    with connect(url) as conn:
        for case_id in (CASE, OTHER_CASE):
            conn.execute(
                "INSERT INTO cases (id, firm_id, created_by, chapter, district,"
                " status, created_at, updated_at)"
                " VALUES (%s, 'firm-1', 'seed', 7, 'Seed', 'intake', now(), now())",
                (case_id,),
            )
    return PostgresJobStore(url)


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
