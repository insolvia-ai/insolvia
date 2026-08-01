"""In-memory JwksProvider for tests and local development (issue #79).

Mirrors MemoryWaitlistStore / InMemoryMailerClient: never composed in a
deployed environment (adapters/aws/jwks_provider.py's CognitoJwksProvider
is). It is handed the keys up front and never touches the network, which is
what lets the auth tests sign tokens with a keypair generated in the test
module and have them verify for real — same code path, real RS256, no socket.
"""

from __future__ import annotations

from typing import Any

from insolvia_api.core.auth import AuthenticationError, AuthFailureReason


class StaticJwksProvider:
    """Serves a fixed {kid: key} map.

    Raises UNKNOWN_KEY for anything else, exactly as the port requires — the
    "unknown kid" path has to behave the same here as in production, or the
    test that covers it would be testing the wrong thing.
    """

    def __init__(self, keys: dict[str, Any] | None = None) -> None:
        self.keys: dict[str, Any] = dict(keys or {})

    def signing_key(self, kid: str) -> Any:
        try:
            return self.keys[kid]
        except KeyError as exc:
            raise AuthenticationError(AuthFailureReason.UNKNOWN_KEY) from exc
