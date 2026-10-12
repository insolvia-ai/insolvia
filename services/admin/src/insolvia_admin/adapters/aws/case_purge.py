"""`CaseTableRows` over the real case table and firm table (ADR 0022's
pre-client purge — `insolvia_admin.core.pre_client_purge` owns the rule).

Scan, not a GSI: the question is "every case partition in this table", and
neither listing index answers it without first knowing every firm. The scan
projects keys and `clientId` only, so it reads no case data it does not need
— though DynamoDB still decrypts each item to read even that, which is why the
principal needs the case key through DynamoDB.
"""

from __future__ import annotations

from collections.abc import Iterator, Mapping
from typing import Any

import boto3
from botocore.exceptions import ClientError

from insolvia_admin.core.pre_client_purge import (
    CASE_PREFIX,
    DEBTOR_PREFIX,
    BindingRow,
)

_CONDITION_FAILED = "ConditionalCheckFailedException"


def _plain(item: Mapping[str, Any]) -> dict[str, object]:
    """Only the string attributes the purge reads — every one it needs is a
    string, and nothing else about a row should leave the adapter."""
    return {key: value["S"] for key, value in item.items() if "S" in value}


class DynamoDbCaseTableRows:
    def __init__(self, case_table: str, firm_table: str) -> None:
        self.case_table = case_table
        self.firm_table = firm_table
        self.client = boto3.client("dynamodb")

    def summaries(self) -> Iterator[Mapping[str, object]]:
        start: dict[str, Any] | None = None
        while True:
            kwargs: dict[str, Any] = {
                "TableName": self.case_table,
                "ProjectionExpression": "PK, SK, clientId",
                # Consistent: a case opened a moment ago must show its
                # client-linked debtor, or the plan would name it — and the
                # per-partition re-read and the conditional deletes are the
                # second and third fences, not the first.
                "ConsistentRead": True,
            }
            if start is not None:
                kwargs["ExclusiveStartKey"] = start
            response = self.client.scan(**kwargs)
            for item in response.get("Items", []):
                yield _plain(item)
            start = response.get("LastEvaluatedKey")
            if not start:
                return

    def partition(self, case_id: str) -> list[Mapping[str, object]]:
        items: list[Mapping[str, object]] = []
        start: dict[str, Any] | None = None
        while True:
            kwargs: dict[str, Any] = {
                "TableName": self.case_table,
                "KeyConditionExpression": "PK = :pk",
                "ExpressionAttributeValues": {":pk": {"S": f"{CASE_PREFIX}{case_id}"}},
                "ConsistentRead": True,
            }
            if start is not None:
                kwargs["ExclusiveStartKey"] = start
            response = self.client.query(**kwargs)
            items.extend(_plain(item) for item in response.get("Items", []))
            start = response.get("LastEvaluatedKey")
            if not start:
                return items

    def delete(self, item: Mapping[str, object]) -> bool:
        kwargs: dict[str, Any] = {
            "TableName": self.case_table,
            "Key": {"PK": {"S": str(item["PK"])}, "SK": {"S": str(item["SK"])}},
        }
        if str(item["SK"]).startswith(DEBTOR_PREFIX):
            # The item-level fence: a debtor that names a client is never
            # deleted, whatever the plan said.
            kwargs["ConditionExpression"] = "attribute_not_exists(clientId)"
        try:
            self.client.delete_item(**kwargs)
        except ClientError as error:
            if error.response.get("Error", {}).get("Code") == _CONDITION_FAILED:
                return False
            raise
        return True

    def delete_binding(self, row: BindingRow) -> bool:
        try:
            self.client.delete_item(
                TableName=self.firm_table,
                Key={
                    "PK": {"S": f"FIRM#{row.firm_id}"},
                    "SK": {"S": f"CLIENT#{row.subject}"},
                },
                # Only while it still binds the purged case: a client since
                # re-bound to another matter keeps that binding.
                ConditionExpression="caseId = :case",
                ExpressionAttributeValues={":case": {"S": row.case_id}},
            )
        except ClientError as error:
            if error.response.get("Error", {}).get("Code") == _CONDITION_FAILED:
                return False
            raise
        return True
