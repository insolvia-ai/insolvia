"""The local job queue (#624): an accepted job runs in this process."""

from __future__ import annotations

from insolvia_api.adapters.memory.in_process_job_queue import InProcessJobQueue
from insolvia_api.adapters.memory.job_store import MemoryJobStore
from insolvia_api.core.jobs import WORKERS, new_job

CASE = "00000000-0000-4000-8000-00000000ca5e"
ALICE = "00000000-0000-4000-8000-00000000a11c"


def test_an_enqueued_job_runs_through_the_deployed_consume_path() -> None:
    store = MemoryJobStore()
    jobs = InProcessJobQueue(store=store, workers=WORKERS)
    job = new_job("echo", case_id=CASE, created_by=ALICE)
    store.create(job)
    jobs.enqueue(job)
    jobs.join()
    stored = store.get(CASE, job.id)
    assert stored is not None
    assert stored.status == "succeeded"


def test_a_failing_job_does_not_stop_the_poller() -> None:
    store = MemoryJobStore()

    def explode(_job):
        raise RuntimeError("infrastructure")

    jobs = InProcessJobQueue(store=store, workers={**WORKERS, "echo": explode})
    first = new_job("echo", case_id=CASE, created_by=ALICE)
    store.create(first)
    jobs.enqueue(first)
    jobs.join()
    stored = store.get(CASE, first.id)
    assert stored is not None
    assert stored.status == "failed"

    jobs._workers = WORKERS
    second = new_job("echo", case_id=CASE, created_by=ALICE)
    store.create(second)
    jobs.enqueue(second)
    jobs.join()
    stored = store.get(CASE, second.id)
    assert stored is not None
    assert stored.status == "succeeded"
