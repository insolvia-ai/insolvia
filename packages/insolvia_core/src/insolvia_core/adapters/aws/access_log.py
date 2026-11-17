from __future__ import annotations

from typing import Any

import boto3

from insolvia_core.access_log import AccessEvent, access_item


class DynamoDbAccessLog:
    """AccessLog backed by DynamoDB PutItem.

    PutItem is the only call this class makes, and the only one its IAM grant
    allows (infra/modules/case_store). There is deliberately no read path on
    either side.
    """

    def __init__(self, table_name: str) -> None:
        self.table_name = table_name
        self.client = boto3.client("dynamodb")

    def record(self, event: AccessEvent) -> None:
        item: dict[str, Any] = {
            key: {"N": str(value)} if isinstance(value, int) else {"S": value}
            for key, value in access_item(event).items()
        }
        self.client.put_item(TableName=self.table_name, Item=item)
