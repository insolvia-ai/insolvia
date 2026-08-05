from __future__ import annotations

from insolvia_api.core.debtors import Debtor, role_order


def _order(debtor: Debtor) -> tuple[int, str]:
    """Position on the form — the order the DebtorStore port promises, and
    NOT the alphabetical order a bare `sorted()` gives for free. Both stores
    call core/debtors.role_order so they cannot drift apart; the role name is
    the tiebreak so two unrecognised roles still order deterministically."""
    return (
        role_order(debtor.filing_role),
        debtor.filing_role,
    )


class MemoryDebtorStore:
    """Ephemeral DebtorStore for tests and the plain development server.

    Keyed by (case_id, filing_role) — the DynamoDB adapter's PK and SK — so
    "one record per role per case" is a property of this dict rather than
    something every caller has to remember, exactly as it is a property of the
    table's key schema on the other side.
    """

    def __init__(self) -> None:
        self.debtors: dict[tuple[str, str], Debtor] = {}

    def put(self, debtor: Debtor) -> None:
        # Whole-record replacement, as put_item is. Merging into the stored
        # record here would make this store accept writes the real one refuses,
        # and a suite running against the looser of the two proves nothing.
        self.debtors[(debtor.case_id, debtor.filing_role)] = debtor

    def get(self, case_id: str, *, filing_role: str) -> Debtor | None:
        return self.debtors.get((case_id, filing_role))

    def list_for_case(self, case_id: str) -> tuple[Debtor, ...]:
        return tuple(
            sorted(
                (
                    debtor
                    for (stored_case_id, _), debtor in self.debtors.items()
                    if stored_case_id == case_id
                ),
                key=_order,
            )
        )
