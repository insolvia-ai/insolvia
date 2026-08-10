from __future__ import annotations

from insolvia_core.firms import Firm, FirmUser


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
        return tuple(
            sorted(
                (user for user in self.users.values() if user.firm_id == firm_id),
                key=lambda user: (user.display_name, user.subject),
            )
        )

    def update_user(self, user: FirmUser) -> FirmUser | None:
        if (user.firm_id, user.subject) not in self.users:
            return None
        self.users[(user.firm_id, user.subject)] = user
        return user

    def remove_user(self, firm_id: str, subject: str) -> bool:
        return self.users.pop((firm_id, subject), None) is not None
