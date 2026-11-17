from __future__ import annotations

from insolvia_api.core.mail import OutboundEmail


class InMemoryMailerClient:
    """Ephemeral Mailer for tests and the plain development server.

    Mirrors MemoryWaitlistStore: never composed in a deployed environment
    (adapters/aws/mailer_client.py's SigV4MailerClient is), so it never
    actually sends anything — it just records what would have been sent.
    """

    def __init__(self) -> None:
        self.sent: list[tuple[OutboundEmail, str]] = []
        self.suppressed: list[tuple[str, str]] = []

    def send(self, email: OutboundEmail, *, idempotency_key: str) -> None:
        self.sent.append((email, idempotency_key))

    def suppress(self, address: str, *, reason: str) -> None:
        self.suppressed.append((address, reason))
