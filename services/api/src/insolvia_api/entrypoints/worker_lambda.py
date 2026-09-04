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

from insolvia_api.adapters.aws.access_log import DynamoDbAccessLog
from insolvia_api.adapters.aws.case_entity_store import DynamoDbCaseEntityStore
from insolvia_api.adapters.aws.case_store import DynamoDbCaseStore
from insolvia_api.adapters.aws.debtor_store import DynamoDbDebtorStore
from insolvia_api.adapters.aws.document_blobs import S3DocumentBlobStore
from insolvia_api.adapters.aws.job_store import DynamoDbJobStore
from insolvia_api.adapters.aws.packet_store import DynamoDbPacketStore
from insolvia_api.core.config import load_config
from insolvia_api.core.jobs import WORKERS, handle_sqs_event
from insolvia_api.core.logging import configure_logging
from insolvia_api.core.packet_assembly import (
    PACKET_ASSEMBLY_KIND,
    PacketAssemblyDeps,
    packet_assembly_worker,
)

configure_logging()

config = load_config()
# Hard-required, mirroring api_lambda.py's rule: the job record IS the
# pipeline's truth, and a worker that cannot reach it has no degraded mode —
# refusing to boot is discovered by the deploy, not by a stuck job.
if not config.case_table_name:
    raise RuntimeError("CASE_TABLE_NAME must be set for the worker Lambda entrypoint")
# Hard-required with the packet worker on board (issue #96): assembly reads
# the whole case file (which must be access-logged — the api_lambda pair rule)
# and stores its bytes in the document bucket. The deploy workflow injects the
# same /insolvia/<env>/api namespace as the API, so both are always present in
# a deployed environment; refusing to boot is discovered by the deploy.
if not config.case_access_log_table_name or not config.case_document_bucket:
    raise RuntimeError(
        "CASE_ACCESS_LOG_TABLE_NAME and CASE_DOCUMENT_BUCKET must be set "
        "for the worker Lambda entrypoint"
    )

_store = DynamoDbJobStore(config.case_table_name)

# The full registry: core/jobs.WORKERS holds the dependency-free workers, and
# the store-reading ones are composed HERE, where the adapters exist —
# core/jobs.KINDS is what keeps the accept endpoint and this mapping naming
# the same kinds (tests/test_jobs.py pins that).
_case_store = DynamoDbCaseStore(config.case_table_name)
_workers = {
    **WORKERS,
    PACKET_ASSEMBLY_KIND: packet_assembly_worker(
        PacketAssemblyDeps(
            case_store=_case_store,
            debtor_store=DynamoDbDebtorStore(config.case_table_name),
            entity_store=DynamoDbCaseEntityStore(config.case_table_name),
            packet_store=DynamoDbPacketStore(config.case_table_name),
            blobs=S3DocumentBlobStore(config.case_document_bucket),
            access_log=DynamoDbAccessLog(config.case_access_log_table_name),
        )
    ),
}


def handler(event: dict[str, Any], context: object) -> None:
    handle_sqs_event(event, store=_store, workers=_workers)
