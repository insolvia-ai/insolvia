from __future__ import annotations

from typing import Any

import boto3
from botocore.exceptions import ClientError

from insolvia_api.adapters.aws.dynamo import from_attributes, to_attributes
from insolvia_api.core.cases import partition_key
from insolvia_api.core.jobs import Job, job_from_item, job_item, list_order, sort_key


class DynamoDbJobStore:
    """JobStore backed by DynamoDB.

    Same table and same partition as the case root — a job is a child item
    (SK = JOB#<id>), so credentials, the absence of a local emulator, and the
    per-machine dev table all work exactly as they do for
    DynamoDbCaseEntityStore. The WORKER Lambda composes this class too, over
    the same table, under its own execution role's grant
    (infra/modules/case_store, worker_role_name).
    """

    def __init__(self, table_name: str) -> None:
        self.table_name = table_name
        self.client = boto3.client("dynamodb")

    def create(self, job: Job) -> None:
        # Ids are server-minted uuid4s; a collision means the minting is
        # broken, and silently replacing would erase a record to hide that.
        try:
            self.client.put_item(
                TableName=self.table_name,
                Item=to_attributes(job_item(job)),
                ConditionExpression="attribute_not_exists(SK)",
            )
        except ClientError as error:
            if (
                error.response.get("Error", {}).get("Code")
                == "ConditionalCheckFailedException"
            ):
                raise RuntimeError("job id already exists in this case") from error
            raise

    def get(self, case_id: str, job_id: str) -> Job | None:
        response = self.client.get_item(
            TableName=self.table_name,
            Key={
                "PK": {"S": partition_key(case_id)},
                "SK": {"S": sort_key(job_id)},
            },
            # Strongly consistent on purpose, and here it is load-bearing
            # rather than cosmetic: the worker's read races the accept
            # endpoint's write by design (the message is sent immediately
            # after the row lands), and an eventually consistent miss would
            # make run_job drop a perfectly real job as "stray".
            ConsistentRead=True,
        )
        item = response.get("Item")
        if not item:
            return None
        return job_from_item(from_attributes(item))

    def list_for_case(self, case_id: str) -> tuple[Job, ...]:
        # Paginated internally: the port promises ALL jobs, and the accept
        # endpoint's idempotency check reads this — a truncated page would
        # let a duplicate pipeline run through.
        jobs: list[Job] = []
        exclusive_start: dict[str, Any] | None = None
        while True:
            kwargs: dict[str, Any] = {
                "TableName": self.table_name,
                "KeyConditionExpression": "PK = :case AND begins_with(SK, :prefix)",
                "ExpressionAttributeValues": {
                    ":case": {"S": partition_key(case_id)},
                    ":prefix": {"S": "JOB#"},
                },
                "ConsistentRead": True,
            }
            if exclusive_start is not None:
                kwargs["ExclusiveStartKey"] = exclusive_start
            response = self.client.query(**kwargs)
            jobs.extend(
                job_from_item(from_attributes(item))
                for item in response.get("Items", [])
            )
            exclusive_start = response.get("LastEvaluatedKey")
            if not exclusive_start:
                break
        # Sorted explicitly: the SK embeds a random uuid, which orders items
        # by coin flip. list_order is the one definition, shared with the
        # memory store.
        return tuple(sorted(jobs, key=list_order))

    def update(self, job: Job, *, expected_status: str) -> Job | None:
        # The compare-and-swap every transition rides on: replace the whole
        # item, but only while the stored status is what the caller read.
        # `#status` because `status` is a DynamoDB reserved word.
        try:
            self.client.put_item(
                TableName=self.table_name,
                Item=to_attributes(job_item(job)),
                ConditionExpression="attribute_exists(SK) AND #status = :expected",
                ExpressionAttributeNames={"#status": "status"},
                ExpressionAttributeValues={":expected": {"S": expected_status}},
            )
        except ClientError as error:
            if (
                error.response.get("Error", {}).get("Code")
                == "ConditionalCheckFailedException"
            ):
                return None
            raise
        return job
