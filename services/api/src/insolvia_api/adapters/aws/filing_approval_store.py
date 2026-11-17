from __future__ import annotations

from typing import Any

import boto3
from botocore.exceptions import ClientError
from insolvia_core.adapters.aws.dynamo import from_attributes, to_attributes
from insolvia_core.cases import partition_key
from insolvia_core.errors import ConflictError

from insolvia_api.core.filing_approval import (
    CURRENT_SORT_KEY,
    FilingApproval,
    approval_from_item,
    approval_item,
    sort_key,
)


def _condition_failed(error: ClientError) -> bool:
    return error.response.get("Error", {}).get("Code") in (
        "ConditionalCheckFailedException",
        "TransactionCanceledException",
    )


class DynamoDbFilingApprovalStore:
    """FilingApprovalStore backed by the case table — approvals are child
    items of the case partition, so the API's existing case-table grant
    covers them and nothing new is provisioned. Composed by the API
    (create / current / void) and, from ADR 0024 PR 7, by the filing worker
    (get / consume / void).

    Every write is conditional and the conditions are core/ports'
    contract: a transaction for create (new item, pointer move, supersede),
    and one conditional UpdateItem each for consume and void."""

    def __init__(self, table_name: str, *, client: Any | None = None) -> None:
        self.table_name = table_name
        self.client = client if client is not None else boto3.client("dynamodb")

    def _get(self, case_id: str, sort: str) -> dict[str, Any] | None:
        response = self.client.get_item(
            TableName=self.table_name,
            Key={"PK": {"S": partition_key(case_id)}, "SK": {"S": sort}},
            # Strongly consistent: the consume must see a void written a
            # moment ago, and the approve a consume.
            ConsistentRead=True,
        )
        item = response.get("Item")
        return from_attributes(item) if item else None

    def current(self, case_id: str) -> FilingApproval | None:
        pointer = self._get(case_id, CURRENT_SORT_KEY)
        if pointer is None:
            return None
        return self.get(case_id, str(pointer["approvalId"]))

    def get(self, case_id: str, approval_id: str) -> FilingApproval | None:
        item = self._get(case_id, sort_key(approval_id))
        return approval_from_item(item) if item else None

    def create(
        self,
        approval: FilingApproval,
        *,
        replacing: FilingApproval | None,
        voided_at: str,
    ) -> None:
        pk = {"S": partition_key(approval.case_id)}
        pointer: dict[str, Any] = {
            "TableName": self.table_name,
            "Item": {
                "PK": pk,
                "SK": {"S": CURRENT_SORT_KEY},
                "approvalId": {"S": approval.approval_id},
            },
        }
        if replacing is None:
            pointer["ConditionExpression"] = "attribute_not_exists(SK)"
        else:
            pointer["ConditionExpression"] = "approvalId = :read"
            pointer["ExpressionAttributeValues"] = {
                ":read": {"S": replacing.approval_id}
            }
        items: list[dict[str, Any]] = [
            {
                "Put": {
                    "TableName": self.table_name,
                    "Item": to_attributes(approval_item(approval)),
                    # Ids are server-minted uuid4s; an existing SK means the
                    # minting broke.
                    "ConditionExpression": "attribute_not_exists(SK)",
                }
            },
            {"Put": pointer},
        ]
        if replacing is not None and replacing.status == "pending":
            items.append(
                {
                    "Update": {
                        "TableName": self.table_name,
                        "Key": {"PK": pk, "SK": {"S": sort_key(replacing.approval_id)}},
                        "UpdateExpression": (
                            "SET #status = :voided, voidedAt = :at, voidReason = :why"
                        ),
                        "ConditionExpression": "#status = :pending",
                        "ExpressionAttributeNames": {"#status": "status"},
                        "ExpressionAttributeValues": {
                            ":voided": {"S": "voided"},
                            ":pending": {"S": "pending"},
                            ":at": {"S": voided_at},
                            ":why": {"S": "superseded"},
                        },
                    }
                }
            )
        try:
            self.client.transact_write_items(TransactItems=items)
        except ClientError as error:
            if _condition_failed(error):
                raise ConflictError(
                    "Another approval was made or used first — review the"
                    " filing set again."
                ) from error
            raise

    def consume(
        self,
        case_id: str,
        approval_id: str,
        *,
        digest: str,
        now: int,
        consumed_at: str,
    ) -> bool:
        try:
            self.client.update_item(
                TableName=self.table_name,
                Key={
                    "PK": {"S": partition_key(case_id)},
                    "SK": {"S": sort_key(approval_id)},
                },
                UpdateExpression="SET #status = :consumed, consumedAt = :at",
                # THE SINGLE USE: still pending, still the digest approved,
                # not yet expired. Of two callers exactly one passes.
                ConditionExpression=(
                    "#status = :pending AND digest = :digest AND expiresAt > :now"
                ),
                ExpressionAttributeNames={"#status": "status"},
                ExpressionAttributeValues={
                    ":consumed": {"S": "consumed"},
                    ":pending": {"S": "pending"},
                    ":digest": {"S": digest},
                    ":now": {"N": str(now)},
                    ":at": {"S": consumed_at},
                },
            )
        except ClientError as error:
            if _condition_failed(error):
                return False
            raise
        return True

    def void(
        self, case_id: str, approval_id: str, *, reason: str, voided_at: str
    ) -> bool:
        try:
            self.client.update_item(
                TableName=self.table_name,
                Key={
                    "PK": {"S": partition_key(case_id)},
                    "SK": {"S": sort_key(approval_id)},
                },
                UpdateExpression=(
                    "SET #status = :voided, voidedAt = :at, voidReason = :why"
                ),
                ConditionExpression="#status = :pending",
                ExpressionAttributeNames={"#status": "status"},
                ExpressionAttributeValues={
                    ":voided": {"S": "voided"},
                    ":pending": {"S": "pending"},
                    ":at": {"S": voided_at},
                    ":why": {"S": reason},
                },
            )
        except ClientError as error:
            if _condition_failed(error):
                return False
            raise
        return True
