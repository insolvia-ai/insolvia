from __future__ import annotations

from typing import Any

import boto3
from botocore.exceptions import ClientError

from insolvia_core.adapters.aws.dynamo import from_attributes, to_attributes
from insolvia_core.fields import timestamp
from insolvia_core.firm_clients import (
    ACTIVE,
    ARCHIVED,
    MERGED_INTO,
    MERGING_FROM,
    MERGING_INTO,
    PROSPECT_STAGE,
    SERVER_OWNED_ATTRIBUTES,
    FirmClient,
    firm_client_from_item,
    firm_client_item,
    sorted_firm_clients,
)
from insolvia_core.firm_clients import SK_PREFIX as FIRM_CLIENT_SK_PREFIX
from insolvia_core.firm_clients import sort_key as firm_client_sort_key
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
_CANCELLED = "TransactionCanceledException"


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

    # ── Firm clients (ADR 0022) ─────────────────────────────────────
    #
    # The library creditors' shape exactly, under `FIRMCLIENT#`. Neither that
    # prefix nor the portal binding's `CLIENT#` begins the other, so neither
    # `begins_with` listing can return the other's rows (`firm_clients` owns
    # why that matters). `client` below is the record; `self.client` is boto3.

    def create_client(self, client: FirmClient) -> None:
        try:
            self.client.put_item(
                TableName=self.table_name,
                Item=to_attributes(firm_client_item(client)),
                ConditionExpression="attribute_not_exists(SK)",
            )
        except ClientError as error:
            if error.response.get("Error", {}).get("Code") == _CONDITION_FAILED:
                raise RuntimeError(f"client {client.id} already exists") from error
            raise

    def get_client(self, firm_id: str, client_id: str) -> FirmClient | None:
        response = self.client.get_item(
            TableName=self.table_name,
            Key={
                "PK": {"S": partition_key(firm_id)},
                "SK": {"S": firm_client_sort_key(client_id)},
            },
            ConsistentRead=True,
        )
        item = response.get("Item")
        return None if not item else firm_client_from_item(from_attributes(item))

    def list_clients(self, firm_id: str) -> tuple[FirmClient, ...]:
        clients: list[FirmClient] = []
        start_key: dict[str, Any] | None = None
        while True:
            kwargs: dict[str, Any] = {
                "TableName": self.table_name,
                "KeyConditionExpression": "PK = :firm AND begins_with(SK, :prefix)",
                "ExpressionAttributeValues": {
                    ":firm": {"S": partition_key(firm_id)},
                    ":prefix": {"S": f"{FIRM_CLIENT_SK_PREFIX}#"},
                },
                "ConsistentRead": True,
            }
            if start_key is not None:
                kwargs["ExclusiveStartKey"] = start_key
            response = self.client.query(**kwargs)
            clients.extend(
                firm_client_from_item(from_attributes(item))
                for item in response.get("Items", [])
            )
            start_key = response.get("LastEvaluatedKey")
            if not start_key:
                break
        return sorted_firm_clients(clients)

    def update_client(self, client: FirmClient) -> FirmClient | None:
        # An UpdateItem that SETs every attribute the record owns and REMOVEs
        # the ones it no longer has — the whole-record save the PutItem this
        # used to be made — but that never names the merge attributes, so a
        # save built from a read taken before a merge claimed the row cannot
        # erase the claim — nor the prospect stage, which has writers of its
        # own. `attribute_not_exists(mergedInto)`: a merged client is
        # terminal (the port says why).
        item = firm_client_item(client)
        owned = {
            key: value
            for key, value in item.items()
            if key not in ("PK", "SK") and key not in SERVER_OWNED_ATTRIBUTES
        }
        names: dict[str, str] = {}
        values: dict[str, Any] = {":firm": {"S": client.firm_id}}
        sets: list[str] = []
        for index, (key, value) in enumerate(owned.items()):
            names[f"#a{index}"] = key
            values[f":a{index}"] = to_attributes({"v": value})["v"]
            sets.append(f"#a{index} = :a{index}")
        expression = "SET " + ", ".join(sets)
        if "taxId" not in item:
            names["#taxId"] = "taxId"
            expression += " REMOVE #taxId"
        names["#merged"] = MERGED_INTO
        try:
            self.client.update_item(
                TableName=self.table_name,
                Key={
                    "PK": {"S": partition_key(client.firm_id)},
                    "SK": {"S": firm_client_sort_key(client.id)},
                },
                UpdateExpression=expression,
                ConditionExpression=(
                    "attribute_exists(SK) AND firmId = :firm"
                    " AND attribute_not_exists(#merged)"
                ),
                ExpressionAttributeNames=names,
                ExpressionAttributeValues=values,
            )
        except ClientError as error:
            if error.response.get("Error", {}).get("Code") == _CONDITION_FAILED:
                return None
            raise
        return client

    # ── The prospect funnel (issue 14.3 / #355) ─────────────────────

    def _conditional_client_update(
        self, firm_id: str, client_id: str, **kwargs: Any
    ) -> FirmClient | None:
        try:
            response = self.client.update_item(
                TableName=self.table_name,
                Key=self._client_key(firm_id, client_id),
                ReturnValues="ALL_NEW",
                **kwargs,
            )
        except ClientError as error:
            if error.response.get("Error", {}).get("Code") == _CONDITION_FAILED:
                return None
            raise
        return firm_client_from_item(from_attributes(response["Attributes"]))

    def set_client_prospect_stage(
        self, firm_id: str, client_id: str, stage: str | None
    ) -> FirmClient | None:
        # ONE UpdateItem, conditioned on the client still being a prospect:
        # `body.first_retained_at` absent. `mark_client_retained` sets that
        # and removes the stage in one write of its own, so the two cannot
        # interleave into a retained client with a funnel position.
        names = {
            "#stage": PROSPECT_STAGE,
            "#updated": "updatedAt",
            "#merged": MERGED_INTO,
            "#body": "body",
            "#retained": "first_retained_at",
        }
        values: dict[str, Any] = {
            ":firm": {"S": firm_id},
            ":now": {"S": timestamp()},
        }
        if stage is None:
            expression = "SET #updated = :now REMOVE #stage"
        else:
            expression = "SET #stage = :stage, #updated = :now"
            values[":stage"] = {"S": stage}
        return self._conditional_client_update(
            firm_id,
            client_id,
            UpdateExpression=expression,
            ConditionExpression=(
                "attribute_exists(SK) AND firmId = :firm"
                " AND attribute_not_exists(#merged)"
                " AND attribute_not_exists(#body.#retained)"
            ),
            ExpressionAttributeNames=names,
            ExpressionAttributeValues=values,
        )

    def mark_client_retained(
        self, firm_id: str, client_id: str, *, retained_on: str
    ) -> FirmClient | None:
        # if_not_exists keeps a date already recorded — by hand, or by an
        # earlier case — and the REMOVE takes the client out of the funnel,
        # in the same write.
        return self._conditional_client_update(
            firm_id,
            client_id,
            UpdateExpression=(
                "SET #body.#retained = if_not_exists(#body.#retained, :date),"
                " #updated = :now REMOVE #stage"
            ),
            ConditionExpression=(
                "attribute_exists(SK) AND firmId = :firm"
                " AND attribute_not_exists(#merged)"
            ),
            ExpressionAttributeNames={
                "#stage": PROSPECT_STAGE,
                "#updated": "updatedAt",
                "#merged": MERGED_INTO,
                "#body": "body",
                "#retained": "first_retained_at",
            },
            ExpressionAttributeValues={
                ":firm": {"S": firm_id},
                ":now": {"S": timestamp()},
                ":date": {"S": retained_on},
            },
        )

    # ── Merging two clients (ADR 0022's PR 7) ───────────────────────
    #
    # Each step is ONE TransactWriteItems over both client rows, so the claim
    # is taken, finished or released on both or on neither. Update actions
    # only — the API's grant on this table already holds UpdateItem, which
    # is what a transactional Update is authorised as.

    def _transact(self, items: list[dict[str, Any]]) -> bool:
        """Run a transaction; False when a condition refused it (or another
        write to either row was in flight — to the caller both mean "not
        now"), raising anything else."""
        try:
            self.client.transact_write_items(TransactItems=items)
        except ClientError as error:
            if error.response.get("Error", {}).get("Code") == _CANCELLED:
                return False
            raise
        return True

    def _client_key(self, firm_id: str, client_id: str) -> dict[str, Any]:
        return {
            "PK": {"S": partition_key(firm_id)},
            "SK": {"S": firm_client_sort_key(client_id)},
        }

    def claim_client_merge(
        self, firm_id: str, *, merged_id: str, survivor_id: str
    ) -> bool:
        if merged_id == survivor_id:
            return False
        names = {
            "#status": "status",
            "#mergedInto": MERGED_INTO,
            "#into": MERGING_INTO,
            "#from": MERGING_FROM,
        }
        active = (
            "attribute_exists(SK) AND firmId = :firm AND #status = :active"
            " AND attribute_not_exists(#mergedInto)"
        )
        return self._transact(
            [
                {
                    "Update": {
                        "TableName": self.table_name,
                        "Key": self._client_key(firm_id, merged_id),
                        "UpdateExpression": "SET #into = :survivor",
                        "ConditionExpression": (
                            f"{active} AND attribute_not_exists(#from)"
                            " AND (attribute_not_exists(#into) OR #into = :survivor)"
                        ),
                        "ExpressionAttributeNames": names,
                        "ExpressionAttributeValues": {
                            ":firm": {"S": firm_id},
                            ":active": {"S": ACTIVE},
                            ":survivor": {"S": survivor_id},
                        },
                    }
                },
                {
                    "Update": {
                        "TableName": self.table_name,
                        "Key": self._client_key(firm_id, survivor_id),
                        "UpdateExpression": "SET #from = :merged",
                        "ConditionExpression": (
                            f"{active} AND attribute_not_exists(#into)"
                            " AND (attribute_not_exists(#from) OR #from = :merged)"
                        ),
                        "ExpressionAttributeNames": names,
                        "ExpressionAttributeValues": {
                            ":firm": {"S": firm_id},
                            ":active": {"S": ACTIVE},
                            ":merged": {"S": merged_id},
                        },
                    }
                },
            ]
        )

    def finish_client_merge(
        self, firm_id: str, *, merged_id: str, survivor_id: str, merged_at: str
    ) -> FirmClient | None:
        finished = self._transact(
            [
                {
                    "Update": {
                        "TableName": self.table_name,
                        "Key": self._client_key(firm_id, merged_id),
                        "UpdateExpression": (
                            "SET #status = :archived, #mergedInto = :survivor,"
                            " updatedAt = :now REMOVE #into"
                        ),
                        "ConditionExpression": "#into = :survivor",
                        "ExpressionAttributeNames": {
                            "#status": "status",
                            "#mergedInto": MERGED_INTO,
                            "#into": MERGING_INTO,
                        },
                        "ExpressionAttributeValues": {
                            ":archived": {"S": ARCHIVED},
                            ":survivor": {"S": survivor_id},
                            ":now": {"S": merged_at},
                        },
                    }
                },
                {
                    "Update": {
                        "TableName": self.table_name,
                        "Key": self._client_key(firm_id, survivor_id),
                        "UpdateExpression": "REMOVE #from",
                        "ConditionExpression": "#from = :merged",
                        "ExpressionAttributeNames": {"#from": MERGING_FROM},
                        "ExpressionAttributeValues": {":merged": {"S": merged_id}},
                    }
                },
            ]
        )
        return self.get_client(firm_id, merged_id) if finished else None

    def release_client_merge(
        self, firm_id: str, *, merged_id: str, survivor_id: str
    ) -> None:
        # Two independent conditional removals rather than one transaction:
        # releasing is best-effort cleanup of THIS pair's claim, and half of
        # it already gone must not stop the other half going.
        for key, attribute, value in (
            (merged_id, MERGING_INTO, survivor_id),
            (survivor_id, MERGING_FROM, merged_id),
        ):
            try:
                self.client.update_item(
                    TableName=self.table_name,
                    Key=self._client_key(firm_id, key),
                    UpdateExpression="REMOVE #claim",
                    ConditionExpression="#claim = :other",
                    ExpressionAttributeNames={"#claim": attribute},
                    ExpressionAttributeValues={":other": {"S": value}},
                )
            except ClientError as error:
                if error.response.get("Error", {}).get("Code") != _CONDITION_FAILED:
                    raise
