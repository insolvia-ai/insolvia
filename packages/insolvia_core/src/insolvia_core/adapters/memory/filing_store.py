"""In-memory FilingStore — the same conditions as the DynamoDB adapter, held
under a lock so concurrent test threads race the way two consumers would.

The case half of a filing write (`FiledCase`) lands in the MemoryCaseStore
this store is composed with — the same table in DynamoDB, so the same
transaction: every condition is checked first, then every write is made,
with nothing between them that can fail."""

from __future__ import annotations

import threading
from dataclasses import replace

from insolvia_core.adapters.memory.case_store import MemoryCaseStore
from insolvia_core.filings import FiledCase, Filing, filing_from_item, filing_item


class MemoryFilingStore:
    def __init__(self, case_store: MemoryCaseStore | None = None) -> None:
        self._items: dict[tuple[str, str], dict[str, object]] = {}
        self._lock = threading.Lock()
        self.case_store = case_store if case_store is not None else MemoryCaseStore()

    def get(self, case_id: str, filing_id: str) -> Filing | None:
        with self._lock:
            item = self._items.get((case_id, filing_id))
        return filing_from_item(item) if item is not None else None

    def claim(self, filing: Filing) -> bool:
        with self._lock:
            key = (filing.case_id, filing.filing_id)
            if key in self._items:
                return False
            self._items[key] = filing_item(filing)
            return True

    def transition(
        self,
        filing: Filing,
        *,
        expected_state: str,
        filed_case: FiledCase | None = None,
    ) -> bool:
        return self._write(
            filing,
            expected_state=expected_state,
            filed_case=filed_case,
            unresolved=False,
        )

    def resolve(
        self,
        filing: Filing,
        *,
        expected_state: str,
        filed_case: FiledCase | None = None,
    ) -> bool:
        return self._write(
            filing,
            expected_state=expected_state,
            filed_case=filed_case,
            unresolved=True,
        )

    def _write(
        self,
        filing: Filing,
        *,
        expected_state: str,
        filed_case: FiledCase | None,
        unresolved: bool,
    ) -> bool:
        with self._lock:
            key = (filing.case_id, filing.filing_id)
            stored = self._items.get(key)
            if (
                stored is None
                or stored["state"] != expected_state
                or stored["attemptId"] != filing.attempt_id
                or (unresolved and "resolution" in stored)
            ):
                return False
            if filed_case is not None:
                cases = self.case_store
                current = cases.cases.get(filed_case.case.id)
                change = filed_case.status_change
                if (
                    current is None
                    or current.deleted
                    or current.firm_id != filed_case.case.firm_id
                    or current.status != filed_case.expected_status
                    or any(
                        row.changed_at == change.changed_at
                        for row in cases.history.get(current.id, ())
                    )
                ):
                    return False
                # The DynamoDB adapter's UpdateItem: the lifecycle attributes
                # only, over whatever else the stored case now says.
                filed = filed_case.case
                cases.cases[current.id] = replace(
                    current,
                    status=filed.status,
                    filed_at=filed.filed_at,
                    case_number=filed.case_number,
                    updated_at=filed.updated_at,
                )
                cases.history.setdefault(current.id, []).append(change)
            self._items[key] = filing_item(filing)
            return True

    def items(self) -> list[dict[str, object]]:
        """Every stored item — for tests that search them for secrets."""
        with self._lock:
            return [dict(item) for item in self._items.values()]
