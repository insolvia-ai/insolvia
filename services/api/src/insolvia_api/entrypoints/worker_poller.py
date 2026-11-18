"""The per-machine dev queue's consumer: long-polls this machine's dev queue and runs
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

Only for a machine that applied the optional per-machine dev stack
(infra/envs/dev). The ordinary local stack needs none of this: with no
JOB_QUEUE_URL the development server runs jobs on its own in-process queue
(adapters/memory/in_process_job_queue.py, #624).

Run it beside the dev server, with services/api/.env loaded:

    set -a; source services/api/.env; set +a
    python -m insolvia_api.entrypoints.worker_poller

Ctrl-C stops it between polls. Jobs run under YOUR credentials against YOUR
dev tables — the same per-machine-principal shape as every dev-aws resource.
"""

from __future__ import annotations

import logging

import boto3
from insolvia_core.adapters.aws.document_blobs import S3DocumentBlobStore
from insolvia_core.adapters.aws.tax_id_cipher import KmsTaxIdCipher, case_key_alias
from insolvia_core.adapters.postgres.access_log import PostgresAccessLog
from insolvia_core.adapters.postgres.candidate_store import PostgresCandidateStore
from insolvia_core.adapters.postgres.case_entity_store import PostgresCaseEntityStore
from insolvia_core.adapters.postgres.case_store import PostgresCaseStore
from insolvia_core.adapters.postgres.debtor_store import PostgresDebtorStore
from insolvia_core.adapters.postgres.document_store import PostgresDocumentStore
from insolvia_core.adapters.postgres.tax_id_store import PostgresTaxIdStore

from insolvia_api.adapters.postgres.job_store import PostgresJobStore
from insolvia_api.adapters.postgres.packet_store import PostgresPacketStore
from insolvia_api.core.config import load_config
from insolvia_api.core.jobs import handle_sqs_event
from insolvia_api.core.logging import configure_logging
from insolvia_api.entrypoints.workers import compose_workers

logger = logging.getLogger(__name__)


def main() -> None:
    configure_logging()
    config = load_config()
    database_url = config.database_url
    if database_url is None:
        raise RuntimeError("DATABASE_URL must be set")
    if not config.case_table_name or not config.job_queue_url:
        raise RuntimeError(
            "CASE_TABLE_NAME and JOB_QUEUE_URL must be set for the worker poller "
            "(scripts/dev-aws-setup.sh writes both into services/api/.env)"
        )
    # The packet worker's extra reach (issue #96) — same dev-aws provisioning,
    # same .env, and the worker Lambda entrypoint states the same pair.
    if not config.case_document_bucket:
        raise RuntimeError(
            "CASE_DOCUMENT_BUCKET must be set for the worker poller "
            "(scripts/dev-aws-setup.sh writes it into services/api/.env)"
        )

    store = PostgresJobStore(database_url)
    # The same composition the worker Lambda does — a laptop runs the exact
    # worker the cloud runs, against this machine's real dev resources. The
    # AI models ride ANTHROPIC_API_KEY from services/api/.env (gitignored;
    # add your own key by hand).
    workers = compose_workers(
        case_store=PostgresCaseStore(database_url),
        debtor_store=PostgresDebtorStore(database_url),
        entity_store=PostgresCaseEntityStore(database_url),
        packet_store=PostgresPacketStore(database_url),
        document_store=PostgresDocumentStore(database_url),
        candidate_store=PostgresCandidateStore(database_url),
        blobs=S3DocumentBlobStore(config.case_document_bucket),
        access_log=PostgresAccessLog(database_url),
        # Under this machine's own case key (root delegation).
        tax_id_store=PostgresTaxIdStore(database_url),
        tax_id_cipher=KmsTaxIdCipher(case_key_alias(config.case_table_name)),
        anthropic_api_key=config.anthropic_api_key,
    )
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
