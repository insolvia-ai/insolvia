from __future__ import annotations

from typing import Any, Final

import boto3
from botocore.exceptions import ClientError

from insolvia_core.cases import partition_key
from insolvia_core.tasks import Task, list_order, sort_key, task_from_item, task_item

# Derived from the one function that builds task sort keys, so the two cannot
# drift — the same TASK_PREFIX argument DOCUMENT_PREFIX makes in
# adapters/aws/document_store.py.
TASK_PREFIX: Final = sort_key("")


# Flat by construction — a task item is strings and one bool, with no nested
# map anywhere — the same two-line conversion adapters/aws/document_store.py
# carries for the same reason.
def _to_attributes(item: dict[str, str | bool]) -> dict[str, Any]:
    return {
        key: {"BOOL": value} if isinstance(value, bool) else {"S": value}
        for key, value in item.items()
    }


def _from_attributes(item: dict[str, Any]) -> dict[str, str | bool]:
    plain: dict[str, str | bool] = {}
    for key, value in item.items():
        if "BOOL" in value:
            plain[key] = bool(value["BOOL"])
        elif "S" in value:
            plain[key] = value["S"]
    return plain


class DynamoDbTaskStore:
    """TaskStore backed by DynamoDB.

    The same table and the same partition as DynamoDbCaseStore — a task row
    is a child item of its case, not a row in a table of its own — so
    credentials, the absence of a local emulator, and the per-machine dev
    table all work exactly as they do there.
    """

    def __init__(self, table_name: str) -> None:
        self.table_name = table_name
        self.client = boto3.client("dynamodb")

    def create(self, task: Task) -> None:
        self.client.put_item(
            TableName=self.table_name,
            Item=_to_attributes(task_item(task)),
            # attribute_not_exists(SK): the id is a uuid4 so a collision is
            # not a real risk — this rules out a retried request silently
            # replacing a row a client already has a copy of.
            ConditionExpression="attribute_not_exists(SK)",
        )

    def get(self, case_id: str, task_id: str) -> Task | None:
        response = self.client.get_item(
            TableName=self.table_name,
            Key={
                "PK": {"S": partition_key(case_id)},
                "SK": {"S": sort_key(task_id)},
            },
            ConsistentRead=True,
        )
        item = response.get("Item")
        if not item:
            return None
        return task_from_item(_from_attributes(item))

    def update(self, task: Task) -> Task | None:
        try:
            self.client.put_item(
                TableName=self.table_name,
                Item=_to_attributes(task_item(task)),
                # attribute_exists(SK): the mirror of create's condition,
                # exactly as DynamoDbDocumentStore.update states — an edit
                # racing a delete must not resurrect the row.
                ConditionExpression="attribute_exists(SK)",
            )
        except ClientError as error:
            if (
                error.response.get("Error", {}).get("Code")
                == "ConditionalCheckFailedException"
            ):
                return None
            raise
        return task

    def list_for_case(self, case_id: str) -> tuple[Task, ...]:
        items: list[dict[str, Any]] = []
        start_key: dict[str, Any] | None = None
        while True:
            kwargs: dict[str, Any] = {
                "TableName": self.table_name,
                "KeyConditionExpression": "PK = :case AND begins_with(SK, :prefix)",
                "ExpressionAttributeValues": {
                    ":case": {"S": partition_key(case_id)},
                    ":prefix": {"S": TASK_PREFIX},
                },
                "ConsistentRead": True,
            }
            if start_key is not None:
                kwargs["ExclusiveStartKey"] = start_key
            response = self.client.query(**kwargs)
            items.extend(response.get("Items", []))
            start_key = response.get("LastEvaluatedKey")
            if not start_key:
                break

        return tuple(
            sorted(
                (task_from_item(_from_attributes(item)) for item in items),
                key=list_order,
            )
        )

    def delete(self, case_id: str, task_id: str) -> bool:
        try:
            self.client.delete_item(
                TableName=self.table_name,
                Key={
                    "PK": {"S": partition_key(case_id)},
                    "SK": {"S": sort_key(task_id)},
                },
                ConditionExpression="attribute_exists(SK)",
            )
        except ClientError as error:
            if (
                error.response.get("Error", {}).get("Code")
                == "ConditionalCheckFailedException"
            ):
                return False
            raise
        return True
