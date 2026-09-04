"""The ports only this service composes.

The case-domain ports — CaseStore, CaseEntityStore, DebtorStore,
DocumentStore, AccessLog — live in `insolvia_core.ports` with the domain they
serve (ADR 0016). The candidate store stays here for the admission-rule
reason `core/candidates.py` gives: one composer today, graduating to the core
package when the review flow (8.9) becomes its second.
"""

from __future__ import annotations

from typing import Protocol

from insolvia_mcp.core.candidates import Candidate


class CandidateStore(Protocol):
    """Persists candidate records (mcp-surface.md § Candidate writes).

    Ownership is NOT a parameter here, the same rule every case-child store
    states: a candidate is reached only through its case, the tool layer
    resolves the case through `CaseStore` first on every path, and a second
    authorisation path here would eventually disagree with the first. What
    every method DOES enforce is the case scope: `case_id` is half the key,
    so a candidate id from another case does not resolve here.
    """

    def create(self, candidate: Candidate) -> None:
        """Store a new row. Ids are server-minted uuid4s, so an existing
        (case, id) means the minting is broken — implementations MUST raise
        rather than silently replace, exactly as CaseEntityStore.create
        does."""
        ...

    def get(self, case_id: str, candidate_id: str) -> Candidate | None: ...

    def list_for_case(self, case_id: str) -> tuple[Candidate, ...]:
        """Every candidate of one case, in creation order
        (core/candidates.list_order — the sort key is a random uuid, so
        neither implementation gets the ordering for free). All of them; the
        tool layer paginates, and status filtering happens above this port so
        both implementations cannot drift on what a filter means."""
        ...

    def update(self, candidate: Candidate, *, expected_status: str) -> Candidate | None:
        """Write `candidate` back, but only if the stored status is still
        `expected_status` — the compare-and-swap withdrawal rides on. None
        means the condition failed (or the row is gone): the caller lost a
        race with the reviewer and must not pretend otherwise — a withdrawal
        that overwrote an acceptance would silently un-review a record the
        human just confirmed."""
        ...
