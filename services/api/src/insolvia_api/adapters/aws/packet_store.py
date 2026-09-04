from __future__ import annotations

from typing import Any

import boto3
from botocore.exceptions import ClientError

from insolvia_api.adapters.aws.dynamo import from_attributes, to_attributes
from insolvia_api.core.cases import Case, case_item, partition_key
from insolvia_api.core.packets import (
    Packet,
    list_order,
    packet_from_item,
    packet_item,
    sort_key,
)


class DynamoDbPacketStore:
    """PacketStore backed by DynamoDB — same table, same partition as every
    case child item. Composed by the pipeline WORKER (create) under its own
    role's grant (infra/modules/case_store, worker_role_name) and by the API
    (get/list) under the API role's."""

    def __init__(self, table_name: str) -> None:
        self.table_name = table_name
        self.client = boto3.client("dynamodb")

    def create(
        self, packet: Packet, *, pinned_case: Case, expected_updated_at: str
    ) -> bool:
        """ONE TRANSACTION, TWO ITEMS — the packet record and the pinned case
        (core/ports.PacketStore owns the argument). The case put carries the
        whole condition: the row must still exist, must not have moved since
        the worker read it, and must not be `filed` — a filed case never
        re-resolves (effective-dating.md), and the status check closes the
        window where filing lands mid-assembly.
        """
        try:
            self.client.transact_write_items(
                TransactItems=[
                    {
                        "Put": {
                            "TableName": self.table_name,
                            "Item": to_attributes(packet_item(packet)),
                            # Ids are server-minted uuid4s; an existing SK
                            # means the minting broke.
                            "ConditionExpression": "attribute_not_exists(SK)",
                        }
                    },
                    {
                        "Put": {
                            "TableName": self.table_name,
                            "Item": to_attributes(case_item(pinned_case)),
                            "ConditionExpression": (
                                "attribute_exists(PK)"
                                " AND updatedAt = :read_at"
                                " AND #status <> :filed"
                            ),
                            "ExpressionAttributeNames": {"#status": "status"},
                            "ExpressionAttributeValues": {
                                ":read_at": {"S": expected_updated_at},
                                ":filed": {"S": "filed"},
                            },
                        }
                    },
                ]
            )
        except ClientError as error:
            if (
                error.response.get("Error", {}).get("Code")
                == "TransactionCanceledException"
            ):
                return False
            raise
        return True

    def get(self, case_id: str, packet_id: str) -> Packet | None:
        response = self.client.get_item(
            TableName=self.table_name,
            Key={
                "PK": {"S": partition_key(case_id)},
                "SK": {"S": sort_key(packet_id)},
            },
            # Strongly consistent: the client reaches for the download URL
            # the moment the job result names the packet, possibly milliseconds
            # after the worker's write.
            ConsistentRead=True,
        )
        item = response.get("Item")
        if not item:
            return None
        return packet_from_item(from_attributes(item))

    def list_for_case(self, case_id: str) -> tuple[Packet, ...]:
        packets: list[Packet] = []
        exclusive_start: dict[str, Any] | None = None
        while True:
            kwargs: dict[str, Any] = {
                "TableName": self.table_name,
                "KeyConditionExpression": "PK = :case AND begins_with(SK, :prefix)",
                "ExpressionAttributeValues": {
                    ":case": {"S": partition_key(case_id)},
                    ":prefix": {"S": "PACKET#"},
                },
                "ConsistentRead": True,
            }
            if exclusive_start is not None:
                kwargs["ExclusiveStartKey"] = exclusive_start
            response = self.client.query(**kwargs)
            packets.extend(
                packet_from_item(from_attributes(item))
                for item in response.get("Items", [])
            )
            exclusive_start = response.get("LastEvaluatedKey")
            if not exclusive_start:
                break
        # Newest first — the SK embeds a random uuid, so the query's own
        # order carries no meaning; list_order is the shared definition.
        return tuple(sorted(packets, key=list_order, reverse=True))
