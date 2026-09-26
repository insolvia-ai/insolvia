from __future__ import annotations

from typing import Any

import boto3
from botocore.exceptions import ClientError

from insolvia_core.adapters.aws.dynamo import from_attributes, to_attributes
from insolvia_core.firms import (
    Firm,
    FirmUser,
    firm_from_item,
    firm_item,
    firm_user_from_item,
    firm_user_item,
    partition_key,
    subject_key,
    user_sort_key,
)
from insolvia_core.library_creditors import (
    LibraryCreditor,
    library_creditor_from_item,
    library_creditor_item,
)
from insolvia_core.library_creditors import sort_key as library_creditor_sort_key

# The sparse index in infra/modules/firm_store — one entry per firm user,
# keyed by their Cognito subject. See the FirmStore port for why this lookup
# exists at all.
SUBJECT_INDEX = "by-subject"

_CONDITION_FAILED = "ConditionalCheckFailedException"


# One converter for every row in this table — the shared recursive one.
# This file used to carry a three-branch converter of its own (S, BOOL and a
# one-level M of strings), which was all a firm row held; the firm defaults
# (issue #360) added an integer and two nested maps, and a second converter
# that must agree with `dynamo.py` about a bool is a second converter that
# will eventually disagree. BOOL IS STILL CHECKED BEFORE INT — in the shared
# module, once, for the same reason its docstring gives.
_to_attributes = to_attributes
_from_attributes = from_attributes


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

    def list_firms(self) -> tuple[Firm, ...]:
        # A paginated Scan filtered to META items — the port's docstring owns
        # why a scan and not an index. The filter references SK, which is
        # legal in a Scan FilterExpression (filters cannot appear in a Query's
        # KeyConditionExpression, but a Scan has none). The pagination loop is
        # the same "all of them" promise list_users keeps: a page boundary
        # must not silently truncate the admin portal's firm list.
        firms: list[Firm] = []
        start_key: dict[str, Any] | None = None
        while True:
            kwargs: dict[str, Any] = {
                "TableName": self.table_name,
                "FilterExpression": "SK = :meta",
                "ExpressionAttributeValues": {":meta": {"S": "META"}},
            }
            if start_key is not None:
                kwargs["ExclusiveStartKey"] = start_key
            response = self.client.scan(**kwargs)
            firms.extend(
                firm_from_item(_from_attributes(item))
                for item in response.get("Items", [])
            )
            start_key = response.get("LastEvaluatedKey")
            if not start_key:
                break
        # Sorted here: scan order is partition-hash order, which reads as
        # random to a human working down a list of firms.
        return tuple(sorted(firms, key=lambda firm: (firm.name, firm.id)))

    def update_firm(self, firm: Firm) -> Firm | None:
        try:
            self.client.put_item(
                TableName=self.table_name,
                Item=_to_attributes(firm_item(firm)),
                # Existence only — no scope condition, because the firm IS the
                # scope (see the port). Without this, an update racing a
                # deletion would resurrect the firm from the caller's stale
                # read.
                ConditionExpression="attribute_exists(PK)",
            )
        except ClientError as error:
            if error.response.get("Error", {}).get("Code") == _CONDITION_FAILED:
                return None
            raise
        return firm

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
        #
        # BY SURNAME, which is what a staff list is normally ordered by and is
        # now expressible. `subject` still breaks the tie, so two colleagues who
        # share a name keep a stable order rather than one that varies per query.
        # The memory adapter sorts identically and must not drift from this.
        return tuple(
            sorted(
                users, key=lambda user: (user.last_name, user.first_name, user.subject)
            )
        )

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

    # ── Library creditors ───────────────────────────────────────────
    #
    # These four use the SHARED converter (`adapters.aws.dynamo`) rather than
    # this file's own `_to_attributes`/`_from_attributes` above: a library
    # creditor's `address` and `additionalNoticeParties` are nested maps and
    # lists, which `FirmItemValue` (`str | bool | dict[str, str]`, one level
    # deep) cannot express. Firm and firm-user items stay on the narrower
    # converter unchanged — widening it would be a bigger diff than this
    # feature needs, for rows that will never carry a third level of nesting.

    def create_library_creditor(self, creditor: LibraryCreditor) -> None:
        try:
            self.client.put_item(
                TableName=self.table_name,
                Item=to_attributes(library_creditor_item(creditor)),
                ConditionExpression="attribute_not_exists(SK)",
            )
        except ClientError as error:
            if error.response.get("Error", {}).get("Code") == _CONDITION_FAILED:
                raise RuntimeError(
                    f"library creditor {creditor.id} already exists"
                ) from error
            raise

    def get_library_creditor(
        self, firm_id: str, creditor_id: str
    ) -> LibraryCreditor | None:
        response = self.client.get_item(
            TableName=self.table_name,
            Key={
                "PK": {"S": partition_key(firm_id)},
                "SK": {"S": library_creditor_sort_key(creditor_id)},
            },
            ConsistentRead=True,
        )
        item = response.get("Item")
        return None if not item else library_creditor_from_item(from_attributes(item))

    def list_library_creditors(self, firm_id: str) -> tuple[LibraryCreditor, ...]:
        creditors: list[LibraryCreditor] = []
        start_key: dict[str, Any] | None = None
        while True:
            kwargs: dict[str, Any] = {
                "TableName": self.table_name,
                "KeyConditionExpression": "PK = :firm AND begins_with(SK, :prefix)",
                "ExpressionAttributeValues": {
                    ":firm": {"S": partition_key(firm_id)},
                    ":prefix": {"S": "LIBCREDITOR#"},
                },
                "ConsistentRead": True,
            }
            if start_key is not None:
                kwargs["ExclusiveStartKey"] = start_key
            response = self.client.query(**kwargs)
            creditors.extend(
                library_creditor_from_item(from_attributes(item))
                for item in response.get("Items", [])
            )
            start_key = response.get("LastEvaluatedKey")
            if not start_key:
                break
        return tuple(
            sorted(creditors, key=lambda creditor: (creditor.name, creditor.id))
        )

    def update_library_creditor(
        self, creditor: LibraryCreditor
    ) -> LibraryCreditor | None:
        try:
            self.client.put_item(
                TableName=self.table_name,
                Item=to_attributes(library_creditor_item(creditor)),
                ConditionExpression="attribute_exists(SK) AND firmId = :firm",
                ExpressionAttributeValues={":firm": {"S": creditor.firm_id}},
            )
        except ClientError as error:
            if error.response.get("Error", {}).get("Code") == _CONDITION_FAILED:
                return None
            raise
        return creditor

    def delete_library_creditor(self, firm_id: str, creditor_id: str) -> bool:
        try:
            self.client.delete_item(
                TableName=self.table_name,
                Key={
                    "PK": {"S": partition_key(firm_id)},
                    "SK": {"S": library_creditor_sort_key(creditor_id)},
                },
                ConditionExpression="attribute_exists(SK)",
            )
        except ClientError as error:
            if error.response.get("Error", {}).get("Code") == _CONDITION_FAILED:
                return False
            raise
        return True
