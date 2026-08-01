from __future__ import annotations

from typing import Any, Protocol

from insolvia_api.core.mail import OutboundEmail
from insolvia_api.core.waitlist import WaitlistRecord


class JwksProvider(Protocol):
    """Supplies the public key a JWT's `kid` names (issue #79).

    This port exists so `core/auth.py` can stay pure. Verification needs a
    key; fetching one needs the network, and `core` may not have it. The real
    implementation (adapters/aws/jwks_provider.py) reads the Cognito pool's
    `<issuer>/.well-known/jwks.json` over stdlib urllib and caches by `kid`;
    the static one (adapters/memory/jwks_provider.py) is handed keys directly
    and is what the tests sign against.

    Implementations MUST raise `insolvia_api.core.auth.AuthenticationError`
    with `AuthFailureReason.UNKNOWN_KEY` for a `kid` they cannot resolve —
    including after a refresh. Returning None or a placeholder would push a
    "no key" case into the verifier, where the safe branch is easy to miss.
    """

    def signing_key(self, kid: str) -> Any:
        """The key for `kid`, in whatever form PyJWT's `decode` accepts."""
        ...


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
