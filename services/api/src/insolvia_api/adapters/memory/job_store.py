from __future__ import annotations

from insolvia_api.core.jobs import Job, list_order


class MemoryJobStore:
    """Ephemeral JobStore for tests and the plain development server.

    Keyed by (case_id, job_id) — the DynamoDB adapter's PK and SK split apart
    — so the case scope is a property of this dict rather than something
    every caller has to remember. The conditional `update` mirrors the
    DynamoDB adapter's compare-and-swap exactly, because the at-least-once
    races tests/test_jobs.py exercises are decided by it.
    """

    def __init__(self) -> None:
        self.jobs: dict[tuple[str, str], Job] = {}

    def create(self, job: Job) -> None:
        key = (job.case_id, job.id)
        if key in self.jobs:
            # The Protocol's contract: an existing (case, id) means the
            # server's id minting is broken, and replacing would erase a
            # record to hide it.
            raise RuntimeError("job id already exists in this case")
        self.jobs[key] = job

    def get(self, case_id: str, job_id: str) -> Job | None:
        return self.jobs.get((case_id, job_id))

    def list_for_case(self, case_id: str) -> tuple[Job, ...]:
        return tuple(
            sorted(
                (
                    job
                    for (stored_case_id, _), job in self.jobs.items()
                    if stored_case_id == case_id
                ),
                key=list_order,
            )
        )

    def update(self, job: Job, *, expected_status: str) -> Job | None:
        key = (job.case_id, job.id)
        stored = self.jobs.get(key)
        if stored is None or stored.status != expected_status:
            return None
        self.jobs[key] = job
        return job
