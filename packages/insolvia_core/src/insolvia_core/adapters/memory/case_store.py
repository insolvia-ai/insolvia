from __future__ import annotations

from collections.abc import Sequence

from insolvia_core.access import Accessor, may_see_case
from insolvia_core.adapters.memory.debtor_store import MemoryDebtorStore
from insolvia_core.cases import (
    INDEX_BY_ASSIGNEE,
    INDEX_BY_FIRM,
    Case,
    CaseAssignment,
    CasePage,
    ClientCase,
    decode_cursor,
    encode_cursor,
    listing_sort_key,
)
from insolvia_core.debtors import Debtor


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

    def __init__(self, debtor_store: MemoryDebtorStore | None = None) -> None:
        self.cases: dict[str, Case] = {}
        # Keyed as the table is — (case, subject) — so a lookup is the same
        # shape the BatchGetItem does, and a subject-keyed dict cannot make
        # cross-case linkage accidentally work.
        self.assignments: dict[tuple[str, str], CaseAssignment] = {}
        # THE SAME TABLE, in DynamoDB: a case, its assignments and its debtors
        # share a partition, which is what lets `create` write all three in
        # one transaction and `list_for_client` read the `by-client` index the
        # debtor items feed. Here that is one shared MemoryDebtorStore — pass
        # the one the debtor routes are composed with, or the debtors this
        # store writes are invisible to them. A composition with no debtor
        # routes gets a private one.
        self.debtor_store = debtor_store if debtor_store is not None else (
            MemoryDebtorStore()
        )

    def create(
        self,
        case: Case,
        assignment: CaseAssignment,
        debtors: Sequence[Debtor] = (),
    ) -> None:
        if case.id in self.cases:
            raise RuntimeError(f"case {case.id} already exists")
        # Refused BEFORE anything is written, which is what makes this a
        # transaction: the DynamoDB adapter's attribute_not_exists(SK) on each
        # debtor fails the whole TransactWriteItems, never part of it.
        for debtor in debtors:
            if debtor.case_id != case.id:
                raise RuntimeError("a debtor written with a case must belong to it")
            if self.debtor_store.get(case.id, filing_role=debtor.filing_role):
                raise RuntimeError(f"debtor {debtor.filing_role} already exists")
        # All together — the transaction, as dicts. Nothing here can fail
        # between the lines, which is the property the DynamoDB adapter buys
        # with TransactWriteItems.
        self.cases[case.id] = case
        self.assignments[(assignment.case_id, assignment.subject)] = assignment
        for debtor in debtors:
            self.debtor_store.put(debtor)

    def list_for_client(
        self, client_id: str, *, accessor: Accessor
    ) -> tuple[ClientCase, ...]:
        # The index, as a filter over the debtors: sparse (no client_id, no
        # entry), one entry per debtor, sorted by the same "<caseCreatedAt>#
        # <caseId>" value GSI3SK holds, newest first.
        entries = sorted(
            (
                debtor
                for debtor in self.debtor_store.debtors.values()
                if debtor.client_id == client_id
                and debtor.case_created_at is not None
            ),
            key=lambda d: listing_sort_key(d.case_created_at or "", d.case_id),
            reverse=True,
        )
        found: list[ClientCase] = []
        for debtor in entries:
            case = self.get(debtor.case_id, accessor=accessor)
            if case is not None:
                found.append(ClientCase(case=case, filing_role=debtor.filing_role))
        return tuple(found)

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
