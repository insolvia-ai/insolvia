from __future__ import annotations

from dataclasses import replace

from insolvia_core.firm_clients import (
    ACTIVE,
    ARCHIVED,
    FirmClient,
    sorted_firm_clients,
)
from insolvia_core.firms import Firm, FirmUser
from insolvia_core.library_creditors import LibraryCreditor


class MemoryFirmStore:
    """Ephemeral FirmStore for tests and the plain development server.

    It enforces the same conditions the DynamoDB adapter does — no overwrite on
    create, firm-scoped reads, a raise on a subject in two firms — because a
    suite running against a store with weaker rules than production would pass
    on code that crosses tenants.
    """

    def __init__(self) -> None:
        self.firms: dict[str, Firm] = {}
        # Keyed exactly as the table is: the firm partition, then the person.
        # A flat dict keyed by subject alone would make the firm-scoped reads
        # below impossible to get wrong, which is the problem — the DynamoDB
        # adapter CAN get them wrong, so this one has to be able to as well.
        self.users: dict[tuple[str, str], FirmUser] = {}
        # Same keying discipline as `users` above, for the same reason.
        self.library_creditors: dict[tuple[str, str], LibraryCreditor] = {}
        # And again for the client directory (ADR 0022).
        self.clients: dict[tuple[str, str], FirmClient] = {}

    # ── Firms ───────────────────────────────────────────────────────

    def create_firm(self, firm: Firm) -> None:
        # RuntimeError rather than a ValidationError, matching
        # MemoryDocumentStore: a 400 would blame the caller for a uuid
        # collision or a replayed write, and neither is something they did.
        if firm.id in self.firms:
            raise RuntimeError(f"firm {firm.id} already exists")
        self.firms[firm.id] = firm

    def get_firm(self, firm_id: str) -> Firm | None:
        return self.firms.get(firm_id)

    def list_firms(self) -> tuple[Firm, ...]:
        # Same ordering contract as the DynamoDB adapter: name then id, so a
        # suite passing against this store proves the ordering the portal
        # renders.
        return tuple(sorted(self.firms.values(), key=lambda firm: (firm.name, firm.id)))

    def update_firm(self, firm: Firm) -> Firm | None:
        if firm.id not in self.firms:
            return None
        self.firms[firm.id] = firm
        return firm

    # ── Firm users ──────────────────────────────────────────────────

    def add_user(self, user: FirmUser) -> None:
        if (user.firm_id, user.subject) in self.users:
            raise RuntimeError("firm user already exists")
        self.users[(user.firm_id, user.subject)] = user

    def get_user(self, firm_id: str, subject: str) -> FirmUser | None:
        return self.users.get((firm_id, subject))

    def find_user(self, subject: str) -> FirmUser | None:
        matches = [user for user in self.users.values() if user.subject == subject]
        if not matches:
            return None
        if len(matches) > 1:
            raise RuntimeError(
                "a firm user resolves to more than one firm; refusing to guess"
            )
        return matches[0]

    def list_users(self, firm_id: str) -> tuple[FirmUser, ...]:
        # BY SURNAME, and this key must stay identical to the DynamoDB
        # adapter's — the two stores exist to be interchangeable, and an
        # ordering that differed between them would make a test that passes
        # here prove nothing about production.
        return tuple(
            sorted(
                (user for user in self.users.values() if user.firm_id == firm_id),
                key=lambda user: (user.last_name, user.first_name, user.subject),
            )
        )

    def update_user(self, user: FirmUser) -> FirmUser | None:
        if (user.firm_id, user.subject) not in self.users:
            return None
        self.users[(user.firm_id, user.subject)] = user
        return user

    def remove_user(self, firm_id: str, subject: str) -> bool:
        return self.users.pop((firm_id, subject), None) is not None

    # ── Library creditors ───────────────────────────────────────────

    def create_library_creditor(self, creditor: LibraryCreditor) -> None:
        key = (creditor.firm_id, creditor.id)
        if key in self.library_creditors:
            raise RuntimeError(f"library creditor {creditor.id} already exists")
        self.library_creditors[key] = creditor

    def get_library_creditor(
        self, firm_id: str, creditor_id: str
    ) -> LibraryCreditor | None:
        return self.library_creditors.get((firm_id, creditor_id))

    def list_library_creditors(self, firm_id: str) -> tuple[LibraryCreditor, ...]:
        # BY NAME, matching the DynamoDB adapter — the two stores exist to be
        # interchangeable.
        return tuple(
            sorted(
                (
                    creditor
                    for creditor in self.library_creditors.values()
                    if creditor.firm_id == firm_id
                ),
                key=lambda creditor: (creditor.name, creditor.id),
            )
        )

    def update_library_creditor(
        self, creditor: LibraryCreditor
    ) -> LibraryCreditor | None:
        key = (creditor.firm_id, creditor.id)
        if key not in self.library_creditors:
            return None
        self.library_creditors[key] = creditor
        return creditor

    def delete_library_creditor(self, firm_id: str, creditor_id: str) -> bool:
        return self.library_creditors.pop((firm_id, creditor_id), None) is not None

    # ── Firm clients ────────────────────────────────────────────────

    def create_client(self, client: FirmClient) -> None:
        key = (client.firm_id, client.id)
        if key in self.clients:
            raise RuntimeError(f"client {client.id} already exists")
        self.clients[key] = client

    def get_client(self, firm_id: str, client_id: str) -> FirmClient | None:
        return self.clients.get((firm_id, client_id))

    def list_clients(self, firm_id: str) -> tuple[FirmClient, ...]:
        # The shared ordering, so this store and DynamoDB cannot disagree.
        return sorted_firm_clients(
            client for client in self.clients.values() if client.firm_id == firm_id
        )

    def update_client(self, client: FirmClient) -> FirmClient | None:
        key = (client.firm_id, client.id)
        stored = self.clients.get(key)
        if stored is None or stored.merged_into is not None:
            return None
        # The record's own fields only: the merge attributes stay as stored,
        # as the DynamoDB adapter's UpdateItem leaves them.
        written = replace(
            client,
            merged_into=stored.merged_into,
            merging_into=stored.merging_into,
            merging_from=stored.merging_from,
        )
        self.clients[key] = written
        return written

    # ── Merging two clients ─────────────────────────────────────────
    #
    # The DynamoDB adapter's conditions, checked and applied in one step —
    # nothing runs between the lines here, which is what its transactions buy.

    def claim_client_merge(
        self, firm_id: str, *, merged_id: str, survivor_id: str
    ) -> bool:
        merged = self.clients.get((firm_id, merged_id))
        survivor = self.clients.get((firm_id, survivor_id))
        if merged is None or survivor is None or merged_id == survivor_id:
            return False
        if not _claimable(merged, survivor_id, survivor=False):
            return False
        if not _claimable(survivor, merged_id, survivor=True):
            return False
        self.clients[(firm_id, merged_id)] = replace(merged, merging_into=survivor_id)
        self.clients[(firm_id, survivor_id)] = replace(survivor, merging_from=merged_id)
        return True

    def finish_client_merge(
        self, firm_id: str, *, merged_id: str, survivor_id: str, merged_at: str
    ) -> FirmClient | None:
        merged = self.clients.get((firm_id, merged_id))
        survivor = self.clients.get((firm_id, survivor_id))
        if (
            merged is None
            or survivor is None
            or merged.merging_into != survivor_id
            or survivor.merging_from != merged_id
        ):
            return None
        archived = replace(
            merged,
            status=ARCHIVED,
            merged_into=survivor_id,
            merging_into=None,
            updated_at=merged_at,
        )
        self.clients[(firm_id, merged_id)] = archived
        self.clients[(firm_id, survivor_id)] = replace(survivor, merging_from=None)
        return archived

    def release_client_merge(
        self, firm_id: str, *, merged_id: str, survivor_id: str
    ) -> None:
        merged = self.clients.get((firm_id, merged_id))
        survivor = self.clients.get((firm_id, survivor_id))
        if merged is not None and merged.merging_into == survivor_id:
            self.clients[(firm_id, merged_id)] = replace(merged, merging_into=None)
        if survivor is not None and survivor.merging_from == merged_id:
            self.clients[(firm_id, survivor_id)] = replace(survivor, merging_from=None)


def _claimable(client: FirmClient, other_id: str, *, survivor: bool) -> bool:
    """`claim_client_merge`'s condition on one row: active, never merged,
    and not already in a merge except this same one."""
    if client.status != ACTIVE or client.merged_into is not None:
        return False
    if survivor:
        return client.merging_into is None and client.merging_from in (
            None,
            other_id,
        )
    return client.merging_from is None and client.merging_into in (None, other_id)
