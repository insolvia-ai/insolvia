from __future__ import annotations

from insolvia_api.core.access import Accessor, may_see_case
from insolvia_api.core.cases import (
    INDEX_BY_ASSIGNEE,
    INDEX_BY_FIRM,
    Case,
    CaseAssignment,
    CasePage,
    decode_cursor,
    encode_cursor,
    listing_sort_key,
)


class MemoryCaseStore:
    """Ephemeral CaseStore for tests and the plain development server.

    It applies `may_see_case` and orders results exactly as the DynamoDB
    adapter does, because a test suite running against a store with weaker
    rules than production would pass on code that leaks other firms' cases.

    It also mirrors the awkward part: which index a listing reads depends on
    the caller, and a cursor minted against one is refused by the other. A
    memory store that paginated one uniform way would hide the only bug that
    design can produce.
    """

    def __init__(self) -> None:
        self.cases: dict[str, Case] = {}
        # Keyed as the table is — (case, subject) — so a lookup is the same
        # shape the BatchGetItem does, and a subject-keyed dict cannot make
        # cross-case linkage accidentally work.
        self.assignments: dict[tuple[str, str], CaseAssignment] = {}

    def create(self, case: Case, assignment: CaseAssignment) -> None:
        if case.id in self.cases:
            raise RuntimeError(f"case {case.id} already exists")
        # Both, together — the transaction, as a dict. Nothing here can fail
        # between the two lines, which is the property the DynamoDB adapter
        # buys with TransactWriteItems.
        self.cases[case.id] = case
        self.assignments[(assignment.case_id, assignment.subject)] = assignment

    def get(self, case_id: str, *, accessor: Accessor) -> Case | None:
        case = self.cases.get(case_id)
        if case is None:
            return None
        assigned = (case_id, accessor.subject) in self.assignments
        return case if may_see_case(accessor, case, assigned=assigned) else None

    def read_for_worker(self, case_id: str) -> Case | None:
        # No access rule, exactly as the port says: the pipeline worker's
        # authority is the accepted job, and only entrypoints compose this
        # path — never a route.
        return self.cases.get(case_id)

    def list_for_accessor(
        self, accessor: Accessor, *, limit: int, cursor: str | None
    ) -> CasePage:
        if accessor.sees_every_case:
            index = INDEX_BY_FIRM
            visible = [
                case for case in self.cases.values() if case.firm_id == accessor.firm_id
            ]
        else:
            index = INDEX_BY_ASSIGNEE
            visible = [
                case
                for case in self.cases.values()
                if case.firm_id == accessor.firm_id
                and (case.id, accessor.subject) in self.assignments
            ]

        # Mirrors both GSIs: sorted by the same "<createdAt>#<id>" value,
        # newest first. They agree on the sort key by construction — that is
        # what core/cases.listing_sort_key is for.
        ordered = sorted(
            visible,
            key=lambda case: listing_sort_key(case.created_at, case.id),
            reverse=True,
        )
        if cursor is not None:
            after = decode_cursor(cursor, index=index).get("SK", "")
            ordered = [
                case
                for case in ordered
                if listing_sort_key(case.created_at, case.id) < after
            ]

        page = ordered[:limit]
        next_cursor = None
        if len(ordered) > limit and page:
            last = page[-1]
            next_cursor = encode_cursor(
                {"SK": listing_sort_key(last.created_at, last.id)}, index=index
            )
        return CasePage(cases=tuple(page), next_cursor=next_cursor)

    def update(self, case: Case) -> Case | None:
        existing = self.cases.get(case.id)
        if existing is None or existing.firm_id != case.firm_id:
            return None
        self.cases[case.id] = case
        return case

    def assign(self, assignment: CaseAssignment) -> None:
        # Unconditional: the port says idempotent, and the DynamoDB adapter's
        # PutItem is too.
        self.assignments[(assignment.case_id, assignment.subject)] = assignment

    def unassign(self, case_id: str, subject: str) -> bool:
        return self.assignments.pop((case_id, subject), None) is not None

    def assignees(self, case_id: str) -> tuple[CaseAssignment, ...]:
        return tuple(
            sorted(
                (a for a in self.assignments.values() if a.case_id == case_id),
                key=lambda a: (a.assigned_at, a.subject),
            )
        )
