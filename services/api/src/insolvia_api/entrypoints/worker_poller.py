"""The local pipeline worker: long-polls this machine's dev queue and runs
jobs on the developer's laptop (ADR 0018's local story).

There is no Lambda locally — infra/envs/dev deliberately provisions none —
but the queue is real, so the one thing the event source mapping does that a
laptop cannot is the managed delivery loop itself. This is that loop's
nearest approximation, and it is deliberately shaped like the mapping's
contract at batch size 1: receive one message, feed it through the SAME
core/jobs.handle_sqs_event the Lambda entrypoint uses, delete on a clean
return, and leave it for redelivery when the handler raises (which is exactly
what an erroring Lambda does — the visibility timeout is the retry delay, and
maxReceiveCount still walks a poison message to the dev DLQ).

Run it beside the dev server, with services/api/.env loaded:

    set -a; source services/api/.env; set +a
    python -m insolvia_api.entrypoints.worker_poller

Ctrl-C stops it between polls. Jobs run under YOUR credentials against YOUR
dev tables — the same per-machine-principal shape as every dev-aws resource.
"""

from __future__ import annotations

import logging

import boto3
from insolvia_core.adapters.aws.access_log import DynamoDbAccessLog
from insolvia_core.adapters.aws.case_entity_store import DynamoDbCaseEntityStore
from insolvia_core.adapters.aws.case_store import DynamoDbCaseStore
from insolvia_core.adapters.aws.debtor_store import DynamoDbDebtorStore
from insolvia_core.adapters.aws.document_blobs import S3DocumentBlobStore

from insolvia_api.adapters.anthropic.review_model import AnthropicReviewModel
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
from insolvia_api.core.petition_review import (
    PETITION_REVIEW_KIND,
    PetitionReviewDeps,
    petition_review_worker,
)

logger = logging.getLogger(__name__)


def main() -> None:
    configure_logging()
    config = load_config()
    if not config.case_table_name or not config.job_queue_url:
        raise RuntimeError(
            "CASE_TABLE_NAME and JOB_QUEUE_URL must be set for the worker poller "
            "(scripts/dev-aws-setup.sh writes both into services/api/.env)"
        )
    # The packet worker's extra reach (issue #96) — same dev-aws provisioning,
    # same .env, and the worker Lambda entrypoint states the same pair.
    if not config.case_access_log_table_name or not config.case_document_bucket:
        raise RuntimeError(
            "CASE_ACCESS_LOG_TABLE_NAME and CASE_DOCUMENT_BUCKET must be set "
            "for the worker poller (scripts/dev-aws-setup.sh writes both into "
            "services/api/.env)"
        )

    store = DynamoDbJobStore(config.case_table_name)
    # The same composition the worker Lambda does — a laptop runs the exact
    # worker the cloud runs, against this machine's real dev resources. The
    # AI review's model rides ANTHROPIC_API_KEY from services/api/.env
    # (gitignored; add your own key by hand — dev-aws-setup does not write
    # it); without one, `petition_review` jobs fail honestly with
    # `not_configured` and everything else runs as before.
    case_store = DynamoDbCaseStore(config.case_table_name)
    debtor_store = DynamoDbDebtorStore(config.case_table_name)
    entity_store = DynamoDbCaseEntityStore(config.case_table_name)
    packet_store = DynamoDbPacketStore(config.case_table_name)
    access_log = DynamoDbAccessLog(config.case_access_log_table_name)
    review_model = (
        AnthropicReviewModel(config.anthropic_api_key)
        if config.anthropic_api_key
        else None
    )
    workers = {
        **WORKERS,
        PACKET_ASSEMBLY_KIND: packet_assembly_worker(
            PacketAssemblyDeps(
                case_store=case_store,
                debtor_store=debtor_store,
                entity_store=entity_store,
                packet_store=packet_store,
                blobs=S3DocumentBlobStore(config.case_document_bucket),
                access_log=access_log,
            )
        ),
        PETITION_REVIEW_KIND: petition_review_worker(
            PetitionReviewDeps(
                case_store=case_store,
                debtor_store=debtor_store,
                entity_store=entity_store,
                packet_store=packet_store,
                access_log=access_log,
                model=review_model,
            )
        ),
    }
    sqs = boto3.client("sqs")
    logger.info("worker poller listening", extra={"queue": config.job_queue_url})

    while True:
        response = sqs.receive_message(
            QueueUrl=config.job_queue_url,
            MaxNumberOfMessages=1,
            WaitTimeSeconds=20,
        )
        for message in response.get("Messages", []):
            try:
                # The Lambda event shape at batch size 1, so the dispatch path
                # is byte-identical to the deployed one.
                handle_sqs_event(
                    {"Records": [{"body": message["Body"]}]},
                    store=store,
                    workers=workers,
                )
            except Exception:
                # Mirror Lambda semantics: no delete, so the visibility
                # timeout redelivers and maxReceiveCount eventually parks it
                # on the dev DLQ.
                logger.exception("job message failed; leaving for redelivery")
            else:
                sqs.delete_message(
                    QueueUrl=config.job_queue_url,
                    ReceiptHandle=message["ReceiptHandle"],
                )


if __name__ == "__main__":
    main()
