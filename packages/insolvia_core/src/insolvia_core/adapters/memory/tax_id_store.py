from __future__ import annotations

from insolvia_core.tax_ids import SealedTaxId


class MemoryTaxIdStore:
    """Ephemeral TaxIdStore for tests and the plain development server.

    Keyed by (case_id, ref) — the DynamoDB adapter's PK and SK — so the
    "location plus identity" shape the port describes is a property of this
    dict too, and a test that reads under the wrong case gets the same None
    the real store answers.
    """

    def __init__(self) -> None:
        self.items: dict[tuple[str, str], SealedTaxId] = {}

    def put(self, case_id: str, sealed: SealedTaxId) -> None:
        self.items[(case_id, sealed.ref)] = sealed

    def get(self, case_id: str, ref: str) -> SealedTaxId | None:
        return self.items.get((case_id, ref))
