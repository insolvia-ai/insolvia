from __future__ import annotations

from typing import Any, Final

import boto3
from botocore.exceptions import ClientError

from insolvia_core.adapters.aws.dynamo import from_attributes, to_attributes
from insolvia_core.cases import partition_key
from insolvia_core.document_requests import (
    DocumentRequest,
    list_order,
    request_from_item,
    request_item,
    sort_key,
)

# Derived from the one function that builds the sort key — DOCUMENT_PREFIX's
# argument in adapters/aws/document_store.py. "DOCREQUEST#" and "DOCUMENT#"
# share no prefix, so neither listing reads the other's rows.
REQUEST_PREFIX: Final = sort_key("")

_CONDITION_FAILED: Final = "ConditionalCheckFailedException"


class DynamoDbDocumentRequestStore:
    """DocumentRequestStore backed by DynamoDB: the case table, the case's
    own partition, exactly as DynamoDbTaskStore. Items carry a list
    (`documentIds`), so this uses the shared converters in `dynamo.py`
    rather than a flat pair."""

    def __init__(self, table_name: str) -> None:
        self.table_name = table_name
        self.client = boto3.client("dynamodb")

    def _key(self, case_id: str, request_id: str) -> dict[str, Any]:
        return {
            "PK": {"S": partition_key(case_id)},
            "SK": {"S": sort_key(request_id)},
        }

    def create(self, request: DocumentRequest) -> None:
        self.client.put_item(
            TableName=self.table_name,
            Item=to_attributes(request_item(request)),
            ConditionExpression="attribute_not_exists(SK)",
        )

    def get(self, case_id: str, request_id: str) -> DocumentRequest | None:
        response = self.client.get_item(
            TableName=self.table_name,
            Key=self._key(case_id, request_id),
            ConsistentRead=True,
        )
        item = response.get("Item")
        return None if not item else request_from_item(from_attributes(item))

    def update(self, request: DocumentRequest) -> DocumentRequest | None:
        try:
            self.client.put_item(
                TableName=self.table_name,
                Item=to_attributes(request_item(request)),
                # An update racing a delete must not resurrect the row.
                ConditionExpression="attribute_exists(SK)",
            )
        except ClientError as error:
            if error.response.get("Error", {}).get("Code") == _CONDITION_FAILED:
                return None
            raise
        return request

    def list_for_case(self, case_id: str) -> tuple[DocumentRequest, ...]:
        items: list[dict[str, Any]] = []
        start_key: dict[str, Any] | None = None
        while True:
            kwargs: dict[str, Any] = {
                "TableName": self.table_name,
                "KeyConditionExpression": "PK = :case AND begins_with(SK, :prefix)",
                "ExpressionAttributeValues": {
                    ":case": {"S": partition_key(case_id)},
                    ":prefix": {"S": REQUEST_PREFIX},
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
                (request_from_item(from_attributes(item)) for item in items),
                key=list_order,
            )
        )

    def delete(self, case_id: str, request_id: str) -> bool:
        try:
            self.client.delete_item(
                TableName=self.table_name,
                Key=self._key(case_id, request_id),
                ConditionExpression="attribute_exists(SK)",
            )
        except ClientError as error:
            if error.response.get("Error", {}).get("Code") == _CONDITION_FAILED:
                return False
            raise
        return True
