from __future__ import annotations

import logging

from insolvia_api.core.waitlist import WaitlistRecord, record_item

logger = logging.getLogger("insolvia_api.development")


class MemoryWaitlistStore:
    """Ephemeral WaitlistStore for tests and the plain development server.

    With echo=True (the development server) each stored item — including its
    field values — is logged so local marketing-site dev can see submissions
    arrive. That is deliberately local-only: this adapter is never composed
    in a deployed environment, where the no-PII logging rule
    (insolvia_api.core.logging) holds absolutely.
    """

    def __init__(self, *, echo: bool = False) -> None:
        self.records: list[WaitlistRecord] = []
        self.echo = echo

    def add(self, record: WaitlistRecord) -> None:
        self.records.append(record)
        if self.echo:
            logger.info(
                "waitlist submission (memory store, not persisted)",
                extra={"item": record_item(record)},
            )
