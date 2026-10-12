from __future__ import annotations

from insolvia_core.clients import LIVE, REVOKED, ClientBinding
from insolvia_core.errors import ConflictError


class MemoryClientBindingStore:
    """Ephemeral ClientBindingStore for tests and the plain development server.

    Enforces the two conditions the DynamoDB transaction does — one live
    binding per subject per firm, one holder per role per case — and checks
    ALL of them before applying ANY write, because a fake that half-applied a
    refused transaction would let a suite pass on code the real store rejects.

    The three dicts are the three kinds of item insolvia_core.clients
    describes; `claims` is public so a test can see who holds a role.
    """

    def __init__(self) -> None:
        self.bindings: dict[tuple[str, str], ClientBinding] = {}
        self.mirrors: dict[tuple[str, str], ClientBinding] = {}
        self.claims: dict[tuple[str, str], str] = {}

    def bind(
        self, binding: ClientBinding, *, narrowed: tuple[ClientBinding, ...]
    ) -> None:
        previous = self.bindings.get((binding.firm_id, binding.subject))
        if previous is not None and previous.status != REVOKED:
            raise ConflictError(
                "that person already has portal access to a case; revoke it "
                "before inviting them again"
            )
        allowed = {binding.subject, *(other.subject for other in narrowed)}
        for role in binding.roles:
            holder = self.claims.get((binding.case_id, role))
            if holder is not None and holder not in allowed:
                raise ConflictError("another client already answers for that debtor")
        for other in narrowed:
            current = self.bindings.get((other.firm_id, other.subject))
            if current is None or current.status not in LIVE:
                raise ConflictError("a binding changed while this one was written")

        self.bindings[(binding.firm_id, binding.subject)] = binding
        self.mirrors[(binding.case_id, binding.subject)] = binding
        for other in narrowed:
            self.bindings[(other.firm_id, other.subject)] = other
            self.mirrors[(other.case_id, other.subject)] = other
        for role in binding.roles:
            self.claims[(binding.case_id, role)] = binding.subject

    def update(self, binding: ClientBinding) -> ClientBinding | None:
        key = (binding.firm_id, binding.subject)
        if key not in self.bindings:
            return None
        self.bindings[key] = binding
        self.mirrors[(binding.case_id, binding.subject)] = binding
        if binding.status == REVOKED:
            for role in binding.roles:
                if self.claims.get((binding.case_id, role)) == binding.subject:
                    del self.claims[(binding.case_id, role)]
        return binding

    def get(self, firm_id: str, subject: str) -> ClientBinding | None:
        return self.bindings.get((firm_id, subject))

    def find(self, subject: str) -> ClientBinding | None:
        found = [b for (_, s), b in self.bindings.items() if s == subject]
        if len(found) > 1:
            raise RuntimeError(
                "a client resolves to more than one firm; refusing to guess"
            )
        return found[0] if found else None

    def find_by_email(self, firm_id: str, email: str) -> ClientBinding | None:
        for (firm, _), binding in self.bindings.items():
            if firm == firm_id and binding.email == email.lower():
                return binding
        return None

    def list_for_case(self, case_id: str) -> tuple[ClientBinding, ...]:
        found = [b for (case, _), b in self.mirrors.items() if case == case_id]
        return tuple(sorted(found, key=lambda b: (b.created_at, b.subject)))
