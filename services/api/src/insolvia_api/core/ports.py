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

    def suppress(self, address: str, *, reason: str) -> None:
        """Stop sending to `address` (issue #80).

        Writes to the mailer's suppression store — the same one the SES
        feedback path fills from bounces and complaints, and the one the
        sender checks before every send. Idempotent: suppressing an already
        suppressed address succeeds.

        This port takes no proof of ownership, and neither does the mailer
        endpoint behind it. Establishing that the request came from the
        address's owner happens *before* this call, in the unsubscribe route,
        by verifying the HMAC token from the link (core/unsubscribe.py).
        Calling this without doing that would be a
        suppress-anyone-you-like button.
        """
        ...
