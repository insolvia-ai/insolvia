"""The Cognito pool's JWKS, fetched and cached (issue #79 / 7.4).

The pool publishes its RSA public keys at `<issuer>/.well-known/jwks.json`.
That document is public, unauthenticated, and essentially static — Cognito
mints two keys per pool and rotates rarely — so this adapter fetches it once
and serves every subsequent verification from memory.

**stdlib `urllib.request`, not `requests`** — the same convention
adapters/aws/mailer_client.py sets explicitly. No new dependency for one GET.

## The caching rules, and the reason for each

- **Cache the whole document, keyed by `kid`, for `ttl_seconds`.** Without a
  cache every authenticated request becomes an outbound HTTPS call in the
  Lambda's request path: latency on every call and a hard dependency on
  Cognito's availability for reads that need none.
- **An unknown `kid` triggers at most one refetch, and no more often than
  `min_refresh_seconds`.** Rotation has to be picked up without a redeploy,
  but "unknown kid" is exactly what a forged token carries, and a refetch per
  bogus token is a free way for an anonymous caller to make this service
  hammer Cognito. The floor turns that into one request per interval.
- **A still-unknown `kid` raises UNKNOWN_KEY.** It never falls through to
  "verify without a key".
- **A failed fetch never clears a good cache.** A Cognito blip must not log
  every signed-in user out; stale keys still verify correctly (they were
  valid when fetched, and the tokens they signed have their own `exp`).
"""

from __future__ import annotations

import json
import time
import urllib.request
from typing import Any

import jwt

from insolvia_core.auth import AuthenticationError, AuthFailureReason

# Cognito rotates pool keys rarely; an hour bounds how long a rotated-out key
# lingers without making the happy path chatty.
DEFAULT_TTL_SECONDS = 3600.0

# At most one JWKS fetch per this interval when an unknown kid arrives — the
# rate limit that stops forged tokens becoming an amplifier.
DEFAULT_MIN_REFRESH_SECONDS = 60.0

# The document is a few hundred bytes of JSON. Anything remotely near this is
# not a JWKS, and reading it unbounded would be a memory DoS on a Lambda.
MAX_DOCUMENT_BYTES = 256 * 1024

DEFAULT_TIMEOUT_SECONDS = 3.0


class CognitoJwksProvider:
    """JwksProvider backed by the pool's published JWKS document."""

    def __init__(
        self,
        issuer_url: str,
        *,
        ttl_seconds: float = DEFAULT_TTL_SECONDS,
        min_refresh_seconds: float = DEFAULT_MIN_REFRESH_SECONDS,
        timeout_seconds: float = DEFAULT_TIMEOUT_SECONDS,
    ) -> None:
        self.jwks_url = f"{issuer_url.rstrip('/')}/.well-known/jwks.json"
        self.ttl_seconds = ttl_seconds
        self.min_refresh_seconds = min_refresh_seconds
        self.timeout_seconds = timeout_seconds
        self._keys: dict[str, Any] = {}
        self._fetched_at: float | None = None

    def signing_key(self, kid: str) -> Any:
        if self._is_stale():
            self._refresh()
        key = self._keys.get(kid)
        if key is not None:
            return key
        # Unknown kid on a warm cache: possibly a rotation, possibly a forged
        # token. Rate-limited refetch decides which.
        if self._may_refresh():
            self._refresh()
            key = self._keys.get(kid)
            if key is not None:
                return key
        raise AuthenticationError(AuthFailureReason.UNKNOWN_KEY)

    def _is_stale(self) -> bool:
        return (
            self._fetched_at is None
            or (time.monotonic() - self._fetched_at) >= self.ttl_seconds
        )

    def _may_refresh(self) -> bool:
        return (
            self._fetched_at is None
            or (time.monotonic() - self._fetched_at) >= self.min_refresh_seconds
        )

    def _refresh(self) -> None:
        """Replace the cache, or leave the previous one exactly as it was.

        The timestamp advances even on failure: that is what rate-limits the
        retry, and it is why an outage degrades to "keep serving the keys we
        have" instead of "hammer Cognito on every request".
        """
        try:
            document = self._fetch()
        except Exception:
            self._fetched_at = time.monotonic()
            if not self._keys:
                # Nothing cached and nothing fetched — there is no key to
                # verify with, so every token is unverifiable. Fail closed.
                raise AuthenticationError(AuthFailureReason.UNKNOWN_KEY) from None
            return
        self._keys = document
        self._fetched_at = time.monotonic()

    def _fetch(self) -> dict[str, Any]:
        request = urllib.request.Request(self.jwks_url, method="GET")
        # The URL is built from the configured issuer, never from request
        # input, so there is no scheme or host an attacker can choose.
        with urllib.request.urlopen(request, timeout=self.timeout_seconds) as response:
            raw = response.read(MAX_DOCUMENT_BYTES + 1)
        if len(raw) > MAX_DOCUMENT_BYTES:
            raise ValueError("JWKS document is implausibly large")
        payload = json.loads(raw.decode("utf-8"))
        return parse_jwks(payload)


def parse_jwks(payload: Any) -> dict[str, Any]:
    """Turn a JWKS document into {kid: PyJWK}, skipping anything unusable.

    Keys with no `kid`, or that PyJWT cannot construct (an unsupported `kty`,
    a malformed modulus), are dropped rather than failing the whole document
    — one bad entry must not take down verification for the good ones.
    """
    if not isinstance(payload, dict):
        raise ValueError("JWKS document is not a JSON object")
    entries = payload.get("keys")
    if not isinstance(entries, list):
        raise ValueError("JWKS document has no `keys` array")

    keys: dict[str, Any] = {}
    for entry in entries:
        if not isinstance(entry, dict):
            continue
        kid = entry.get("kid")
        if not isinstance(kid, str) or not kid:
            continue
        try:
            keys[kid] = jwt.PyJWK(entry)
        except jwt.PyJWTError:
            continue
    if not keys:
        raise ValueError("JWKS document contained no usable keys")
    return keys
