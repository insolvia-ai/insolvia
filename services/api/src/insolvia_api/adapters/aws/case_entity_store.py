from __future__ import annotations

from typing import Any

import boto3
from botocore.exceptions import ClientError

from insolvia_api.adapters.aws.dynamo import from_attributes, to_attributes
from insolvia_api.core.case_entities import (
    CaseEntity,
    EntityKind,
    entity_from_item,
    entity_item,
    list_order,
    sort_key,
)
from insolvia_api.core.cases import partition_key


class DynamoDbCaseEntityStore:
    """CaseEntityStore backed by DynamoDB.

    The same table and the same partition as the case root — an entity is a
    child item of its case (SK = <PREFIX>#<id>), not a row in a table of its
    own — so credentials, the absence of a local emulator, and the per-machine
    dev table all work exactly as they do for DynamoDbDebtorStore. Both key
    halves come from the functions that own them (`partition_key`,
    `sort_key`), which is the same pair `entity_item` writes.
    """

    def __init__(self, table_name: str) -> None:
        self.table_name = table_name
        self.client = boto3.client("dynamodb")

    def create(self, entity: CaseEntity[Any]) -> None:
        # Ids are server-minted uuid4s, so this condition should never fire —
        # which is exactly why it raises rather than returning False: a
        # collision here means the minting is broken, and silently replacing
        # would erase a record to hide that.
        try:
            self.client.put_item(
                TableName=self.table_name,
                Item=to_attributes(entity_item(entity)),
                ConditionExpression="attribute_not_exists(SK)",
            )
        except ClientError as error:
            if (
                error.response.get("Error", {}).get("Code")
                == "ConditionalCheckFailedException"
            ):
                raise RuntimeError("entity id already exists in this case") from error
            raise

    def get(
        self, case_id: str, kind: EntityKind[Any], entity_id: str
    ) -> CaseEntity[Any] | None:
        response = self.client.get_item(
            TableName=self.table_name,
            Key={
                "PK": {"S": partition_key(case_id)},
                "SK": {"S": sort_key(kind, entity_id)},
            },
            # Strongly consistent because the caller has very likely just
            # written this record: intake saves and then reads back, and an
            # eventually consistent read would show the previous answer —
            # which on a form looks exactly like the save having been lost.
            ConsistentRead=True,
        )
        item = response.get("Item")
        if not item:
            return None
        return entity_from_item(kind, from_attributes(item))

    def put(self, entity: CaseEntity[Any]) -> bool:
        # Conditional on the row still existing, per the Protocol: an edit
        # racing a delete must not resurrect the record.
        try:
            self.client.put_item(
                TableName=self.table_name,
                Item=to_attributes(entity_item(entity)),
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

    def delete(self, case_id: str, kind: EntityKind[Any], entity_id: str) -> bool:
        # ReturnValues is what makes this a true "did THIS call remove it":
        # two concurrent deletes cannot both see the old item.
        response = self.client.delete_item(
            TableName=self.table_name,
            Key={
                "PK": {"S": partition_key(case_id)},
                "SK": {"S": sort_key(kind, entity_id)},
            },
            ReturnValues="ALL_OLD",
        )
        return bool(response.get("Attributes"))

    def list_for_case(
        self, case_id: str, kind: EntityKind[Any]
    ) -> tuple[CaseEntity[Any], ...]:
        # Paginated INTERNALLY, unlike the debtor query, because nothing caps
        # this collection at three items — a Chapter 7 with hundreds of
        # creditors is a normal case, and the port promises all of them.
        items: list[CaseEntity[Any]] = []
        exclusive_start: dict[str, Any] | None = None
        while True:
            kwargs: dict[str, Any] = {
                "TableName": self.table_name,
                "KeyConditionExpression": "PK = :case AND begins_with(SK, :prefix)",
                "ExpressionAttributeValues": {
                    ":case": {"S": partition_key(case_id)},
                    ":prefix": {"S": f"{kind.sk_prefix}#"},
                },
                "ConsistentRead": True,
            }
            if exclusive_start is not None:
                kwargs["ExclusiveStartKey"] = exclusive_start
            response = self.client.query(**kwargs)
            items.extend(
                entity_from_item(kind, from_attributes(item))
                for item in response.get("Items", [])
            )
            exclusive_start = response.get("LastEvaluatedKey")
            if not exclusive_start:
                break
        # Sorted explicitly rather than trusting the sort-key order: the SK
        # embeds a random uuid, which orders items by coin flip. list_order is
        # the one definition, shared with the memory store.
        return tuple(sorted(items, key=list_order))
