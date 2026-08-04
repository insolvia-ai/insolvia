from __future__ import annotations

from typing import Any

import boto3
from botocore.exceptions import ClientError

from insolvia_api.core.cases import (
    Case,
    CasePage,
    case_from_item,
    case_item,
    decode_cursor,
    encode_cursor,
    owner_key,
    partition_key,
)

# The sparse index in infra/modules/case_store — one entry per case, keyed by
# owner and sorted by creation time.
OWNER_INDEX = "by-owner"


def _to_attributes(item: dict[str, str | int]) -> dict[str, Any]:
    return {
        key: {"N": str(value)} if isinstance(value, int) else {"S": value}
        for key, value in item.items()
    }


def _from_attributes(item: dict[str, Any]) -> dict[str, str | int]:
    plain: dict[str, str | int] = {}
    for key, value in item.items():
        if "N" in value:
            plain[key] = int(value["N"])
        elif "S" in value:
            plain[key] = value["S"]
    return plain


class DynamoDbCaseStore:
    """CaseStore backed by DynamoDB.

    Credentials come from the runtime's default provider chain — the Lambda
    execution role in AWS, or in local dev the short-lived credentials
    scripts/dev-up.sh exports from the developer's AWS profile. There is no
    local emulator: `infra/envs/dev` provisions this machine's real table.
    """

    def __init__(self, table_name: str) -> None:
        self.table_name = table_name
        self.client = boto3.client("dynamodb")

    def create(self, case: Case) -> None:
        # attribute_not_exists(PK) makes the write fail rather than silently
        # overwrite if a uuid4 ever collided, or if a retry replayed a create.
        self.client.put_item(
            TableName=self.table_name,
            Item=_to_attributes(case_item(case)),
            ConditionExpression="attribute_not_exists(PK)",
        )

    def get(self, case_id: str, *, owner_principal: str) -> Case | None:
        response = self.client.get_item(
            TableName=self.table_name,
            Key={"PK": {"S": partition_key(case_id)}, "SK": {"S": "META"}},
            ConsistentRead=True,
        )
        item = response.get("Item")
        if not item:
            return None
        case = case_from_item(_from_attributes(item))
        # Ownership is checked here, not only in the route. A case belonging to
        # someone else is indistinguishable from one that does not exist.
        if case.owner_principal != owner_principal:
            return None
        return case

    def list_for_owner(
        self, owner_principal: str, *, limit: int, cursor: str | None
    ) -> CasePage:
        kwargs: dict[str, Any] = {
            "TableName": self.table_name,
            "IndexName": OWNER_INDEX,
            "KeyConditionExpression": "GSI1PK = :owner",
            "ExpressionAttributeValues": {":owner": {"S": owner_key(owner_principal)}},
            # Newest first: GSI1SK is <createdAt>#<id>, so descending order is
            # reverse-chronological without sorting anything in the service.
            "ScanIndexForward": False,
            "Limit": limit,
        }
        if cursor is not None:
            kwargs["ExclusiveStartKey"] = {
                key: {"S": value} for key, value in decode_cursor(cursor).items()
            }

        response = self.client.query(**kwargs)
        cases = tuple(
            case_from_item(_from_attributes(item)) for item in response.get("Items", [])
        )
        last_key = response.get("LastEvaluatedKey")
        next_cursor = (
            encode_cursor({key: value["S"] for key, value in last_key.items()})
            if last_key
            else None
        )
        return CasePage(cases=cases, next_cursor=next_cursor)

    def update(self, case: Case) -> Case | None:
        try:
            self.client.put_item(
                TableName=self.table_name,
                Item=_to_attributes(case_item(case)),
                # Both halves matter. attribute_exists rejects an update to a
                # case that has since been deleted; the owner check closes the
                # window between the route's read and this write, so a case
                # cannot be reassigned out from under a caller mid-request.
                ConditionExpression=(
                    "attribute_exists(PK) AND ownerPrincipal = :owner"
                ),
                ExpressionAttributeValues={":owner": {"S": case.owner_principal}},
            )
        except ClientError as error:
            if (
                error.response.get("Error", {}).get("Code")
                == "ConditionalCheckFailedException"
            ):
                return None
            raise
        return case
