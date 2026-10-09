from __future__ import annotations

from insolvia_core.debtors import (
    Debtor,
    LinkOutcome,
    RepointOutcome,
    link_client,
    role_order,
)
from insolvia_core.ports import FirmStore


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

    `firm_store` is the DynamoDB adapter's `firm_table_name`: the store whose
    client rows `link` (and `MemoryCaseStore.create`, which shares this
    store) condition on. Without one, `link` refuses to run, as the
    DynamoDB adapter's does — pass the firm store the routes are composed
    with.
    """

    def __init__(self, firm_store: FirmStore | None = None) -> None:
        self.debtors: dict[tuple[str, str], Debtor] = {}
        self.firm_store = firm_store

    def client_linkable(self, firm_id: str, client_id: str) -> bool:
        """`firm_store.client_linkable_check`'s condition, read from the
        composed firm store: the row exists and `refusal_for_new_case` has
        nothing to say. Evaluated in the same step as the write it guards —
        nothing runs between them here — which is what the transaction buys
        on the other side. True when no firm store is composed (only
        `MemoryCaseStore.create` asks then, for the seed loader's reason)."""
        if self.firm_store is None:
            return True
        client = self.firm_store.get_client(firm_id, client_id)
        return client is not None and client.refusal_for_new_case() is None

    def create(self, debtor: Debtor) -> bool:
        # setdefault is the conditional write: it is the dict equivalent of
        # attribute_not_exists(SK), so this store refuses the same second
        # first-save the real one does rather than being quietly looser.
        key = (debtor.case_id, debtor.filing_role)
        return self.debtors.setdefault(key, debtor) is debtor

    def put(self, debtor: Debtor) -> None:
        # Whole-record replacement, as put_item is. Merging into the stored
        # record here would make this store accept writes the real one refuses,
        # and a suite running against the looser of the two proves nothing.
        self.debtors[(debtor.case_id, debtor.filing_role)] = debtor

    def link(self, debtor: Debtor, *, create: bool, firm_id: str) -> LinkOutcome:
        # The same three conditions the DynamoDB transaction states, checked
        # and applied in one step — nothing else runs between them here —
        # and answered in the order its cancellation reasons are read.
        if debtor.client_id is None:
            raise ValueError("link needs a debtor that names a client")
        if self.firm_store is None:
            raise RuntimeError(
                "link needs the firm store: a link must be conditional on the "
                "client row"
            )
        key = (debtor.case_id, debtor.filing_role)
        if create and key in self.debtors:
            return "role_taken"
        for (case_id, role), other in self.debtors.items():
            if (
                case_id == debtor.case_id
                and role != debtor.filing_role
                and other.client_id is not None
                and other.client_id == debtor.client_id
            ):
                return "client_taken"
        if not self.client_linkable(firm_id, debtor.client_id):
            return "client_unavailable"
        self.debtors[key] = debtor
        return "written"

    def roles_for_client(self, client_id: str) -> tuple[tuple[str, str], ...]:
        # The index as a filter, sparse as it is: no client, no entry.
        return tuple(
            sorted(
                key
                for key, debtor in self.debtors.items()
                if debtor.client_id is not None and debtor.client_id == client_id
            )
        )

    def repoint_client(
        self,
        case_id: str,
        filing_role: str,
        *,
        from_client_id: str,
        to_client_id: str,
    ) -> RepointOutcome:
        # Both of the DynamoDB transaction's conditions, checked and applied
        # in one step. Only the link moves — `link_client` keeps every
        # copied field and the provenance exactly as they are.
        stored = self.debtors.get((case_id, filing_role))
        if stored is None or stored.client_id != from_client_id:
            return "absent"
        for (other_case, role), other in self.debtors.items():
            if (
                other_case == case_id
                and role != filing_role
                and other.client_id == to_client_id
            ):
                return "client_taken"
        if stored.case_created_at is None:
            raise RuntimeError("a debtor naming a client has no case_created_at")
        self.debtors[(case_id, filing_role)] = link_client(
            stored, client_id=to_client_id, case_created_at=stored.case_created_at
        )
        return "written"

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
