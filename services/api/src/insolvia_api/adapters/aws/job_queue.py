from __future__ import annotations

import json

import boto3

from insolvia_api.core.jobs import Job, job_message


class SqsJobQueue:
    """JobQueue backed by SQS — the enqueue side of the pipeline (ADR 0018).

    Deliberately thin: the message body comes from core/jobs.job_message, the
    one owner of the wire shape (identifiers only, never case data), so this
    adapter cannot invent a contract of its own. The consuming side —
    entrypoints/worker_lambda.py, and worker_poller.py locally — parses with
    the same module's parse_job_message; tests/test_jobs.py pins the shape.

    Locally this is real too: JOB_QUEUE_URL in services/api/.env points at
    this machine's own dev queue (infra/envs/dev), and the poller consumes
    it. No emulator, same semantics.
    """

    def __init__(self, queue_url: str) -> None:
        self.queue_url = queue_url
        self.client = boto3.client("sqs")

    def enqueue(self, job: Job) -> None:
        self.client.send_message(
            QueueUrl=self.queue_url,
            MessageBody=json.dumps(job_message(job), separators=(",", ":")),
        )
