from __future__ import annotations

from insolvia_admin.core.audit import AdminEvent


class MemoryAuditLog:
    """Ephemeral AuditLog for tests and the plain development server.

    `events` is public so a test can assert that a mutating route wrote its
    row — the audit write is part of every mutation's contract (#178), and a
    suite that could not see it would be testing half the route.
    """

    def __init__(self) -> None:
        self.events: list[AdminEvent] = []

    def record(self, event: AdminEvent) -> None:
        self.events.append(event)
