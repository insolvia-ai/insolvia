"""The pipeline worker Lambda's entrypoint (ADR 0018, issue #271).

The consuming half of the pipeline: the SQS event source mapping
(infra/modules/job_pipeline) delivers job messages here, batch size 1, and
everything after the parse is core/jobs.handle_sqs_event — the same function
entrypoints/worker_poller.py drives locally, so the Lambda adds composition
and nothing else. An unhandled exception here is the retry signal: SQS
redelivers, and after maxReceiveCount parks the message on the DLQ whose
depth alarms.

Ships in the WORKER image (services/api/Dockerfile, `worker` target), not the
API's — the split ADR 0015 requires, so 9.6/9.7's heavy dependencies never
land in the request path's image.
"""

from __future__ import annotations

from typing import Any

from insolvia_api.adapters.aws.job_store import DynamoDbJobStore
from insolvia_api.core.config import load_config
from insolvia_api.core.jobs import WORKERS, handle_sqs_event
from insolvia_api.core.logging import configure_logging

configure_logging()

config = load_config()
# Hard-required, mirroring api_lambda.py's rule: the job record IS the
# pipeline's truth, and a worker that cannot reach it has no degraded mode —
# refusing to boot is discovered by the deploy, not by a stuck job.
if not config.case_table_name:
    raise RuntimeError("CASE_TABLE_NAME must be set for the worker Lambda entrypoint")

_store = DynamoDbJobStore(config.case_table_name)


def handler(event: dict[str, Any], context: object) -> None:
    handle_sqs_event(event, store=_store, workers=WORKERS)
