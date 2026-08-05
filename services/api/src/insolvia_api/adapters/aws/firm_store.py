from __future__ import annotations

from typing import Any

import boto3
from botocore.exceptions import ClientError

from insolvia_api.core.firms import (
    Firm,
    FirmItemValue,
    FirmUser,
    firm_from_item,
    firm_item,
    firm_user_from_item,
    firm_user_item,
    partition_key,
    subject_key,
    user_sort_key,
)

# The sparse index in infra/modules/firm_store — one entry per firm user,
# keyed by their Cognito subject. See the FirmStore port for why this lookup
# exists at all.
SUBJECT_INDEX = "by-subject"

_CONDITION_FAILED = "ConditionalCheckFailedException"


def _to_attributes(item: dict[str, FirmItemValue]) -> dict[str, Any]:
    """Item shape -> DynamoDB attribute values.

    BOOL IS CHECKED FIRST, and it has to be: in Python `True` is an instance of
    `int`, so an isinstance(int) branch above this one would store `isAdmin` as
    the number 1. It would then come back from `firm_user_from_item` as `1 is
    True` — False — and every admin in the system would quietly stop being one.
    """
    attributes: dict[str, Any] = {}
    for key, value in item.items():
        if isinstance(value, bool):
            attributes[key] = {"BOOL": value}
        elif isinstance(value, dict):
            attributes[key] = {"M": {k: {"S": v} for k, v in value.items()}}
        else:
            attributes[key] = {"S": value}
    return attributes


def _from_attributes(item: dict[str, Any]) -> dict[str, FirmItemValue]:
    plain: dict[str, FirmItemValue] = {}
    for key, value in item.items():
        if "BOOL" in value:
            plain[key] = bool(value["BOOL"])
        elif "M" in value:
            plain[key] = {
                k: v["S"] for k, v in value["M"].items() if isinstance(v.get("S"), str)
            }
        elif "S" in value:
            plain[key] = value["S"]
    return plain


class DynamoDbFirmStore:
    """FirmStore backed by DynamoDB.

    Credentials come from the runtime's default provider chain — the Lambda
    execution role in AWS, or in local dev the short-lived credentials
    scripts/dev-up.sh exports from the developer's AWS profile. There is no
    local emulator: `infra/envs/dev` provisions this machine's real table.
    """

    def __init__(self, table_name: str) -> None:
        self.table_name = table_name
        self.client = boto3.client("dynamodb")

    # ── Firms ───────────────────────────────────────────────────────

    def create_firm(self, firm: Firm) -> None:
        self.client.put_item(
            TableName=self.table_name,
            Item=_to_attributes(firm_item(firm)),
            ConditionExpression="attribute_not_exists(PK)",
        )

    def get_firm(self, firm_id: str) -> Firm | None:
        response = self.client.get_item(
            TableName=self.table_name,
            Key={"PK": {"S": partition_key(firm_id)}, "SK": {"S": "META"}},
            # Strongly consistent, like the case store's reads. A firm created
            # a moment ago must be visible to the request that adds its first
            # admin, and this is the primary key, so consistency is available
            # here in a way it is not on the index below.
            ConsistentRead=True,
        )
        item = response.get("Item")
        return None if not item else firm_from_item(_from_attributes(item))

    # ── Firm users ──────────────────────────────────────────────────

    def add_user(self, user: FirmUser) -> None:
        # attribute_not_exists(SK) rather than (PK): PK is the firm, which
        # exists by the time anyone is added to it, so conditioning on it would
        # refuse every user after the first. SK is the one that is unique per
        # person.
        self.client.put_item(
            TableName=self.table_name,
            Item=_to_attributes(firm_user_item(user)),
            ConditionExpression="attribute_not_exists(SK)",
        )

    def get_user(self, firm_id: str, subject: str) -> FirmUser | None:
        response = self.client.get_item(
            TableName=self.table_name,
            Key={
                "PK": {"S": partition_key(firm_id)},
                "SK": {"S": user_sort_key(subject)},
            },
            ConsistentRead=True,
        )
        item = response.get("Item")
        return None if not item else firm_user_from_item(_from_attributes(item))

    def find_user(self, subject: str) -> FirmUser | None:
        # Limit=2 rather than 1, deliberately: one row is the answer and two is
        # the invariant violation the port requires us to raise on. Asking for
        # one would make a duplicate indistinguishable from the healthy case.
        response = self.client.query(
            TableName=self.table_name,
            IndexName=SUBJECT_INDEX,
            KeyConditionExpression="GSI1PK = :subject",
            ExpressionAttributeValues={":subject": {"S": subject_key(subject)}},
            Limit=2,
        )
        items = response.get("Items", [])
        if not items:
            return None
        if len(items) > 1:
            # RuntimeError, not ValidationError: ValidationError is a 400 in
            # app_factory, and this is not the caller's fault. It is a broken
            # invariant in our own store and it must read as a 500.
            #
            # No subject in the message. This is an error an operator reads in
            # a log line, and a Cognito sub identifies a person.
            raise RuntimeError(
                "a firm user resolves to more than one firm; refusing to guess"
            )
        return firm_user_from_item(_from_attributes(items[0]))

    def list_users(self, firm_id: str) -> tuple[FirmUser, ...]:
        users: list[FirmUser] = []
        start_key: dict[str, Any] | None = None
        while True:
            kwargs: dict[str, Any] = {
                "TableName": self.table_name,
                "KeyConditionExpression": "PK = :firm AND begins_with(SK, :prefix)",
                "ExpressionAttributeValues": {
                    ":firm": {"S": partition_key(firm_id)},
                    # Excludes the firm's own META item, which shares the
                    # partition and is not a user.
                    ":prefix": {"S": "USER#"},
                },
                "ConsistentRead": True,
            }
            if start_key is not None:
                kwargs["ExclusiveStartKey"] = start_key
            response = self.client.query(**kwargs)
            users.extend(
                firm_user_from_item(_from_attributes(item))
                for item in response.get("Items", [])
            )
            # The loop is the port's "all of them" promise kept. A single Query
            # returns at most 1 MB, and a staff list that crossed it would
            # otherwise come back silently short — a firm admin seeing eleven of
            # their twelve colleagues with nothing anywhere saying so.
            start_key = response.get("LastEvaluatedKey")
            if not start_key:
                break
        # Sorted here rather than by the key, because the sort key is
        # USER#<uuid> and orders by nothing a human recognises.
        return tuple(sorted(users, key=lambda user: (user.display_name, user.subject)))

    def update_user(self, user: FirmUser) -> FirmUser | None:
        try:
            self.client.put_item(
                TableName=self.table_name,
                Item=_to_attributes(firm_user_item(user)),
                ConditionExpression="attribute_exists(SK) AND firmId = :firm",
                ExpressionAttributeValues={":firm": {"S": user.firm_id}},
            )
        except ClientError as error:
            if error.response.get("Error", {}).get("Code") == _CONDITION_FAILED:
                return None
            raise
        return user

    def remove_user(self, firm_id: str, subject: str) -> bool:
        try:
            self.client.delete_item(
                TableName=self.table_name,
                Key={
                    "PK": {"S": partition_key(firm_id)},
                    "SK": {"S": user_sort_key(subject)},
                },
                # Without this a delete of a subject that is not there succeeds
                # silently, and two concurrent removals would both report
                # success — the same reason DocumentStore.delete returns a bool.
                ConditionExpression="attribute_exists(SK)",
            )
        except ClientError as error:
            if error.response.get("Error", {}).get("Code") == _CONDITION_FAILED:
                return False
            raise
        return True
