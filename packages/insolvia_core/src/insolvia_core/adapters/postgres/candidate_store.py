"""CandidateStore over Postgres (ADR 0030): the `candidates` table."""

from __future__ import annotations

from insolvia_core.adapters.postgres.children import ChildTable
from insolvia_core.candidates import (
    Candidate,
    candidate_from_item,
    candidate_item,
    list_order,
)


class PostgresCandidateStore:
    def __init__(self, database_url: str) -> None:
        self.rows = ChildTable(database_url, "candidates", ("case_id", "id"))

    def create(self, candidate: Candidate) -> None:
        self.rows.create(
            {"case_id": candidate.case_id, "id": candidate.id},
            candidate_item(candidate),
        )

    def get(self, case_id: str, candidate_id: str) -> Candidate | None:
        item = self.rows.get({"case_id": case_id, "id": candidate_id})
        return None if item is None else candidate_from_item(item)

    def list_for_case(self, case_id: str) -> tuple[Candidate, ...]:
        items = self.rows.list({"case_id": case_id})
        return tuple(sorted(map(candidate_from_item, items), key=list_order))

    def update(self, candidate: Candidate, *, expected_status: str) -> Candidate | None:
        written = self.rows.replace(
            {"case_id": candidate.case_id, "id": candidate.id},
            candidate_item(candidate),
            expected_status=expected_status,
        )
        return candidate if written else None
