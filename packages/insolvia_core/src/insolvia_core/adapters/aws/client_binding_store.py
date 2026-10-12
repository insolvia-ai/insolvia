from __future__ import annotations

from typing import Any

import boto3
from botocore.exceptions import ClientError

from insolvia_core.adapters.aws.dynamo import from_attributes, to_attributes
from insolvia_core.cases import partition_key as case_partition_key
from insolvia_core.clients import (
    ACTIVE,
    INVITED,
    REVOKED,
    ClientBinding,
    binding_from_item,
    binding_item,
    client_sort_key,
    client_subject_key,
    mirror_item,
    role_claim_item,
    role_claim_sort_key,
)
from insolvia_core.errors import ConflictError
from insolvia_core.firms import partition_key as firm_partition_key

# The firm table's sparse by-subject index (infra/modules/firm_store) — the
# same index a firm user resolves through, under a CLIENT# key.
SUBJECT_INDEX = "by-subject"

_CANCELLED = "TransactionCanceledException"


class DynamoDbClientBindingStore:
    """ClientBindingStore over the firm table and the case table.

    ONE TransactWriteItems SPANS BOTH TABLES — the only store in this package
    that does, and the reason it is its own adapter rather than a method on
    either table's store. DynamoDB transactions may span tables in one
    account and region; IAM authorises each item by its own action (PutItem,
    DeleteItem) on its own table, all of which the API role already holds on
    both (infra/modules/firm_store, infra/modules/case_store), as does the
    staging seed role for the Put-only shape `bind` uses.

    Credentials come from the runtime's default provider chain, as every
    store here.
    """

    def __init__(self, firm_table_name: str, case_table_name: str) -> None:
        self.firm_table = firm_table_name
        self.case_table = case_table_name
        self.client = boto3.client("dynamodb")

    # ── writes ──────────────────────────────────────────────────────

    def bind(
        self, binding: ClientBinding, *, narrowed: tuple[ClientBinding, ...]
    ) -> None:
        not_live = "attribute_not_exists(SK) OR #status = :revoked"
        items: list[dict[str, Any]] = [
            {
                "Put": {
                    "TableName": self.firm_table,
                    "Item": to_attributes(binding_item(binding)),
                    # One live binding per subject per firm: a revoked row may
                    # be re-bound (a refiled case), a live one may not.
                    "ConditionExpression": not_live,
                    "ExpressionAttributeNames": {"#status": "status"},
                    "ExpressionAttributeValues": {":revoked": {"S": REVOKED}},
                }
            },
            {
                "Put": {
                    "TableName": self.case_table,
                    "Item": to_attributes(mirror_item(binding)),
                    "ConditionExpression": not_live,
                    "ExpressionAttributeNames": {"#status": "status"},
                    "ExpressionAttributeValues": {":revoked": {"S": REVOKED}},
                }
            },
        ]
        # The per-role rule, made structural: each claim may be taken only if
        # nobody holds it, or its holder is the new subject or one this same
        # transaction narrows.
        holders = [binding.subject, *(other.subject for other in narrowed)]
        placeholders = {f":h{index}": {"S": s} for index, s in enumerate(holders)}
        for role in binding.roles:
            items.append(
                {
                    "Put": {
                        "TableName": self.case_table,
                        "Item": to_attributes(
                            role_claim_item(binding.case_id, role, binding.subject)
                        ),
                        "ConditionExpression": (
                            "attribute_not_exists(SK) OR #subject IN ("
                            + ", ".join(placeholders)
                            + ")"
                        ),
                        "ExpressionAttributeNames": {"#subject": "subject"},
                        "ExpressionAttributeValues": placeholders,
                    }
                }
            )
        for other in narrowed:
            items.append(
                {
                    "Put": {
                        "TableName": self.firm_table,
                        "Item": to_attributes(binding_item(other)),
                        # Still live, or the narrowing is stale.
                        "ConditionExpression": "#status IN (:invited, :active)",
                        "ExpressionAttributeNames": {"#status": "status"},
                        "ExpressionAttributeValues": {
                            ":invited": {"S": INVITED},
                            ":active": {"S": ACTIVE},
                        },
                    }
                }
            )
            items.append(
                {
                    "Put": {
                        "TableName": self.case_table,
                        "Item": to_attributes(mirror_item(other)),
                        "ConditionExpression": "attribute_exists(SK)",
                    }
                }
            )
        try:
            self.client.transact_write_items(TransactItems=items)
        except ClientError as error:
            if error.response.get("Error", {}).get("Code") == _CANCELLED:
                # No subject, no email, no role in the message: the caller
                # learns the state refused the write, which is all it can act on.
                raise ConflictError(
                    "another invitation on this case changed while this one was "
                    "written; reload and try again"
                ) from error
            raise

    def update(self, binding: ClientBinding) -> ClientBinding | None:
        items: list[dict[str, Any]] = [
            {
                "Put": {
                    "TableName": self.firm_table,
                    "Item": to_attributes(binding_item(binding)),
                    "ConditionExpression": "attribute_exists(SK)",
                }
            },
            {
                "Put": {
                    "TableName": self.case_table,
                    "Item": to_attributes(mirror_item(binding)),
                }
            },
        ]
        if binding.status == REVOKED:
            for role in binding.roles:
                items.append(
                    {
                        "Delete": {
                            "TableName": self.case_table,
                            "Key": {
                                "PK": {"S": case_partition_key(binding.case_id)},
                                "SK": {"S": role_claim_sort_key(role)},
                            },
                            # Release only what this subject holds.
                            "ConditionExpression": (
                                "attribute_not_exists(SK) OR #subject = :subject"
                            ),
                            "ExpressionAttributeNames": {"#subject": "subject"},
                            "ExpressionAttributeValues": {
                                ":subject": {"S": binding.subject}
                            },
                        }
                    }
                )
        try:
            self.client.transact_write_items(TransactItems=items)
        except ClientError as error:
            if error.response.get("Error", {}).get("Code") != _CANCELLED:
                raise
            reasons = error.response.get("CancellationReasons") or []
            if reasons and reasons[0].get("Code") == "ConditionalCheckFailed":
                return None
            raise ConflictError(
                "that client's access changed while this was written; reload and "
                "try again"
            ) from error
        return binding

    # ── reads ───────────────────────────────────────────────────────

    def get(self, firm_id: str, subject: str) -> ClientBinding | None:
        response = self.client.get_item(
            TableName=self.firm_table,
            Key={
                "PK": {"S": firm_partition_key(firm_id)},
                "SK": {"S": client_sort_key(subject)},
            },
            ConsistentRead=True,
        )
        item = response.get("Item")
        return None if not item else binding_from_item(from_attributes(item))

    def find(self, subject: str) -> ClientBinding | None:
        # Limit=2 for FirmStore.find_user's reason: two rows is the invariant
        # violation the port requires a raise on.
        response = self.client.query(
            TableName=self.firm_table,
            IndexName=SUBJECT_INDEX,
            KeyConditionExpression="GSI1PK = :subject",
            ExpressionAttributeValues={":subject": {"S": client_subject_key(subject)}},
            Limit=2,
        )
        items = response.get("Items", [])
        if not items:
            return None
        if len(items) > 1:
            raise RuntimeError(
                "a client resolves to more than one firm; refusing to guess"
            )
        return binding_from_item(from_attributes(items[0]))

    def find_by_email(self, firm_id: str, email: str) -> ClientBinding | None:
        wanted = email.lower()
        for binding in self._query_prefix(self.firm_table, firm_partition_key(firm_id)):
            if binding.email == wanted:
                return binding
        return None

    def list_for_case(self, case_id: str) -> tuple[ClientBinding, ...]:
        found = self._query_prefix(self.case_table, case_partition_key(case_id))
        return tuple(sorted(found, key=lambda b: (b.created_at, b.subject)))

    def _query_prefix(self, table: str, partition: str) -> list[ClientBinding]:
        """Every CLIENT# row in one partition. The prefix is `CLIENT#` with
        its hash, so the case partition's `CLIENTROLE#` claims never match."""
        found: list[ClientBinding] = []
        start_key: dict[str, Any] | None = None
        while True:
            kwargs: dict[str, Any] = {
                "TableName": table,
                "KeyConditionExpression": "PK = :pk AND begins_with(SK, :prefix)",
                "ExpressionAttributeValues": {
                    ":pk": {"S": partition},
                    ":prefix": {"S": "CLIENT#"},
                },
                "ConsistentRead": True,
            }
            if start_key is not None:
                kwargs["ExclusiveStartKey"] = start_key
            response = self.client.query(**kwargs)
            found.extend(
                binding_from_item(from_attributes(item))
                for item in response.get("Items", [])
            )
            start_key = response.get("LastEvaluatedKey")
            if not start_key:
                break
        return found
