"""DynamoDB FilingStore — the filing records in the case table
(insolvia_core.filings owns the shape; this adapter owns only the
conditions).

Every write is conditional, and the conditions are the idempotency:

- `claim`      PutItem, `attribute_not_exists(PK)` — one record per filing;
- `transition` PutItem of the whole record, `#state = :expected AND
               attemptId = :attempt` — only the attempt that holds the
               record moves it, and only from the state it read;
- `resolve`    PutItem of the whole record, `#state = :expected AND
               attemptId = :attempt AND attribute_not_exists(resolution)` —
               a hand-back is resolved once.

With a `FiledCase` (ADR 0024 PR 8) either of the last two is ONE
TransactWriteItems with two more items: an UpdateItem of the case's META
setting exactly the lifecycle attributes the move changes (`status`,
`filedAt`, `caseNumber`, `caseNumberKey`, `updatedAt`), conditional on the
case still existing in its firm with the status the caller read; and the
STATUS# history row, conditional on not existing. An UpdateItem rather than
a whole-record Put so a concurrent edit of anything else on the case — a
debtor's name, the § 341 date — is never written back over.

The filing worker's role (infra/modules/filing_worker, `FilingRecordWrite`)
holds PutItem and UpdateItem on the case table, which is what a transaction
of a Put and an Update needs — IAM authorizes a transaction's items one by
one; it has no separate TransactWriteItems action.
"""

from __future__ import annotations

from typing import Any

import boto3
from botocore.exceptions import ClientError

from insolvia_core.adapters.aws.dynamo import from_attributes, to_attributes
from insolvia_core.cases import (
    CASE_NUMBER_KEY_ATTRIBUTE,
    case_item,
    partition_key,
    status_change_item,
)
from insolvia_core.filings import (
    FiledCase,
    Filing,
    filing_from_item,
    filing_item,
    sort_key,
)

_CONDITION_FAILED = "ConditionalCheckFailedException"
_TRANSACTION_CANCELLED = "TransactionCanceledException"

# The case attributes an electronic filing changes — and nothing else.
_FILED_ATTRIBUTES = ("status", "filedAt", "caseNumber", "updatedAt")


def _refused(error: ClientError) -> bool:
    code = error.response.get("Error", {}).get("Code")
    return code in (_CONDITION_FAILED, _TRANSACTION_CANCELLED)


class DynamoDbFilingStore:
    def __init__(self, table_name: str, *, client: Any = None) -> None:
        self.table_name = table_name
        self.client = client if client is not None else boto3.client("dynamodb")

    def get(self, case_id: str, filing_id: str) -> Filing | None:
        response = self.client.get_item(
            TableName=self.table_name,
            Key={
                "PK": {"S": partition_key(case_id)},
                "SK": {"S": sort_key(filing_id)},
            },
            ConsistentRead=True,
        )
        item = response.get("Item")
        return filing_from_item(from_attributes(item)) if item else None

    def claim(self, filing: Filing) -> bool:
        try:
            self.client.put_item(
                TableName=self.table_name,
                Item=to_attributes(filing_item(filing)),
                ConditionExpression="attribute_not_exists(PK)",
            )
        except ClientError as error:
            if _refused(error):
                return False
            raise
        return True

    def transition(
        self,
        filing: Filing,
        *,
        expected_state: str,
        filed_case: FiledCase | None = None,
    ) -> bool:
        return self._write(
            filing,
            condition="#state = :expected AND attemptId = :attempt",
            expected_state=expected_state,
            filed_case=filed_case,
        )

    def resolve(
        self,
        filing: Filing,
        *,
        expected_state: str,
        filed_case: FiledCase | None = None,
    ) -> bool:
        return self._write(
            filing,
            condition=(
                "#state = :expected AND attemptId = :attempt"
                " AND attribute_not_exists(resolution)"
            ),
            expected_state=expected_state,
            filed_case=filed_case,
        )

    def _write(
        self,
        filing: Filing,
        *,
        condition: str,
        expected_state: str,
        filed_case: FiledCase | None,
    ) -> bool:
        put: dict[str, Any] = {
            "TableName": self.table_name,
            "Item": to_attributes(filing_item(filing)),
            "ConditionExpression": condition,
            "ExpressionAttributeNames": {"#state": "state"},
            "ExpressionAttributeValues": {
                ":expected": {"S": expected_state},
                ":attempt": {"S": filing.attempt_id},
            },
        }
        try:
            if filed_case is None:
                self.client.put_item(**put)
            else:
                self.client.transact_write_items(
                    TransactItems=[
                        {"Put": put},
                        {"Update": self._case_update(filed_case)},
                        {
                            "Put": {
                                "TableName": self.table_name,
                                "Item": to_attributes(
                                    status_change_item(filed_case.status_change)
                                ),
                                "ConditionExpression": "attribute_not_exists(SK)",
                            }
                        },
                    ]
                )
        except ClientError as error:
            if _refused(error):
                return False
            raise
        return True

    def _case_update(self, filed_case: FiledCase) -> dict[str, Any]:
        stored = case_item(filed_case.case)
        names = {f"#a{i}": name for i, name in enumerate(_FILED_ATTRIBUTES)}
        values: dict[str, Any] = {
            f":a{i}": to_attributes({"v": stored[name]})["v"]
            for i, name in enumerate(_FILED_ATTRIBUTES)
        }
        assignments = [f"#a{i} = :a{i}" for i in range(len(_FILED_ATTRIBUTES))]
        removals: list[str] = []
        names["#key"] = CASE_NUMBER_KEY_ATTRIBUTE
        if CASE_NUMBER_KEY_ATTRIBUTE in stored:
            values[":key"] = {"S": str(stored[CASE_NUMBER_KEY_ATTRIBUTE])}
            assignments.append("#key = :key")
        else:
            removals.append("#key")
        names["#status"] = "status"
        values[":firm"] = {"S": filed_case.case.firm_id}
        values[":read"] = {"S": filed_case.expected_status}
        expression = "SET " + ", ".join(assignments)
        if removals:
            expression += " REMOVE " + ", ".join(removals)
        return {
            "TableName": self.table_name,
            "Key": {
                "PK": {"S": partition_key(filed_case.case.id)},
                "SK": {"S": "META"},
            },
            "UpdateExpression": expression,
            "ConditionExpression": (
                "attribute_exists(PK) AND firmId = :firm AND #status = :read"
                " AND attribute_not_exists(deletedAt)"
            ),
            "ExpressionAttributeNames": names,
            "ExpressionAttributeValues": values,
        }
