from __future__ import annotations

import json
import logging
import queue
import threading
from collections.abc import Callable, Mapping
from typing import Any

from insolvia_api.core.jobs import Job, handle_sqs_event, job_message
from insolvia_api.core.ports import JobStore

logger = logging.getLogger(__name__)


class InProcessJobQueue:
    """The job queue's local stand-in (#624, ADR 0030): a queue in this
    process and a poller thread draining it, so a laptop with no AWS session
    runs accepted jobs — packets, extraction — for real.

    The poller feeds the SAME core/jobs.handle_sqs_event the worker Lambda
    does, with the same wire body, so the consume path is the deployed one.
    shortcut: no redelivery — a job that raises is marked failed and logged,
    not retried as SQS would; use the per-machine dev queue to test retries.
    """

    def __init__(
        self,
        *,
        store: JobStore,
        workers: Mapping[str, Callable[[Job], dict[str, Any]]],
    ) -> None:
        self._bodies: queue.Queue[str] = queue.Queue()
        self._store = store
        self._workers = workers
        threading.Thread(target=self._poll, name="job-poller", daemon=True).start()

    def enqueue(self, job: Job) -> None:
        self._bodies.put(json.dumps(job_message(job), separators=(",", ":")))

    def join(self) -> None:
        """Block until every enqueued job has run — for tests."""
        self._bodies.join()

    def _poll(self) -> None:
        while True:
            body = self._bodies.get()
            try:
                handle_sqs_event(
                    {"Records": [{"body": body}]},
                    store=self._store,
                    workers=self._workers,
                )
            except Exception:
                logger.exception("local job failed; not retried")
            finally:
                self._bodies.task_done()
