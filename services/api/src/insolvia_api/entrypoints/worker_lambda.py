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

from insolvia_core.adapters.aws.access_log import DynamoDbAccessLog
from insolvia_core.adapters.aws.candidate_store import DynamoDbCandidateStore
from insolvia_core.adapters.aws.case_entity_store import DynamoDbCaseEntityStore
from insolvia_core.adapters.aws.case_store import DynamoDbCaseStore
from insolvia_core.adapters.aws.debtor_store import DynamoDbDebtorStore
from insolvia_core.adapters.aws.document_blobs import S3DocumentBlobStore
from insolvia_core.adapters.aws.document_store import DynamoDbDocumentStore

from insolvia_api.adapters.anthropic.extraction_model import AnthropicExtractionModel
from insolvia_api.adapters.anthropic.review_model import AnthropicReviewModel
from insolvia_api.adapters.aws.job_store import DynamoDbJobStore
from insolvia_api.adapters.aws.packet_store import DynamoDbPacketStore
from insolvia_api.core.config import load_config
from insolvia_api.core.extraction import (
    DOCUMENT_EXTRACTION_KIND,
    DocumentExtractionDeps,
    document_extraction_worker,
)
from insolvia_api.core.jobs import WORKERS, handle_sqs_event
from insolvia_api.core.logging import configure_logging
from insolvia_api.core.packet_assembly import (
    PACKET_ASSEMBLY_KIND,
    PacketAssemblyDeps,
    packet_assembly_worker,
)
from insolvia_api.core.petition_review import (
    PETITION_REVIEW_KIND,
    PetitionReviewDeps,
    petition_review_worker,
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
_debtor_store = DynamoDbDebtorStore(config.case_table_name)
_entity_store = DynamoDbCaseEntityStore(config.case_table_name)
_packet_store = DynamoDbPacketStore(config.case_table_name)
_access_log = DynamoDbAccessLog(config.case_access_log_table_name)
# The AI review's model seam (issue #97, ADR 0019). Deliberately NOT
# hard-required like the stores above: an environment without the key still
# runs every other job kind, and a `petition_review` job fails
# deterministically with `not_configured` — an honest status, not a boot
# refusal that would take packet assembly down with it. Extraction (8.7/8.8)
# rides the same key under the same rule.
_review_model = (
    AnthropicReviewModel(config.anthropic_api_key) if config.anthropic_api_key else None
)
_extraction_model = (
    AnthropicExtractionModel(config.anthropic_api_key)
    if config.anthropic_api_key
    else None
)
_blobs = S3DocumentBlobStore(config.case_document_bucket)
_workers = {
    **WORKERS,
    PACKET_ASSEMBLY_KIND: packet_assembly_worker(
        PacketAssemblyDeps(
            case_store=_case_store,
            debtor_store=_debtor_store,
            entity_store=_entity_store,
            packet_store=_packet_store,
            blobs=_blobs,
            access_log=_access_log,
        )
    ),
    PETITION_REVIEW_KIND: petition_review_worker(
        PetitionReviewDeps(
            case_store=_case_store,
            debtor_store=_debtor_store,
            entity_store=_entity_store,
            packet_store=_packet_store,
            access_log=_access_log,
            model=_review_model,
        )
    ),
    DOCUMENT_EXTRACTION_KIND: document_extraction_worker(
        DocumentExtractionDeps(
            case_store=_case_store,
            document_store=DynamoDbDocumentStore(config.case_table_name),
            blobs=_blobs,
            candidate_store=DynamoDbCandidateStore(config.case_table_name),
            access_log=_access_log,
            model=_extraction_model,
        )
    ),
}


def handler(event: dict[str, Any], context: object) -> None:
    handle_sqs_event(event, store=_store, workers=_workers)
