from __future__ import annotations

import boto3

from insolvia_api.core.waitlist import WaitlistRecord, record_item


class DynamoDbWaitlistStore:
    """WaitlistStore backed by DynamoDB PutItem.

    Credentials come from the runtime's default provider chain — the Lambda
    execution role in AWS, the local AWS profile or docker-compose dummy
    credentials in dev. endpoint_url is the local-only dynamodb-local
    override (config rejects it outside INSOLVIA_ENV=local).
    """

    def __init__(self, table_name: str, *, endpoint_url: str | None = None) -> None:
        self.table_name = table_name
        self.client = boto3.client("dynamodb", endpoint_url=endpoint_url)

    def add(self, record: WaitlistRecord) -> None:
        item = {key: {"S": value} for key, value in record_item(record).items()}
        self.client.put_item(TableName=self.table_name, Item=item)
