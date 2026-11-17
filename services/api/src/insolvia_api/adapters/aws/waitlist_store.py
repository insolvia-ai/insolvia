from __future__ import annotations

import boto3

from insolvia_api.core.waitlist import WaitlistRecord, record_item


class DynamoDbWaitlistStore:
    """WaitlistStore backed by DynamoDB PutItem.

    Credentials come from the runtime's default provider chain — the Lambda
    execution role in AWS, or in local dev the short-lived credentials
    scripts/dev-up.sh exports from the developer's AWS profile.
    """

    def __init__(self, table_name: str) -> None:
        self.table_name = table_name
        self.client = boto3.client("dynamodb")

    def add(self, record: WaitlistRecord) -> None:
        item = {key: {"S": value} for key, value in record_item(record).items()}
        self.client.put_item(TableName=self.table_name, Item=item)
