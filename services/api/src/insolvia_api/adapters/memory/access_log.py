from __future__ import annotations

from insolvia_api.core.access_log import AccessEvent


class MemoryAccessLog:
    """Ephemeral AccessLog for tests and the plain development server.

    Unlike the real one this keeps what it recorded, so tests can assert that
    a read was logged. That is a test affordance and not a capability the
    service has in a deployed environment, where IAM permits PutItem alone.
    """

    def __init__(self) -> None:
        self.events: list[AccessEvent] = []

    def record(self, event: AccessEvent) -> None:
        self.events.append(event)
