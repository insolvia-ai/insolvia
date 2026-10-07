"""In-memory FilingStore — the same conditions as the DynamoDB adapter, held
under a lock so concurrent test threads race the way two consumers would."""

from __future__ import annotations

import threading

from ...core.filings import Filing, filing_from_item, filing_item


class MemoryFilingStore:
    def __init__(self) -> None:
        self._items: dict[tuple[str, str], dict[str, object]] = {}
        self._lock = threading.Lock()

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

    def transition(self, filing: Filing, *, expected_state: str) -> bool:
        with self._lock:
            key = (filing.case_id, filing.filing_id)
            stored = self._items.get(key)
            if (
                stored is None
                or stored["state"] != expected_state
                or stored["attemptId"] != filing.attempt_id
            ):
                return False
            self._items[key] = filing_item(filing)
            return True

    def items(self) -> list[dict[str, object]]:
        """Every stored item — for tests that search them for secrets."""
        with self._lock:
            return [dict(item) for item in self._items.values()]
