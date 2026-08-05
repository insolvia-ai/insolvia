from __future__ import annotations

from typing import Final

import boto3

from insolvia_api.adapters.aws.dynamo import from_attributes, to_attributes
from insolvia_api.core.cases import partition_key
from insolvia_api.core.debtors import (
    Debtor,
    debtor_from_item,
    debtor_item,
    role_order,
    sort_key,
)

# Derived from the one function that builds debtor sort keys, so the two cannot
# drift. This prefix is what makes list_for_case safe to run against a case's
# whole partition: the case root lives there too, under SK=META, and a bare
# `PK = :case` would hand that row to debtor_from_item to parse as a debtor.
DEBTOR_PREFIX: Final = sort_key("")


class DynamoDbDebtorStore:
    """DebtorStore backed by DynamoDB.

    The same table and the same partition as DynamoDbCaseStore — a debtor is a
    child item of its case, not a row in a table of its own — so credentials,
    the absence of a local emulator, and the per-machine dev table all work
    exactly as they do there. Both key halves come from the functions that own
    them (`partition_key` for the case's PK, `sort_key` for the role's SK),
    which is the same pair `debtor_item` writes.
    """

    def __init__(self, table_name: str) -> None:
        self.table_name = table_name
        self.client = boto3.client("dynamodb")

    def put(self, debtor: Debtor) -> None:
        # No ConditionExpression, unlike the case store's create — replacing is
        # the point here. The questionnaire PUTs the whole record on every
        # autosave (core/debtors.parse_debtor says why it must be whole), so an
        # existing item is the normal case rather than the exceptional one, and
        # the route has already resolved the case and its ownership before any
        # of this runs. There is nothing left for a condition to protect.
        self.client.put_item(
            TableName=self.table_name,
            Item=to_attributes(debtor_item(debtor)),
        )

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
