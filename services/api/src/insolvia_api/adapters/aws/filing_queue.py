from __future__ import annotations

import json

import boto3

from insolvia_api.core.filing_approval import FilingApproval, filing_job_message


class SqsFilingQueue:
    """FilingQueue backed by the filing worker's own SQS queue
    (infra/modules/filing_queue; ADR 0024).

    Thin on purpose, SqsJobQueue's shape: the body is `filing_job_message`'s
    — the approval id, its filing id and the case id, nothing else — so this
    adapter cannot invent a contract. It is THE ONE `send_message` against
    that queue in the service, and `approve_filing` is its one caller
    (tests/unit/test_filing_approval.py reads every call site). The API
    role's grant on the queue is SendMessage alone.

    Locally this is real too: FILING_QUEUE_URL in services/api/.env names
    this machine's dev queue, where the job waits — nothing consumes it
    until services/filing (PR 7).
    """

    def __init__(self, queue_url: str) -> None:
        self.queue_url = queue_url
        self.client = boto3.client("sqs")

    def enqueue(self, approval: FilingApproval) -> None:
        self.client.send_message(
            QueueUrl=self.queue_url,
            MessageBody=json.dumps(
                filing_job_message(approval), separators=(",", ":"), sort_keys=True
            ),
        )
