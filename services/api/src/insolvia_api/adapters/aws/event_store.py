from __future__ import annotations

from typing import Any

import boto3
from botocore.exceptions import ClientError
from insolvia_core.adapters.aws.dynamo import from_attributes, to_attributes

from insolvia_api.core.calendar_feed import (
    CalendarToken,
    token_from_item,
    token_item,
)
from insolvia_api.core.events import (
    Event,
    EventScope,
    calendar_key,
    event_from_item,
    event_item,
    list_order,
    partition_key,
    sort_key,
)

# The case table's first index — created for the by-firm case listing
# (infra/modules/case_store) and shared here under a DIFFERENT partition
# key prefix, FIRMCAL#, so neither query sees the other's rows. No new
# index, no Terraform: the grant already covers every index of the table.
_CALENDAR_INDEX = "by-firm"


class DynamoDbEventStore:
    """EventStore backed by DynamoDB — the case table, no table of its own.

    A case event is a child item of its case (SK = EVENT#<id>); a firm
    event sits under PK = FIRM#<firm_id> in the same table. Both carry the
    calendar's GSI1 keys, which is what makes a firm's month one query.
    """

    def __init__(self, table_name: str) -> None:
        self.table_name = table_name
        self.client = boto3.client("dynamodb")

    def create(self, event: Event) -> None:
        try:
            self.client.put_item(
                TableName=self.table_name,
                Item=to_attributes(event_item(event)),
                ConditionExpression="attribute_not_exists(SK)",
            )
        except ClientError as error:
            if (
                error.response.get("Error", {}).get("Code")
                == "ConditionalCheckFailedException"
            ):
                raise RuntimeError("event id already exists in this scope") from error
            raise

    def get(self, scope: EventScope, event_id: str) -> Event | None:
        response = self.client.get_item(
            TableName=self.table_name,
            Key={
                "PK": {"S": partition_key(scope)},
                "SK": {"S": sort_key(event_id)},
            },
            ConsistentRead=True,
        )
        item = response.get("Item")
        if not item:
            return None
        return event_from_item(from_attributes(item))

    def put(self, event: Event) -> bool:
        try:
            self.client.put_item(
                TableName=self.table_name,
                Item=to_attributes(event_item(event)),
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

    def delete(self, scope: EventScope, event_id: str) -> bool:
        response = self.client.delete_item(
            TableName=self.table_name,
            Key={
                "PK": {"S": partition_key(scope)},
                "SK": {"S": sort_key(event_id)},
            },
            ReturnValues="ALL_OLD",
        )
        return bool(response.get("Attributes"))

    def _query_all(self, **kwargs: Any) -> list[Event]:
        events: list[Event] = []
        exclusive_start: dict[str, Any] | None = None
        while True:
            page = dict(kwargs)
            if exclusive_start is not None:
                page["ExclusiveStartKey"] = exclusive_start
            response = self.client.query(**page)
            events.extend(
                event_from_item(from_attributes(item))
                for item in response.get("Items", [])
            )
            exclusive_start = response.get("LastEvaluatedKey")
            if not exclusive_start:
                break
        return events

    def list_for_scope(self, scope: EventScope) -> tuple[Event, ...]:
        events = self._query_all(
            TableName=self.table_name,
            KeyConditionExpression="PK = :scope AND begins_with(SK, :prefix)",
            ExpressionAttributeValues={
                ":scope": {"S": partition_key(scope)},
                ":prefix": {"S": "EVENT#"},
            },
            ConsistentRead=True,
        )
        return tuple(sorted(events, key=list_order))

    def list_for_firm(
        self, firm_id: str, *, starting_from: str, until: str
    ) -> tuple[Event, ...]:
        # GSI1SK is "<instant>#<id>". The lower bound is the bare instant
        # (which sorts before any "<instant>#…"); the upper bound appends
        # "~", which sorts after "#" and every digit, so an event AT `until`
        # is included and one a second later is not. Eventually consistent,
        # as every GSI read is — a calendar a second behind is fine.
        events = self._query_all(
            TableName=self.table_name,
            IndexName=_CALENDAR_INDEX,
            KeyConditionExpression="GSI1PK = :firm AND GSI1SK BETWEEN :lo AND :hi",
            ExpressionAttributeValues={
                ":firm": {"S": calendar_key(firm_id)},
                ":lo": {"S": starting_from},
                ":hi": {"S": f"{until}~"},
            },
        )
        return tuple(sorted(events, key=list_order))


class DynamoDbCalendarTokenStore:
    """CalendarTokenStore in the case table: PK FIRM#<firm_id>, SK
    CALTOKEN#<subject> — beside the firm's own events, under the same grant."""

    def __init__(self, table_name: str) -> None:
        self.table_name = table_name
        self.client = boto3.client("dynamodb")

    def put(self, token: CalendarToken) -> None:
        self.client.put_item(
            TableName=self.table_name, Item=to_attributes(token_item(token))
        )

    def get(self, firm_id: str, subject: str) -> CalendarToken | None:
        response = self.client.get_item(
            TableName=self.table_name,
            Key={
                "PK": {"S": f"FIRM#{firm_id}"},
                "SK": {"S": f"CALTOKEN#{subject}"},
            },
            ConsistentRead=True,
        )
        item = response.get("Item")
        if not item:
            return None
        return token_from_item(from_attributes(item))

    def delete(self, firm_id: str, subject: str) -> bool:
        response = self.client.delete_item(
            TableName=self.table_name,
            Key={
                "PK": {"S": f"FIRM#{firm_id}"},
                "SK": {"S": f"CALTOKEN#{subject}"},
            },
            ReturnValues="ALL_OLD",
        )
        return bool(response.get("Attributes"))
