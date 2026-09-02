from __future__ import annotations

from insolvia_api.core.jobs import Job, job_message


class MemoryJobQueue:
    """Ephemeral JobQueue for tests and the plain development server.

    Records the exact WIRE message (job_message's dict), not the Job, so a
    route test asserts what would actually cross the seam. It does not run
    the job — locally, jobs run by pointing JOB_QUEUE_URL at this machine's
    dev queue and running entrypoints/worker_poller.py; in tests, workers
    and run_job are called directly (ADR 0018's local story).
    """

    def __init__(self) -> None:
        self.messages: list[dict[str, object]] = []

    def enqueue(self, job: Job) -> None:
        self.messages.append(job_message(job))
