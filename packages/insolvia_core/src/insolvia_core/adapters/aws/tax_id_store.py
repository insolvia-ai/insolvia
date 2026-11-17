from __future__ import annotations

import boto3

from insolvia_core.adapters.aws.dynamo import from_attributes, to_attributes
from insolvia_core.cases import partition_key
from insolvia_core.tax_ids import SealedTaxId, sort_key, tax_id_from_item, tax_id_item


class DynamoDbTaxIdStore:
    """TaxIdStore backed by DynamoDB — the same table as the debtor it
    belongs to, under the case's partition, at SK=TAXID#<ref>.

    THE PARTITION IS A LOCATION, NOT AN IDENTITY (insolvia_core.tax_ids):
    the ref is what a debtor points at, and ADR 0022's backfill will move
    these items under the client they belong to. That is why `case_id`
    arrives as a plain argument on both methods rather than being read off
    the sealed item — the item itself names no case.

    Every key half comes from the functions that own it (`partition_key`,
    `tax_ids.sort_key`), the same pair `tax_id_item` is built beside.
    """

    def __init__(self, table_name: str) -> None:
        self.table_name = table_name
        self.client = boto3.client("dynamodb")

    def put(self, case_id: str, sealed: SealedTaxId) -> None:
        # Unconditional: the port says a write under an existing ref REPLACES
        # it (a corrected identifier), so there is no race worth guarding.
        self.client.put_item(
            TableName=self.table_name,
            Item=to_attributes({"PK": partition_key(case_id), **tax_id_item(sealed)}),
        )

    def get(self, case_id: str, ref: str) -> SealedTaxId | None:
        response = self.client.get_item(
            TableName=self.table_name,
            Key={"PK": {"S": partition_key(case_id)}, "SK": {"S": sort_key(ref)}},
            # Strongly consistent for the debtor store's reason: a save and
            # the B121 preview that follows it may be seconds apart.
            ConsistentRead=True,
        )
        item = response.get("Item")
        if not item:
            return None
        return tax_id_from_item(from_attributes(item))
