from __future__ import annotations

from insolvia_api.core.filing_approval import FilingApproval, filing_job_message


class MemoryFilingQueue:
    """Ephemeral FilingQueue for tests and the plain development server.

    Records the exact WIRE message — `filing_job_message`'s dict — so a test
    asserts what would actually cross the seam, MemoryJobQueue's rule. It
    files nothing; nothing consumes a filing job until services/filing (ADR
    0024 PR 7). `fail` makes the next enqueue raise, for the enqueue-failed
    path."""

    def __init__(self) -> None:
        self.messages: list[dict[str, object]] = []
        self.fail = False

    def enqueue(self, approval: FilingApproval) -> None:
        if self.fail:
            raise RuntimeError("the memory filing queue was told to fail")
        self.messages.append(filing_job_message(approval))
