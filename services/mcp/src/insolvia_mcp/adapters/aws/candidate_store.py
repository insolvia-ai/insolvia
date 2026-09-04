from __future__ import annotations

from typing import Any

import boto3
from botocore.exceptions import ClientError
from insolvia_core.adapters.aws.dynamo import from_attributes, to_attributes
from insolvia_core.cases import partition_key

from insolvia_mcp.core.candidates import (
    Candidate,
    candidate_from_item,
    candidate_item,
    list_order,
    sort_key,
)


class DynamoDbCandidateStore:
    """CandidateStore backed by DynamoDB.

    The same table and the same partition as the case root — a candidate is a
    child item of its case (SK = CANDIDATE#<id>, the namespace registered in
    insolvia_core.case_collections.RESERVED_SK_NAMESPACES) — so credentials,
    the absence of a local emulator, and the per-machine dev table all work
    exactly as they do for the shared case-entity store.
    """

    def __init__(self, table_name: str) -> None:
        self.table_name = table_name
        self.client = boto3.client("dynamodb")

    def create(self, candidate: Candidate) -> None:
        # Ids are server-minted uuid4s, so this condition should never fire —
        # which is exactly why it raises rather than returning False: a
        # collision here means the minting is broken.
        try:
            self.client.put_item(
                TableName=self.table_name,
                Item=to_attributes(candidate_item(candidate)),
                ConditionExpression="attribute_not_exists(SK)",
            )
        except ClientError as error:
            if (
                error.response.get("Error", {}).get("Code")
                == "ConditionalCheckFailedException"
            ):
                raise RuntimeError(
                    "candidate id already exists in this case"
                ) from error
            raise

    def get(self, case_id: str, candidate_id: str) -> Candidate | None:
        response = self.client.get_item(
            TableName=self.table_name,
            Key={
                "PK": {"S": partition_key(case_id)},
                "SK": {"S": sort_key(candidate_id)},
            },
            # Strongly consistent because withdrawal reads and then
            # compare-and-swaps: an eventually consistent read could show
            # `pending` for a candidate the reviewer just accepted, and the
            # caller would draft a withdrawal the CAS then (correctly)
            # refuses — a confusing round trip a consistent read avoids.
            ConsistentRead=True,
        )
        item = response.get("Item")
        if not item:
            return None
        return candidate_from_item(from_attributes(item))

    def list_for_case(self, case_id: str) -> tuple[Candidate, ...]:
        # Paginated INTERNALLY: a busy agent session can queue many proposals,
        # and the port promises all of them — the tool layer paginates.
        items: list[Candidate] = []
        exclusive_start: dict[str, Any] | None = None
        while True:
            kwargs: dict[str, Any] = {
                "TableName": self.table_name,
                "KeyConditionExpression": "PK = :case AND begins_with(SK, :prefix)",
                "ExpressionAttributeValues": {
                    ":case": {"S": partition_key(case_id)},
                    ":prefix": {"S": "CANDIDATE#"},
                },
                "ConsistentRead": True,
            }
            if exclusive_start is not None:
                kwargs["ExclusiveStartKey"] = exclusive_start
            response = self.client.query(**kwargs)
            items.extend(
                candidate_from_item(from_attributes(item))
                for item in response.get("Items", [])
            )
            exclusive_start = response.get("LastEvaluatedKey")
            if not exclusive_start:
                break
        # Sorted explicitly rather than trusting the sort-key order: the SK
        # embeds a random uuid, which orders items by coin flip.
        return tuple(sorted(items, key=list_order))

    def update(self, candidate: Candidate, *, expected_status: str) -> Candidate | None:
        # The compare-and-swap the Protocol demands: a withdrawal racing the
        # reviewer's acceptance must lose, not overwrite it.
        try:
            self.client.put_item(
                TableName=self.table_name,
                Item=to_attributes(candidate_item(candidate)),
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
        return candidate
