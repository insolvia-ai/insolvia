from __future__ import annotations

from typing import Any, Final

import boto3
from botocore.exceptions import ClientError

from insolvia_core.adapters.aws.dynamo import from_attributes, to_attributes
from insolvia_core.adapters.aws.firm_store import client_linkable_check
from insolvia_core.cases import INDEX_BY_CLIENT, client_key, partition_key
from insolvia_core.debtors import (
    FILING_ROLES,
    Debtor,
    LinkOutcome,
    RepointOutcome,
    debtor_from_item,
    debtor_item,
    role_order,
    sort_key,
)
from insolvia_core.errors import ConflictError
from insolvia_core.fields import timestamp

# Derived from the one function that builds debtor sort keys, so the two cannot
# drift. This prefix is what makes list_for_case safe to run against a case's
# whole partition: the case root lives there too, under SK=META, and a bare
# `PK = :case` would hand that row to debtor_from_item to parse as a debtor.
DEBTOR_PREFIX: Final = sort_key("")

_CANCELLED: Final = "TransactionCanceledException"
_CONDITION_FAILED: Final = "ConditionalCheckFailed"


class DynamoDbDebtorStore:
    """DebtorStore backed by DynamoDB.

    The same table and the same partition as DynamoDbCaseStore — a debtor is a
    child item of its case, not a row in a table of its own — so credentials,
    the absence of a local emulator, and the per-machine dev table all work
    exactly as they do there. Both key halves come from the functions that own
    them (`partition_key` for the case's PK, `sort_key` for the role's SK),
    which is the same pair `debtor_item` writes.

    `firm_table_name` is the FIRM table, which `link` conditions on: the
    client row it links must still be linkable when the write lands
    (`firm_store.client_linkable_check`). A composition that never links —
    the workers, the MCP service, the filing worker — leaves it None, and
    `link` then refuses to run rather than writing an unconditioned link.
    """

    def __init__(self, table_name: str, *, firm_table_name: str | None = None) -> None:
        self.table_name = table_name
        self.firm_table_name = firm_table_name
        self.client = boto3.client("dynamodb")

    def create(self, debtor: Debtor) -> bool:
        # The one place a condition is load-bearing. Everything else about
        # this store replaces outright, but a first save mints an id that the
        # route returns to the client, so two overlapping first saves must not
        # both succeed — the loser's id would already be in a client's hands.
        try:
            self.client.put_item(
                TableName=self.table_name,
                Item=to_attributes(debtor_item(debtor)),
                ConditionExpression="attribute_not_exists(SK)",
            )
        except ClientError as error:
            if (
                error.response.get("Error", {}).get("Code")
                == "ConditionalCheckFailedException"
            ):
                return False
            raise
        return True

    def put(self, debtor: Debtor) -> None:
        # Unconditional, and correct because this path only ever runs after a
        # read found an existing record whose id is being kept. Replacing is
        # the point: the questionnaire PUTs the whole record on every autosave
        # (core/debtors.parse_debtor says why it must be whole).
        self.client.put_item(
            TableName=self.table_name,
            Item=to_attributes(debtor_item(debtor)),
        )

    def link(self, debtor: Debtor, *, create: bool, firm_id: str) -> LinkOutcome:
        """One transaction: the Put of this role, plus a ConditionCheck on
        each OTHER role's item that it does not name the same client, plus
        one on the CLIENT'S OWN ROW in the firm table (cross-table, which a
        transaction allows) that it is still linkable — last, so its
        cancellation reason is the last one.

        FILING_ROLES caps a case at three debtor items, so "every other
        role" is two fixed keys — no query, and no lock item to keep in step
        with the debtor. A ConditionCheck on an item that does not exist
        evaluates against an empty item, where `attribute_not_exists` holds,
        so an empty role passes. Two racing links of one client to two roles
        conflict on each other's items: DynamoDB serialises them, and the
        second fails its check (or is cancelled as a conflict, which is
        answered as one). A merge's claim on the client and this link
        conflict on the client's row the same way: whichever lands second
        is refused."""
        if debtor.client_id is None:
            raise ValueError("link needs a debtor that names a client")
        firm_table = self.firm_table_name
        if firm_table is None:
            raise RuntimeError(
                "link needs the firm table: a link must be conditional on the "
                "client row"
            )
        put: dict[str, Any] = {
            "TableName": self.table_name,
            "Item": to_attributes(debtor_item(debtor)),
        }
        if create:
            put["ConditionExpression"] = "attribute_not_exists(SK)"
        items: list[dict[str, Any]] = [{"Put": put}]
        for role in FILING_ROLES:
            if role == debtor.filing_role:
                continue
            items.append(
                {
                    "ConditionCheck": {
                        "TableName": self.table_name,
                        "Key": {
                            "PK": {"S": partition_key(debtor.case_id)},
                            "SK": {"S": sort_key(role)},
                        },
                        "ConditionExpression": (
                            "attribute_not_exists(clientId) OR clientId <> :client"
                        ),
                        "ExpressionAttributeValues": {
                            ":client": {"S": debtor.client_id}
                        },
                    }
                }
            )
        items.append(
            client_linkable_check(
                firm_table, firm_id=firm_id, client_id=debtor.client_id
            )
        )
        try:
            self.client.transact_write_items(TransactItems=items)
        except ClientError as error:
            if error.response.get("Error", {}).get("Code") != _CANCELLED:
                raise
            reasons = error.response.get("CancellationReasons") or []
            codes = [reason.get("Code") for reason in reasons]
            if codes and codes[0] == _CONDITION_FAILED:
                return "role_taken"
            if _CONDITION_FAILED in codes[1:-1]:
                return "client_taken"
            if len(codes) == len(items) and codes[-1] == _CONDITION_FAILED:
                return "client_unavailable"
            # A TransactionConflict: another write to one of these items was
            # in flight. Refused as "someone else is linking" rather than
            # retried — the caller reloads and sees who won.
            raise ConflictError(
                "this case's debtors changed while the client was linked; "
                "reload and try again"
            ) from error
        return "written"

    def roles_for_client(self, client_id: str) -> tuple[tuple[str, str], ...]:
        # The `by-client` index, every page, no accessor filter — the port
        # says who may call this. Keys only are read back; the index projects
        # the whole debtor, but `repoint_client` conditions on the item
        # itself, so nothing here is trusted beyond "look at this role".
        found: list[tuple[str, str]] = []
        start_key: dict[str, Any] | None = None
        while True:
            kwargs: dict[str, Any] = {
                "TableName": self.table_name,
                "IndexName": INDEX_BY_CLIENT,
                "KeyConditionExpression": "GSI3PK = :client",
                "ExpressionAttributeValues": {":client": {"S": client_key(client_id)}},
                "ProjectionExpression": "caseId, filingRole",
            }
            if start_key is not None:
                kwargs["ExclusiveStartKey"] = start_key
            response = self.client.query(**kwargs)
            for raw in response.get("Items", []):
                plain = from_attributes(raw)
                found.append((str(plain["caseId"]), str(plain["filingRole"])))
            start_key = response.get("LastEvaluatedKey")
            if not start_key:
                break
        return tuple(sorted(found))

    def repoint_client(
        self,
        case_id: str,
        filing_role: str,
        *,
        from_client_id: str,
        to_client_id: str,
    ) -> RepointOutcome:
        """One transaction: an UpdateItem of this role that moves `clientId`
        and `GSI3PK` — never `body` or `provenance`, so the copied identity
        cannot change by construction — conditional on the role still naming
        the merged client; plus `link`'s ConditionCheck on each other role
        that it does not already name the survivor."""
        items: list[dict[str, Any]] = [
            {
                "Update": {
                    "TableName": self.table_name,
                    "Key": {
                        "PK": {"S": partition_key(case_id)},
                        "SK": {"S": sort_key(filing_role)},
                    },
                    "UpdateExpression": (
                        "SET clientId = :to, GSI3PK = :index, updatedAt = :now"
                    ),
                    "ConditionExpression": "clientId = :from",
                    "ExpressionAttributeValues": {
                        ":to": {"S": to_client_id},
                        ":from": {"S": from_client_id},
                        ":index": {"S": client_key(to_client_id)},
                        ":now": {"S": timestamp()},
                    },
                }
            }
        ]
        for role in FILING_ROLES:
            if role == filing_role:
                continue
            items.append(
                {
                    "ConditionCheck": {
                        "TableName": self.table_name,
                        "Key": {
                            "PK": {"S": partition_key(case_id)},
                            "SK": {"S": sort_key(role)},
                        },
                        "ConditionExpression": (
                            "attribute_not_exists(clientId) OR clientId <> :client"
                        ),
                        "ExpressionAttributeValues": {":client": {"S": to_client_id}},
                    }
                }
            )
        try:
            self.client.transact_write_items(TransactItems=items)
        except ClientError as error:
            if error.response.get("Error", {}).get("Code") != _CANCELLED:
                raise
            reasons = error.response.get("CancellationReasons") or []
            codes = [reason.get("Code") for reason in reasons]
            if codes and codes[0] == _CONDITION_FAILED:
                return "absent"
            if _CONDITION_FAILED in codes[1:]:
                return "client_taken"
            raise ConflictError(
                "a case's debtors changed while the clients were being merged; "
                "run the merge again to finish it"
            ) from error
        return "written"

    def get(self, case_id: str, *, filing_role: str) -> Debtor | None:
        response = self.client.get_item(
            TableName=self.table_name,
            Key={
                "PK": {"S": partition_key(case_id)},
                "SK": {"S": sort_key(filing_role)},
            },
            # Strongly consistent because the caller has very likely just
            # written this record: intake autosaves and then reads back, and an
            # eventually consistent read would show the previous answer — which
            # on a form looks exactly like the save having been lost.
            ConsistentRead=True,
        )
        item = response.get("Item")
        if not item:
            return None
        return debtor_from_item(from_attributes(item))

    def list_for_case(self, case_id: str) -> tuple[Debtor, ...]:
        response = self.client.query(
            TableName=self.table_name,
            KeyConditionExpression="PK = :case AND begins_with(SK, :prefix)",
            ExpressionAttributeValues={
                ":case": {"S": partition_key(case_id)},
                ":prefix": {"S": DEBTOR_PREFIX},
            },
            ConsistentRead=True,
        )
        # No pagination, and that is a property of the schema rather than an
        # omission: FILING_ROLES caps a case at three debtor items, so this
        # query cannot approach the 1 MB page limit.
        #
        # Sorted explicitly rather than trusting the sort-key order. DynamoDB
        # returns a query alphabetically by SK, which matches FILING_ROLES
        # only because today's three names happen to fall that way — see
        # core/debtors.role_order for what that coincidence would cost.
        return tuple(
            sorted(
                (
                    debtor_from_item(from_attributes(item))
                    for item in response.get("Items", [])
                ),
                key=lambda debtor: role_order(debtor.filing_role),
            )
        )
