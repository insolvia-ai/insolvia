from __future__ import annotations

from typing import Any, Final

import boto3
from botocore.exceptions import ClientError

from insolvia_core.cases import partition_key
from insolvia_core.documents import (
    Document,
    document_from_item,
    document_item,
    list_order,
    sort_key,
)

# Derived from the one function that builds document sort keys, so the two
# cannot drift. The prefix is what makes list_for_case safe to run against a
# case's whole partition: the case root lives there too, under SK=META, and a
# bare `PK = :case` would hand that row to document_from_item to parse.
DOCUMENT_PREFIX: Final = sort_key("")


# Flat by construction — a document item is strings and one number, with no
# nested map anywhere — so these are the same two-line conversions
# adapters/aws/case_store.py carries for the same reason. If a document item
# ever grows a nested attribute, the answer is one shared recursive converter
# in this layer rather than a third copy: two converters that must agree on an
# encoding are two converters that will eventually disagree about a bool.
def _to_attributes(item: dict[str, str | int]) -> dict[str, Any]:
    return {
        key: {"N": str(value)} if isinstance(value, int) else {"S": value}
        for key, value in item.items()
    }


def _from_attributes(item: dict[str, Any]) -> dict[str, str | int]:
    plain: dict[str, str | int] = {}
    for key, value in item.items():
        if "N" in value:
            plain[key] = int(value["N"])
        elif "S" in value:
            plain[key] = value["S"]
    return plain


class DynamoDbDocumentStore:
    """DocumentStore backed by DynamoDB.

    The same table and the same partition as DynamoDbCaseStore — a document row
    is a child item of its case, not a row in a table of its own — so
    credentials, the absence of a local emulator, and the per-machine dev table
    all work exactly as they do there. Both key halves come from the functions
    that own them (`partition_key` for the case, `sort_key` for the document),
    which is the same pair `document_item` writes.
    """

    def __init__(self, table_name: str) -> None:
        self.table_name = table_name
        self.client = boto3.client("dynamodb")

    def create(self, document: Document) -> None:
        # attribute_not_exists(SK) makes the write fail rather than silently
        # overwrite. The id is a uuid4 so a collision is not a real risk; what
        # this rules out is a retried request replacing a row whose object has
        # already been uploaded, which would leave the bytes in the bucket with
        # nothing pointing at them.
        self.client.put_item(
            TableName=self.table_name,
            Item=_to_attributes(document_item(document)),
            ConditionExpression="attribute_not_exists(SK)",
        )

    def update(self, document: Document) -> Document | None:
        try:
            self.client.put_item(
                TableName=self.table_name,
                Item=_to_attributes(document_item(document)),
                # The mirror of create's attribute_not_exists(SK). Without it a
                # confirm racing a delete would PUT the row back — a `stored`
                # document, pointing at an object the delete already marked,
                # that the case lists and nobody can open. No owner check to
                # pair with it, unlike the case store: this table's key is
                # (case, document) and the route has already resolved the case
                # through CaseStore, which is where ownership lives.
                ConditionExpression="attribute_exists(SK)",
            )
        except ClientError as error:
            if (
                error.response.get("Error", {}).get("Code")
                == "ConditionalCheckFailedException"
            ):
                return None
            raise
        return document

    def get(self, case_id: str, document_id: str) -> Document | None:
        response = self.client.get_item(
            TableName=self.table_name,
            Key={
                "PK": {"S": partition_key(case_id)},
                "SK": {"S": sort_key(document_id)},
            },
            # Strongly consistent because the caller has very likely just
            # written this record: the client POSTs, uploads, and immediately
            # asks for a download URL. An eventually consistent read would 404
            # a document the same client was handed a moment ago.
            ConsistentRead=True,
        )
        item = response.get("Item")
        if not item:
            return None
        return document_from_item(_from_attributes(item))

    def list_for_case(self, case_id: str) -> tuple[Document, ...]:
        # Paginated, unlike the debtor query, and the difference is the schema
        # rather than caution: a case has at most three debtors because
        # FILING_ROLES caps it, while nothing caps documents. Items are a few
        # hundred bytes so a 1 MB page holds thousands and this loop will
        # essentially never run twice — but "essentially never" is exactly the
        # case where a truncated list silently loses a document, and the port
        # promises all of them.
        items: list[dict[str, Any]] = []
        start_key: dict[str, Any] | None = None
        while True:
            kwargs: dict[str, Any] = {
                "TableName": self.table_name,
                "KeyConditionExpression": "PK = :case AND begins_with(SK, :prefix)",
                "ExpressionAttributeValues": {
                    ":case": {"S": partition_key(case_id)},
                    ":prefix": {"S": DOCUMENT_PREFIX},
                },
                "ConsistentRead": True,
            }
            if start_key is not None:
                kwargs["ExclusiveStartKey"] = start_key
            response = self.client.query(**kwargs)
            items.extend(response.get("Items", []))
            start_key = response.get("LastEvaluatedKey")
            if not start_key:
                break

        # Sorted here rather than relying on the query's order: the sort key is
        # DOCUMENT#<uuid4>, so what DynamoDB returns is ordered by a random
        # number. core/documents.list_order is the shared definition.
        return tuple(
            sorted(
                (document_from_item(_from_attributes(item)) for item in items),
                key=list_order,
                reverse=True,
            )
        )

    def delete(self, case_id: str, document_id: str) -> bool:
        try:
            self.client.delete_item(
                TableName=self.table_name,
                Key={
                    "PK": {"S": partition_key(case_id)},
                    "SK": {"S": sort_key(document_id)},
                },
                # DeleteItem is idempotent by default — deleting nothing
                # succeeds — and here that would be wrong. The route deletes
                # the object only after this returns True, so without the
                # condition two concurrent deletes would both "succeed" and
                # both go on to delete an object one of them no longer owns.
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
