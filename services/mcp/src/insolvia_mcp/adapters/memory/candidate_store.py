from __future__ import annotations

from insolvia_mcp.core.candidates import Candidate, list_order


class MemoryCandidateStore:
    """Ephemeral CandidateStore for tests and the plain development server.

    Keyed by (case_id, id) — the DynamoDB adapter's PK and SK split apart —
    so the case scope is a property of this dict rather than something every
    caller has to remember.
    """

    def __init__(self) -> None:
        self.candidates: dict[tuple[str, str], Candidate] = {}

    def create(self, candidate: Candidate) -> None:
        key = (candidate.case_id, candidate.id)
        if key in self.candidates:
            # The Protocol's contract: an existing (case, id) means the
            # server's id minting is broken, and replacing would erase a row
            # to hide it.
            raise RuntimeError("candidate id already exists in this case")
        self.candidates[key] = candidate

    def get(self, case_id: str, candidate_id: str) -> Candidate | None:
        return self.candidates.get((case_id, candidate_id))

    def list_for_case(self, case_id: str) -> tuple[Candidate, ...]:
        return tuple(
            sorted(
                (
                    candidate
                    for (stored_case_id, _), candidate in self.candidates.items()
                    if stored_case_id == case_id
                ),
                key=list_order,
            )
        )

    def update(self, candidate: Candidate, *, expected_status: str) -> Candidate | None:
        key = (candidate.case_id, candidate.id)
        stored = self.candidates.get(key)
        if stored is None or stored.status != expected_status:
            return None
        self.candidates[key] = candidate
        return candidate
