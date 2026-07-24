from __future__ import annotations

from typing import Protocol

from insolvia_api.core.mail import OutboundEmail
from insolvia_api.core.waitlist import WaitlistRecord


class WaitlistStore(Protocol):
    """Persists waitlist submissions. Implemented by adapters/aws (DynamoDB)
    and adapters/memory (tests and the plain development server)."""

    def add(self, record: WaitlistRecord) -> None: ...


class Mailer(Protocol):
    """Sends transactional mail through the mailer service (issue 6.4).

    Implemented by adapters/aws/mailer_client.py's SigV4MailerClient
    (production) and adapters/memory/mailer_client.py's InMemoryMailerClient
    (tests and the plain development server).
    """

    def send(self, email: OutboundEmail, *, idempotency_key: str) -> None:
        """Send `email`. `idempotency_key` becomes the mailer contract's
        `application_message_id` — callers supply a stable key so retries of
        the same logical send (e.g. a Lambda retry) dedupe on the mailer
        side rather than emailing the recipient twice."""
        ...
