from __future__ import annotations

from insolvia_api.core.cases import (
    Case,
    CasePage,
    decode_cursor,
    encode_cursor,
)


class MemoryCaseStore:
    """Ephemeral CaseStore for tests and the plain development server.

    It enforces ownership and orders results exactly as the DynamoDB adapter
    does, because a test suite running against a store with weaker rules than
    production would pass on code that leaks other firms' cases.
    """

    def __init__(self) -> None:
        self.cases: dict[str, Case] = {}

    def create(self, case: Case) -> None:
        self.cases[case.id] = case

    def get(self, case_id: str, *, owner_principal: str) -> Case | None:
        case = self.cases.get(case_id)
        if case is None or case.owner_principal != owner_principal:
            return None
        return case

    def list_for_owner(
        self, owner_principal: str, *, limit: int, cursor: str | None
    ) -> CasePage:
        # Mirrors the GSI: filtered to the owner, sorted by the same
        # "<createdAt>#<id>" value, newest first.
        ordered = sorted(
            (
                case
                for case in self.cases.values()
                if case.owner_principal == owner_principal
            ),
            key=lambda case: f"{case.created_at}#{case.id}",
            reverse=True,
        )
        if cursor is not None:
            after = decode_cursor(cursor).get("GSI1SK", "")
            ordered = [
                case for case in ordered if f"{case.created_at}#{case.id}" < after
            ]

        page = ordered[:limit]
        next_cursor = None
        if len(ordered) > limit and page:
            last = page[-1]
            next_cursor = encode_cursor({"GSI1SK": f"{last.created_at}#{last.id}"})
        return CasePage(cases=tuple(page), next_cursor=next_cursor)

    def update(self, case: Case) -> Case | None:
        existing = self.cases.get(case.id)
        if existing is None or existing.owner_principal != case.owner_principal:
            return None
        self.cases[case.id] = case
        return case
