"""DynamoDB FilingStore — the filing records in the case table
(core/filings.py owns the shape; this adapter owns only the conditions).

Two writes, both conditional, and the conditions are the idempotency:

- `claim`     PutItem, `attribute_not_exists(PK)` — one record per filing;
- `transition` PutItem of the whole record, `#state = :expected AND
              attemptId = :attempt` — only the attempt that holds the record
              moves it, and only from the state it read.

A whole-item PutItem (not an UpdateItem) because the record is small and
every transition rewrites its history; the condition is what makes it safe.
The worker role's grant (infra/modules/case_store, filing_worker_role_name)
holds PutItem on the case table for these items, the receipt's document item,
and the approval's consume — nothing else in this service writes the table.
"""

from __future__ import annotations

from decimal import Decimal
from typing import Any

import boto3
from botocore.exceptions import ClientError
from insolvia_core.cases import partition_key

from ...core.filings import Filing, filing_from_item, filing_item, sort_key


def _plain(value: Any) -> Any:
    if isinstance(value, Decimal):
        return int(value) if value == value.to_integral_value() else float(value)
    if isinstance(value, list):
        return [_plain(entry) for entry in value]
    if isinstance(value, dict):
        return {key: _plain(entry) for key, entry in value.items()}
    return value


def _condition_failed(error: ClientError) -> bool:
    code = error.response.get("Error", {}).get("Code")
    return bool(code == "ConditionalCheckFailedException")


class DynamoDbFilingStore:
    def __init__(self, table_name: str, *, resource: Any = None) -> None:
        self._table = (resource or boto3.resource("dynamodb")).Table(table_name)

    def get(self, case_id: str, filing_id: str) -> Filing | None:
        response = self._table.get_item(
            Key={"PK": partition_key(case_id), "SK": sort_key(filing_id)},
            ConsistentRead=True,
        )
        item = response.get("Item")
        return filing_from_item(_plain(item)) if item is not None else None

    def claim(self, filing: Filing) -> bool:
        try:
            self._table.put_item(
                Item=filing_item(filing),
                ConditionExpression="attribute_not_exists(PK)",
            )
        except ClientError as error:
            if _condition_failed(error):
                return False
            raise
        return True

    def transition(self, filing: Filing, *, expected_state: str) -> bool:
        try:
            self._table.put_item(
                Item=filing_item(filing),
                ConditionExpression="#state = :expected AND attemptId = :attempt",
                ExpressionAttributeNames={"#state": "state"},
                ExpressionAttributeValues={
                    ":expected": expected_state,
                    ":attempt": filing.attempt_id,
                },
            )
        except ClientError as error:
            if _condition_failed(error):
                return False
            raise
        return True
