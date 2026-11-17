"""The one DynamoDB piece of the firm table left: the condition the DynamoDB
case and debtor stores put on a client's row when they link a debtor to it.

The firm stores moved to Postgres (#621, `adapters/postgres/firm_store.py`,
whose `lock_linkable_client` is this condition's successor). This stays only
until the case stores move too (#622); with the firm table gone from
DynamoDB, the services compose those stores without a firm table.
"""

from __future__ import annotations

from typing import Any

from insolvia_core.firm_clients import ACTIVE, MERGED_INTO, MERGING_INTO
from insolvia_core.firm_clients import sort_key as firm_client_sort_key
from insolvia_core.firms import partition_key


def client_linkable_check(
    table_name: str, *, firm_id: str, client_id: str
) -> dict[str, Any]:
    """The TransactWriteItems ConditionCheck that a write LINKING a debtor to
    `client_id` carries on the client's own row in the firm table — the
    write's condition, not a read before it, so a merge's claim cannot land
    between the two (`insolvia_core.client_merge` owns the race).

    `FirmClient.refusal_for_new_case` as a condition expression: the row
    exists in this firm, is active (which excludes archived, and every merged
    client is archived), was never merged, and is not being merged away. A
    client RECEIVING a merge (`mergingFrom`) passes — the merge moves cases
    onto it, so a case linked to it is where the merge would put it anyway.
    A missing row evaluates against an empty item and fails, as it should.

    Owned here, beside `claim_client_merge`'s condition on the same row, so
    the two cannot drift; the case and debtor stores import it. The API's
    grant on this table holds `dynamodb:ConditionCheckItem` for it
    (infra/modules/firm_store)."""
    return {
        "ConditionCheck": {
            "TableName": table_name,
            "Key": {
                "PK": {"S": partition_key(firm_id)},
                "SK": {"S": firm_client_sort_key(client_id)},
            },
            "ConditionExpression": (
                "attribute_exists(SK) AND firmId = :firm AND #status = :active"
                " AND attribute_not_exists(#mergedInto)"
                " AND attribute_not_exists(#mergingInto)"
            ),
            "ExpressionAttributeNames": {
                "#status": "status",
                "#mergedInto": MERGED_INTO,
                "#mergingInto": MERGING_INTO,
            },
            "ExpressionAttributeValues": {
                ":firm": {"S": firm_id},
                ":active": {"S": ACTIVE},
            },
        }
    }
