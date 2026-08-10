from __future__ import annotations

import boto3

from insolvia_admin.core.audit import AdminEvent, event_item


class DynamoDbAuditLog:
    """AuditLog backed by the append-only admin audit table.

    A plain unconditional PutItem: the sort key ends in a fresh uuid, so a
    collision is not a state this can reach, and a condition would imply an
    overwrite semantics this table must never have. The role's grant is
    PutItem alone — a read here would fail at IAM, which is the point.
    """

    def __init__(self, table_name: str) -> None:
        self.table_name = table_name
        self.client = boto3.client("dynamodb")

    def record(self, event: AdminEvent) -> None:
        self.client.put_item(
            TableName=self.table_name,
            Item={key: {"S": value} for key, value in event_item(event).items()},
        )
