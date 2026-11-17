from __future__ import annotations

from collections.abc import Sequence
from typing import Any

import boto3
from botocore.exceptions import ClientError

from insolvia_core.access import Accessor, ClientAccessor, may_see_case
from insolvia_core.adapters.aws.dynamo import from_attributes, to_attributes
from insolvia_core.adapters.aws.firm_store import client_linkable_check
from insolvia_core.cases import (
    INDEX_BY_ASSIGNEE,
    INDEX_BY_CLIENT,
    INDEX_BY_FIRM,
    STATUS_MOVED,
    Case,
    CaseAssignment,
    CasePage,
    ClientCase,
    StatusChange,
    assignee_key,
    assignment_from_item,
    assignment_item,
    assignment_sort_key,
    case_from_item,
    case_item,
    client_key,
    decode_cursor,
    encode_cursor,
    firm_key,
    listing_cursor_tag,
    partition_key,
    status_change_from_item,
    status_change_item,
)
from insolvia_core.clients import (
    PUBLIC_STATUS_ATTRIBUTES,
    CasePublicStatus,
    public_status_from_case_item,
)
from insolvia_core.debtors import Debtor, debtor_item
from insolvia_core.errors import ClientUnavailableError, ConflictError

# The sparse indexes in infra/modules/case_store. Which of the first two a
# listing reads depends on the caller — see list_for_accessor. The third is a
# client's cases (ADR 0022), fed by debtor items.
FIRM_INDEX = INDEX_BY_FIRM
ASSIGNEE_INDEX = INDEX_BY_ASSIGNEE
CLIENT_INDEX = INDEX_BY_CLIENT

_CONDITION_FAILED = "ConditionalCheckFailedException"
_TRANSACTION_CANCELLED = "TransactionCanceledException"


