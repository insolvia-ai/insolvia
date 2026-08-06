from __future__ import annotations

from typing import Any

import boto3
from botocore.exceptions import ClientError

from insolvia_api.core.access import Accessor, may_see_case
from insolvia_api.core.cases import (
    INDEX_BY_ASSIGNEE,
    INDEX_BY_FIRM,
    Case,
    CaseAssignment,
    CasePage,
    assignee_key,
    assignment_from_item,
    assignment_item,
    assignment_sort_key,
    case_from_item,
    case_item,
    decode_cursor,
    encode_cursor,
    firm_key,
    partition_key,
)

# The two sparse indexes in infra/modules/case_store. Which one a listing reads
# depends on the caller — see list_for_accessor.
FIRM_INDEX = INDEX_BY_FIRM
ASSIGNEE_INDEX = INDEX_BY_ASSIGNEE

_CONDITION_FAILED = "ConditionalCheckFailedException"


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


class DynamoDbCaseStore:
    """CaseStore backed by DynamoDB.

    Credentials come from the runtime's default provider chain — the Lambda
    execution role in AWS, or in local dev the short-lived credentials
    scripts/dev-up.sh exports from the developer's AWS profile. There is no
    local emulator: `infra/envs/dev` provisions this machine's real table.
    """

    def __init__(self, table_name: str) -> None:
        self.table_name = table_name
        self.client = boto3.client("dynamodb")

    def create(self, case: Case, assignment: CaseAssignment) -> None:
        # ONE TRANSACTION, TWO ITEMS. Not a nicety: a case whose assignment
        # write failed is invisible to the person who just created it, and
        # indistinguishable from the request having failed — except that the id
        # is taken. TransactWriteItems is granted in infra/modules/case_store.
        #
        # attribute_not_exists(PK) makes the write fail rather than silently
        # overwrite if a uuid4 ever collided, or if a retry replayed a create.
        self.client.transact_write_items(
            TransactItems=[
                {
                    "Put": {
                        "TableName": self.table_name,
                        "Item": _to_attributes(case_item(case)),
                        "ConditionExpression": "attribute_not_exists(PK)",
                    }
                },
                {
                    "Put": {
                        "TableName": self.table_name,
                        "Item": _to_attributes(assignment_item(assignment)),
                        # SK, not PK: the case's own META item shares this
                        # partition and is written in the same transaction, so
                        # conditioning on PK would refuse the pair outright.
                        "ConditionExpression": "attribute_not_exists(SK)",
                    }
                },
            ]
        )

    def get(self, case_id: str, *, accessor: Accessor) -> Case | None:
        # ONE ROUND TRIP FOR BOTH HALVES of the access question. The case and
        # the caller's assignment row share a partition, so a BatchGetItem
        # answers "is this my firm's" and "am I linked to it" together. Reading
        # the case, deciding, and then checking linkage would be two sequential
        # calls on the hottest path in the service.
        response = self.client.batch_get_item(
            RequestItems={
                self.table_name: {
                    "Keys": [
                        {"PK": {"S": partition_key(case_id)}, "SK": {"S": "META"}},
                        {
                            "PK": {"S": partition_key(case_id)},
                            "SK": {"S": assignment_sort_key(accessor.subject)},
                        },
                    ],
                    # BatchGetItem CAN do strongly consistent reads, unlike a
                    # GSI query. Kept on: a case read immediately after its
                    # creation must not miss its own assignment row.
                    "ConsistentRead": True,
                }
            }
        )
        items = response.get("Responses", {}).get(self.table_name, [])
        case: Case | None = None
        assigned = False
        for raw in items:
            plain = _from_attributes(raw)
            if plain.get("SK") == "META":
                case = case_from_item(plain)
            else:
                assigned = True
        if case is None:
            return None
        # The whole rule, in core, applied here rather than in the route.
        return case if may_see_case(accessor, case, assigned=assigned) else None

    def list_for_accessor(
        self, accessor: Accessor, *, limit: int, cursor: str | None
    ) -> CasePage:
        if accessor.sees_every_case:
            return self._list_by_firm(accessor, limit=limit, cursor=cursor)
        return self._list_by_assignee(accessor, limit=limit, cursor=cursor)

    def _list_by_firm(
        self, accessor: Accessor, *, limit: int, cursor: str | None
    ) -> CasePage:
        response = self._query(
            index=FIRM_INDEX,
            condition="GSI1PK = :firm",
            values={":firm": {"S": firm_key(accessor.firm_id)}},
            limit=limit,
            cursor=cursor,
        )
        cases = tuple(
            case_from_item(_from_attributes(item)) for item in response.get("Items", [])
        )
        return CasePage(
            cases=cases,
            next_cursor=self._next_cursor(response, index=FIRM_INDEX),
        )

    def _list_by_assignee(
        self, accessor: Accessor, *, limit: int, cursor: str | None
    ) -> CasePage:
        response = self._query(
            index=ASSIGNEE_INDEX,
            condition="GSI2PK = :assignee",
            values={":assignee": {"S": assignee_key(accessor.subject)}},
            limit=limit,
            cursor=cursor,
        )
        # The index holds ASSIGNMENTS, not cases. It could have held a
        # projected copy of the case instead, and that was rejected: a copy
        # goes stale the moment a district or a status changes, and the listing
        # would show values the case detail contradicts. So this is the one
        # read path that costs a second round trip, and it is bounded by the
        # page size rather than by the firm's caseload.
        case_ids = [
            str(_from_attributes(item)["caseId"]) for item in response.get("Items", [])
        ]
        by_id = self._cases_by_id(case_ids)
        cases = tuple(
            case
            for case_id in case_ids
            # Order is preserved from the index query, which is already
            # newest-first. An assignment whose case has vanished is skipped
            # rather than raising: the pair is written in one transaction, so
            # this means a delete landed between the two reads.
            if (case := by_id.get(case_id)) is not None
            # Belt and braces, and cheap: an assignment row for another firm's
            # case should be impossible, and if one exists it must not list.
            if case.firm_id == accessor.firm_id
        )
        return CasePage(
            cases=cases,
            next_cursor=self._next_cursor(response, index=ASSIGNEE_INDEX),
        )

    def _cases_by_id(self, case_ids: list[str]) -> dict[str, Case]:
        """The case records behind a page of assignments.

        BatchGetItem caps at 100 keys per call and a page caps at
        core/cases.MAX_LIST_LIMIT — 100 — so one call always suffices. The
        assert-shaped guard is a slice instead: silently dropping the tail
        would be a listing that is short with nothing saying so.
        """
        if not case_ids:
            return {}
        found: dict[str, Case] = {}
        remaining: list[dict[str, Any]] = [
            {"PK": {"S": partition_key(case_id)}, "SK": {"S": "META"}}
            for case_id in case_ids[:100]
        ]
        while remaining:
            response = self.client.batch_get_item(
                RequestItems={
                    self.table_name: {"Keys": remaining, "ConsistentRead": True}
                }
            )
            for raw in response.get("Responses", {}).get(self.table_name, []):
                case = case_from_item(_from_attributes(raw))
                found[case.id] = case
            # UnprocessedKeys is DynamoDB throttling a partial batch, not an
            # error. Ignoring it is a listing that quietly loses rows under
            # load — the shape of bug that only appears in production.
            remaining = (
                response.get("UnprocessedKeys", {})
                .get(self.table_name, {})
                .get("Keys", [])
            )
        return found

    def _query(
        self,
        *,
        index: str,
        condition: str,
        values: dict[str, Any],
        limit: int,
        cursor: str | None,
    ) -> dict[str, Any]:
        kwargs: dict[str, Any] = {
            "TableName": self.table_name,
            "IndexName": index,
            "KeyConditionExpression": condition,
            "ExpressionAttributeValues": values,
            # Newest first: both sort keys are <createdAt>#<caseId>, so
            # descending order is reverse-chronological without sorting
            # anything in the service.
            "ScanIndexForward": False,
            "Limit": limit,
        }
        if cursor is not None:
            # `index=` is what turns a cursor from the other listing into a
            # 400 instead of a silent skip. See core/cases.decode_cursor.
            kwargs["ExclusiveStartKey"] = {
                key: {"S": value}
                for key, value in decode_cursor(cursor, index=index).items()
            }
        return dict(self.client.query(**kwargs))

    @staticmethod
    def _next_cursor(response: dict[str, Any], *, index: str) -> str | None:
        last_key = response.get("LastEvaluatedKey")
        if not last_key:
            return None
        return encode_cursor(
            {key: value["S"] for key, value in last_key.items()}, index=index
        )

    def update(self, case: Case) -> Case | None:
        try:
            self.client.put_item(
                TableName=self.table_name,
                Item=_to_attributes(case_item(case)),
                # Both halves matter. attribute_exists rejects an update to a
                # case that has since been deleted; the firm check closes the
                # window between the route's read and this write, so a case
                # cannot move firms out from under a caller mid-request.
                ConditionExpression="attribute_exists(PK) AND firmId = :firm",
                ExpressionAttributeValues={":firm": {"S": case.firm_id}},
            )
        except ClientError as error:
            if error.response.get("Error", {}).get("Code") == _CONDITION_FAILED:
                return None
            raise
        return case

    def assign(self, assignment: CaseAssignment) -> None:
        # Unconditional, because the port says idempotent. The firm-admin UI
        # cannot tell whether its first request landed, and re-linking somebody
        # already on the matter has to succeed. The only thing a replay
        # rewrites is `assignedAt`/`assignedBy`, which is the honest record of
        # the most recent linking.
        self.client.put_item(
            TableName=self.table_name, Item=_to_attributes(assignment_item(assignment))
        )

    def unassign(self, case_id: str, subject: str) -> bool:
        try:
            self.client.delete_item(
                TableName=self.table_name,
                Key={
                    "PK": {"S": partition_key(case_id)},
                    "SK": {"S": assignment_sort_key(subject)},
                },
                ConditionExpression="attribute_exists(SK)",
            )
        except ClientError as error:
            if error.response.get("Error", {}).get("Code") == _CONDITION_FAILED:
                return False
            raise
        return True

    def assignees(self, case_id: str) -> tuple[CaseAssignment, ...]:
        assignments: list[CaseAssignment] = []
        start_key: dict[str, Any] | None = None
        while True:
            kwargs: dict[str, Any] = {
                "TableName": self.table_name,
                "KeyConditionExpression": "PK = :case AND begins_with(SK, :prefix)",
                "ExpressionAttributeValues": {
                    ":case": {"S": partition_key(case_id)},
                    # Excludes META and every other case-scoped entity sharing
                    # this partition — documents, and debtors when they land.
                    ":prefix": {"S": "ASSIGNEE#"},
                },
                "ConsistentRead": True,
            }
            if start_key is not None:
                kwargs["ExclusiveStartKey"] = start_key
            response = self.client.query(**kwargs)
            assignments.extend(
                assignment_from_item(_from_attributes(item))
                for item in response.get("Items", [])
            )
            start_key = response.get("LastEvaluatedKey")
            if not start_key:
                break
        # Oldest link first. The sort key is ASSIGNEE#<uuid> and orders by
        # nothing meaningful, so the order DynamoDB returns carries none
        # either — both implementations sort explicitly instead.
        return tuple(sorted(assignments, key=lambda a: (a.assigned_at, a.subject)))
