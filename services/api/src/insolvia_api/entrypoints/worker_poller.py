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

from insolvia_api.adapters.aws.job_store import DynamoDbJobStore
from insolvia_api.core.config import load_config
from insolvia_api.core.jobs import WORKERS, handle_sqs_event
from insolvia_api.core.logging import configure_logging

logger = logging.getLogger(__name__)


def main() -> None:
    configure_logging()
    config = load_config()
    if not config.case_table_name or not config.job_queue_url:
        raise RuntimeError(
            "CASE_TABLE_NAME and JOB_QUEUE_URL must be set for the worker poller "
            "(scripts/dev-aws-setup.sh writes both into services/api/.env)"
        )

    store = DynamoDbJobStore(config.case_table_name)
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
                    workers=WORKERS,
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
