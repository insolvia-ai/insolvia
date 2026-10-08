"""The local filing worker: long-polls this machine's dev filing queue and
runs each job on the laptop — ADR 0018's local story, for ADR 0024's worker.

    ./services/filing/scripts/dev-up.sh       (the fake CM/ECF and this poller)

AS THE WORKER'S ROLE, NOT AS YOU. With FILING_WORKER_ROLE_ARN set (dev-aws-
setup writes it into services/filing/.env), the poller assumes
insolvia-dev-<id>-filing-role before it composes anything — so a laptop run
is held to the role's real, narrow grants and to the vault key's policy,
which refuses Decrypt to everyone else (ADR 0024, guardrail 3). infra/envs/dev
is the only environment whose role trusts a developer to assume it.

The consume-dispatch-delete contract at batch size 1, like the Lambda
mapping: a run that returns deletes its message; one that raises leaves it
for redelivery — which resumes through the filing record and never files
twice.
"""

from __future__ import annotations

import logging
import os

import boto3

from ..core.config import load_config
from ..core.logging import configure_logging
from ..core.worker import run_filing
from .compose import compose

logger = logging.getLogger(__name__)

# The assumed session's lifetime. A filing run is minutes; a poller left
# running longer than this is restarted (dev-up.sh says so).
SESSION_SECONDS = 3600


def assume_worker_role(role_arn: str) -> None:
    """Swap this process's credentials for the worker role's, before any
    client exists."""
    credentials = boto3.client("sts").assume_role(
        RoleArn=role_arn,
        RoleSessionName="dev-filing-poller",
        DurationSeconds=SESSION_SECONDS,
    )["Credentials"]
    os.environ.pop("AWS_PROFILE", None)
    os.environ["AWS_ACCESS_KEY_ID"] = credentials["AccessKeyId"]
    os.environ["AWS_SECRET_ACCESS_KEY"] = credentials["SecretAccessKey"]
    os.environ["AWS_SESSION_TOKEN"] = credentials["SessionToken"]
    boto3.setup_default_session()


def main() -> None:
    configure_logging()
    config = load_config()
    if config.environment != "local":
        raise RuntimeError(
            "the poller is the local worker; deployed runs are the Lambda"
        )
    if not config.filing_queue_url:
        raise RuntimeError(
            "FILING_QUEUE_URL must be set (scripts/dev-aws-setup.sh writes it"
            " into services/filing/.env)"
        )
    if config.worker_role_arn:
        assume_worker_role(config.worker_role_arn)
    deps = compose(config)
    sqs = boto3.client("sqs")
    logger.info("filing poller listening", extra={"queue": config.filing_queue_url})
    while True:
        response = sqs.receive_message(
            QueueUrl=config.filing_queue_url,
            MaxNumberOfMessages=1,
            WaitTimeSeconds=20,
        )
        for message in response.get("Messages", []):
            try:
                result = run_filing(message["Body"], deps)
            except Exception:
                logger.exception("filing job raised; leaving it for redelivery")
                continue
            logger.info(
                "filing job done",
                extra={
                    "outcome": result.outcome,
                    "filing_id": result.filing_id,
                    "reason": result.reason,
                },
            )
            sqs.delete_message(
                QueueUrl=config.filing_queue_url, ReceiptHandle=message["ReceiptHandle"]
            )


if __name__ == "__main__":
    main()
