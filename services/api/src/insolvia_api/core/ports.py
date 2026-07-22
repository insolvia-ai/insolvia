from __future__ import annotations

from typing import Protocol

from insolvia_api.core.waitlist import WaitlistRecord


class WaitlistStore(Protocol):
    """Persists waitlist submissions. Implemented by adapters/aws (DynamoDB)
    and adapters/memory (tests and the plain development server)."""

    def add(self, record: WaitlistRecord) -> None: ...