class DynamoDbCaseStore:
    """CaseStore backed by DynamoDB.

    Credentials come from the runtime's default provider chain — the Lambda
    execution role in AWS, or in local dev the short-lived credentials
    scripts/dev-up.sh exports from the developer's AWS profile. There is no
    local emulator: `infra/envs/dev` provisions this machine's real table.

    `firm_table_name` is the FIRM table: with it, `create` conditions every
    debtor's link on its client's row (`firm_store.client_linkable_check`),
    so a case cannot be opened for a client a merge has claimed since the
    route read it. The API composes it. A composition that opens no case
    leaves it None — and so does the seed loader, which links only the
    clients it has just written itself, and whose role holds no
    ConditionCheckItem on the firm table (infra/envs/ci-trust).
    """

    def __init__(self, table_name: str, *, firm_table_name: str | None = None) -> None:
        self.table_name = table_name
        self.firm_table_name = firm_table_name
        self.client = boto3.client("dynamodb")

    def create(
        self,
        case: Case,
        assignment: CaseAssignment,
        debtors: Sequence[Debtor] = (),
    ) -> None:
        # ONE TRANSACTION: the case, its creator's assignment, and the debtors
        # copied from its clients (ADR 0022). Not a nicety: a case whose
        # assignment write failed is invisible to the person who just created
        # it, and a case whose debtor write failed is invisible from its
        # client — the debtor item is the `by-client` index entry. Either is
        # indistinguishable from the request having failed, except that the
        # id is taken. TransactWriteItems is granted in infra/modules/case_store.
        #
        # attribute_not_exists(PK) makes the write fail rather than silently
        # overwrite if a uuid4 ever collided, or if a retry replayed a create.
        #
        # And, with the firm table composed, a ConditionCheck on each linked
        # client's row — LAST, one per distinct client (a transaction may
        # not touch one item twice), so their cancellation reasons are the
        # tail of the list and say which client refused.
        for debtor in debtors:
            if debtor.case_id != case.id:
                raise RuntimeError("a debtor written with a case must belong to it")
        firm_table = self.firm_table_name
        clients = (
            []
            if firm_table is None
            else list(
                dict.fromkeys(d.client_id for d in debtors if d.client_id is not None)
            )
        )
        items: list[dict[str, Any]] = [
            {
                "Put": {
                    "TableName": self.table_name,
                    "Item": to_attributes(case_item(case)),
                    "ConditionExpression": "attribute_not_exists(PK)",
                }
            },
            {
                "Put": {
                    "TableName": self.table_name,
                    "Item": to_attributes(assignment_item(assignment)),
                    # SK, not PK: the case's own META item shares this
                    # partition and is written in the same transaction, so
                    # conditioning on PK would refuse the pair outright.
                    "ConditionExpression": "attribute_not_exists(SK)",
                }
            },
            *(
                {
                    "Put": {
                        "TableName": self.table_name,
                        "Item": to_attributes(debtor_item(debtor)),
                        # SK for the assignment's reason; it is also
                        # DebtorStore.create's own guard, so a role can
                        # exist at most once however it was written.
                        "ConditionExpression": "attribute_not_exists(SK)",
                    }
                }
                for debtor in debtors
            ),
            *(
                client_linkable_check(
                    firm_table, firm_id=case.firm_id, client_id=client_id
                )
                for client_id in clients
                if firm_table is not None
            ),
        ]
        try:
            self.client.transact_write_items(TransactItems=items)
        except ClientError as error:
            if error.response.get("Error", {}).get("Code") != _TRANSACTION_CANCELLED:
                raise
            reasons = error.response.get("CancellationReasons") or []
            codes = [reason.get("Code") for reason in reasons]
            if len(codes) == len(items) and clients:
                for client_id, code in zip(
                    clients, codes[-len(clients) :], strict=True
                ):
                    if code == "ConditionalCheckFailed":
                        raise ClientUnavailableError(client_id) from error
            raise

    def list_for_client(
        self, client_id: str, *, accessor: Accessor
    ) -> tuple[ClientCase, ...]:
        # The index holds DEBTOR items — which role, which case — never the
        # case: the by-assignee listing's reasoning, a projected copy of the
        # case would go stale. So it is one query plus one BatchGetItem, and
        # the BatchGetItem fetches each case's META AND the caller's
        # assignment row together, which is everything `may_see_case` needs.
        roles: list[tuple[str, str]] = []
        start_key: dict[str, Any] | None = None
        while True:
            kwargs: dict[str, Any] = {
                "TableName": self.table_name,
                "IndexName": CLIENT_INDEX,
                "KeyConditionExpression": "GSI3PK = :client",
                "ExpressionAttributeValues": {":client": {"S": client_key(client_id)}},
                # Newest matter first — GSI3SK is <caseCreatedAt>#<caseId>.
                "ScanIndexForward": False,
            }
            if start_key is not None:
                kwargs["ExclusiveStartKey"] = start_key
            response = self.client.query(**kwargs)
            for raw in response.get("Items", []):
                plain = from_attributes(raw)
                roles.append((str(plain["caseId"]), str(plain["filingRole"])))
            start_key = response.get("LastEvaluatedKey")
            if not start_key:
                break
        if not roles:
            return ()
        visible = self._visible_cases([case_id for case_id, _ in roles], accessor)
        return tuple(
            ClientCase(case=visible[case_id], filing_role=role)
            for case_id, role in roles
            if case_id in visible
        )

    def _visible_cases(
        self, case_ids: list[str], accessor: Accessor
    ) -> dict[str, Case]:
        """The cases among `case_ids` this accessor may see: each case's META
        and the caller's assignment row, read together and decided by
        `may_see_case`. 50 cases per call — two keys each, BatchGetItem's
        100-key cap."""
        unique = list(dict.fromkeys(case_ids))
        metas: dict[str, Case] = {}
        assigned: set[str] = set()
        for offset in range(0, len(unique), 50):
            remaining: list[dict[str, Any]] = []
            for case_id in unique[offset : offset + 50]:
                remaining.append(
                    {"PK": {"S": partition_key(case_id)}, "SK": {"S": "META"}}
                )
                remaining.append(
                    {
                        "PK": {"S": partition_key(case_id)},
                        "SK": {"S": assignment_sort_key(accessor.subject)},
                    }
                )
            while remaining:
                response = self.client.batch_get_item(
                    RequestItems={
                        self.table_name: {"Keys": remaining, "ConsistentRead": True}
                    }
                )
                for raw in response.get("Responses", {}).get(self.table_name, []):
                    plain = from_attributes(raw)
                    if plain.get("SK") == "META":
                        case = case_from_item(plain)
                        metas[case.id] = case
                    else:
                        assigned.add(str(plain.get("caseId")))
                # Throttled keys are retried, never dropped — `_cases_by_id`'s
                # reason: a list that quietly loses rows under load.
                remaining = (
                    response.get("UnprocessedKeys", {})
                    .get(self.table_name, {})
                    .get("Keys", [])
                )
        return {
            case_id: case
            for case_id, case in metas.items()
            if may_see_case(accessor, case, assigned=case_id in assigned)
        }

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
            plain = from_attributes(raw)
            if plain.get("SK") == "META":
                case = case_from_item(plain)
            else:
                assigned = True
        if case is None:
            return None
        # The whole rule, in core, applied here rather than in the route.
        return case if may_see_case(accessor, case, assigned=assigned) else None

    def read_for_worker(self, case_id: str) -> Case | None:
        # No access rule and no assignment read, exactly as the port says:
        # the pipeline worker's authority is the accepted job, and only the
        # worker entrypoints compose this path — never a route. Strongly
        # consistent for the job store's reason: the worker may run within
        # milliseconds of the accept that read this case.
        response = self.client.get_item(
            TableName=self.table_name,
            Key={"PK": {"S": partition_key(case_id)}, "SK": {"S": "META"}},
            ConsistentRead=True,
        )
        item = response.get("Item")
        if not item:
            return None
        return case_from_item(from_attributes(item))

    def public_status(self, client: ClientAccessor) -> CasePublicStatus | None:
        # A ProjectionExpression of exactly the public attributes, so the
        # rest of the case row never leaves the table on a portal request.
        # `status` is a DynamoDB reserved word, hence the name placeholders.
        names = {f"#a{i}": name for i, name in enumerate(PUBLIC_STATUS_ATTRIBUTES)}
        response = self.client.get_item(
            TableName=self.table_name,
            Key={"PK": {"S": partition_key(client.case_id)}, "SK": {"S": "META"}},
            ProjectionExpression=", ".join(names),
            ExpressionAttributeNames=names,
        )
        item = response.get("Item")
        if not item:
            return None
        return public_status_from_case_item(
            from_attributes(item), firm_id=client.firm_id
        )

    def list_for_accessor(
        self,
        accessor: Accessor,
        *,
        limit: int,
        cursor: str | None,
        archived: bool = False,
    ) -> CasePage:
        if accessor.sees_every_case:
            index = FIRM_INDEX
            condition = "GSI1PK = :key"
            key = firm_key(accessor.firm_id)
        else:
            index = ASSIGNEE_INDEX
            condition = "GSI2PK = :key"
            key = assignee_key(accessor.subject)
        tag = listing_cursor_tag(index, archived=archived)
        start_key: dict[str, Any] | None = None
        if cursor is not None:
            # `index=` is what turns a cursor from the other listing — or the
            # other view — into a 400 instead of a silent skip. See
            # core/cases.decode_cursor.
            start_key = {
                name: {"S": value}
                for name, value in decode_cursor(cursor, index=tag).items()
            }

        # FILL THE PAGE (#355). The view is a filter over the index, so one
        # query of `limit` rows can come back short — or empty — because the
        # rows it read were archived or deleted. Each pass asks for at most
        # the rows still missing, so the page never overfills, and the last
        # pass's LastEvaluatedKey is exactly where the next page starts:
        # every row before it was either returned or belonged to the other
        # view.
        found: list[Case] = []
        while True:
            response = self._query(
                index=index,
                condition=condition,
                values={":key": {"S": key}},
                limit=limit - len(found),
                start_key=start_key,
            )
            if index == FIRM_INDEX:
                cases = [
                    case_from_item(from_attributes(item))
                    for item in response.get("Items", [])
                ]
            else:
                cases = self._cases_for_assignments(response, accessor)
            found.extend(
                case for case in cases if not case.deleted and case.archived == archived
            )
            start_key = response.get("LastEvaluatedKey") or None
            if start_key is None or len(found) >= limit:
                break
        next_cursor = (
            encode_cursor(
                {name: value["S"] for name, value in start_key.items()}, index=tag
            )
            if start_key is not None
            else None
        )
        return CasePage(cases=tuple(found), next_cursor=next_cursor)

    def _cases_for_assignments(
        self, response: dict[str, Any], accessor: Accessor
    ) -> list[Case]:
        # The by-assignee index holds ASSIGNMENTS, not cases. It could have
        # held a projected copy of the case instead, and that was rejected: a
        # copy goes stale the moment a district or a status changes, and the
        # listing would show values the case detail contradicts. So this is
        # the one read path that costs a second round trip, and it is bounded
        # by the page size rather than by the firm's caseload.
        case_ids = [
            str(from_attributes(item)["caseId"]) for item in response.get("Items", [])
        ]
        by_id = self._cases_by_id(case_ids)
        return [
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
        ]

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
                case = case_from_item(from_attributes(raw))
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
        start_key: dict[str, Any] | None,
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
        if start_key is not None:
            kwargs["ExclusiveStartKey"] = start_key
        return dict(self.client.query(**kwargs))

    def update(
        self,
        case: Case,
        *,
        expected_status: str | None = None,
        status_change: StatusChange | None = None,
    ) -> Case | None:
        # Both halves matter. attribute_exists rejects an update to a case
        # that has since been deleted; the firm check closes the window
        # between the route's read and this write, so a case cannot move
        # firms out from under a caller mid-request. The status check (#355)
        # stops a whole-record write read before a status move from putting
        # the old status back.
        condition = "attribute_exists(PK) AND firmId = :firm"
        values: dict[str, Any] = {":firm": {"S": case.firm_id}}
        names: dict[str, str] = {}
        if expected_status is not None:
            condition += " AND #status = :expected"
            values[":expected"] = {"S": expected_status}
            names["#status"] = "status"
        put: dict[str, Any] = {
            "TableName": self.table_name,
            "Item": to_attributes(case_item(case)),
            "ConditionExpression": condition,
            "ExpressionAttributeValues": values,
        }
        if names:
            put["ExpressionAttributeNames"] = names
        try:
            if status_change is None:
                self.client.put_item(**put)
            else:
                # ONE TRANSACTION: the record and its history row, so the
                # history can never describe a move that did not land.
                self.client.transact_write_items(
                    TransactItems=[
                        {"Put": put},
                        {
                            "Put": {
                                "TableName": self.table_name,
                                "Item": to_attributes(
                                    status_change_item(status_change)
                                ),
                                "ConditionExpression": "attribute_not_exists(SK)",
                            }
                        },
                    ]
                )
        except ClientError as error:
            code = error.response.get("Error", {}).get("Code")
            if code not in (_CONDITION_FAILED, _TRANSACTION_CANCELLED):
                raise
            return self._refused_update(case, expected_status)
        return case

    def _refused_update(self, case: Case, expected_status: str | None) -> Case | None:
        """Why a conditional update failed, told apart by one consistent
        read: gone or moved firms is None (the port's 404), a status that
        moved underneath the caller is a 409."""
        response = self.client.get_item(
            TableName=self.table_name,
            Key={"PK": {"S": partition_key(case.id)}, "SK": {"S": "META"}},
            ConsistentRead=True,
        )
        item = response.get("Item")
        if not item:
            return None
        stored = case_from_item(from_attributes(item))
        if stored.firm_id != case.firm_id:
            return None
        if expected_status is not None and stored.status != expected_status:
            raise ConflictError(STATUS_MOVED)
        # The condition held on re-read: the transaction lost to something
        # else (a history row at the same microsecond). Let it surface.
        raise RuntimeError("case update was refused for an unknown reason")

    def status_history(self, case_id: str) -> tuple[StatusChange, ...]:
        changes: list[StatusChange] = []
        start_key: dict[str, Any] | None = None
        while True:
            kwargs: dict[str, Any] = {
                "TableName": self.table_name,
                "KeyConditionExpression": "PK = :case AND begins_with(SK, :prefix)",
                "ExpressionAttributeValues": {
                    ":case": {"S": partition_key(case_id)},
                    ":prefix": {"S": "STATUS#"},
                },
                "ConsistentRead": True,
            }
            if start_key is not None:
                kwargs["ExclusiveStartKey"] = start_key
            response = self.client.query(**kwargs)
            changes.extend(
                status_change_from_item(from_attributes(item))
                for item in response.get("Items", [])
            )
            start_key = response.get("LastEvaluatedKey")
            if not start_key:
                break
        # The sort key is the timestamp, so the query is already oldest
        # first; sorted anyway so the two adapters agree by construction.
        return tuple(sorted(changes, key=lambda change: change.changed_at))

    def assign(self, assignment: CaseAssignment) -> None:
        # Unconditional, because the port says idempotent. The firm-admin UI
        # cannot tell whether its first request landed, and re-linking somebody
        # already on the matter has to succeed. The only thing a replay
        # rewrites is `assignedAt`/`assignedBy`, which is the honest record of
        # the most recent linking.
        self.client.put_item(
            TableName=self.table_name, Item=to_attributes(assignment_item(assignment))
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
                assignment_from_item(from_attributes(item))
                for item in response.get("Items", [])
            )
            start_key = response.get("LastEvaluatedKey")
            if not start_key:
                break
        # Oldest link first. The sort key is ASSIGNEE#<uuid> and orders by
        # nothing meaningful, so the order DynamoDB returns carries none
        # either — both implementations sort explicitly instead.
        return tuple(sorted(assignments, key=lambda a: (a.assigned_at, a.subject)))
