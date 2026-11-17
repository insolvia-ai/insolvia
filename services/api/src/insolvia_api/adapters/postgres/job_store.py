"""JobStore over Postgres (ADR 0030): the `jobs` table. The worker composes
it too — its read races the accept endpoint's write by design, and a
Postgres read sees every committed write."""

from __future__ import annotations

from insolvia_core.adapters.postgres.children import ChildTable

from insolvia_api.core.jobs import Job, job_from_item, job_item, list_order


class PostgresJobStore:
    def __init__(self, database_url: str) -> None:
        self.rows = ChildTable(database_url, "jobs", ("case_id", "id"))

    def create(self, job: Job) -> None:
        self.rows.create({"case_id": job.case_id, "id": job.id}, job_item(job))

    def get(self, case_id: str, job_id: str) -> Job | None:
        item = self.rows.get({"case_id": case_id, "id": job_id})
        return None if item is None else job_from_item(item)

    def list_for_case(self, case_id: str) -> tuple[Job, ...]:
        # All of them: the accept endpoint's idempotency check reads this.
        items = self.rows.list({"case_id": case_id})
        return tuple(sorted(map(job_from_item, items), key=list_order))

    def update(self, job: Job, *, expected_status: str) -> Job | None:
        # The compare-and-set every transition rides on.
        written = self.rows.replace(
            {"case_id": job.case_id, "id": job.id},
            job_item(job),
            expected_status=expected_status,
        )
        return job if written else None
