"""Unsubscribe tokens (issue #80 / 6.8).

Pure — no framework or AWS imports, same dependency-direction rule as every
other `core/` module (tests/test_architecture.py).

## Why a token at all

The mailer's `POST /v1/services/<id>/suppressions` will suppress whatever
address a registered caller names; it has no way to check that the request
came from that address's owner. Supplying that proof is this API's job, and
this module is the proof: a link that carries an address plus an HMAC over it,
signed with a secret only this service holds.

Without it the unsubscribe endpoint would be an unauthenticated
"stop-sending-mail-to-anyone" button — a stranger could suppress a competitor's
address and that person would silently stop receiving password resets.

## The format

    v1.<base64url(payload)>.<base64url(hmac_sha256(secret, payload))>

    payload = "1:<issued_at unix seconds>:<lowercased address>"

Deliberate properties, each of which is a decision rather than an accident:

- **The address is readable, not encrypted.** It is base64, not a cipher, and
  that is fine: the only person who receives the link already knows their own
  address. What the HMAC buys is that nobody can *forge* a token for a
  different address, which is the property that actually matters.
- **No expiry is enforced.** An unsubscribe link that stops working is a
  compliance problem, not a security improvement — a person who kept an old
  email and clicks unsubscribe must still be unsubscribed. `issued_at` is
  carried for diagnostics and for a future "this link predates a key rotation"
  decision, and `token_age_seconds` exposes it, but `verify_token` does not
  reject on it.
- **Verification is constant-time** (`hmac.compare_digest`), so the signature
  cannot be recovered a byte at a time.
- **Every failure raises the same error with the same message.** A caller
  learns "invalid", never *which* part was invalid.
"""

from __future__ import annotations

import base64
import binascii
import hmac
import time
from hashlib import sha256

from insolvia_api.core.errors import ValidationError

TOKEN_VERSION = "v1"
_PAYLOAD_VERSION = "1"
_SEPARATOR = "."

# The mailer caps to_address at 320 characters (RFC 5321's practical limit);
# a token claiming more than that could not name a deliverable address, so
# reject it before spending an HMAC on it.
MAX_ADDRESS_CHARS = 320
MAX_TOKEN_CHARS = 1024

# One message for every rejection. Which check failed is useful to an
# attacker and useless to the person holding the link.
_INVALID = "unsubscribe token is invalid"


def _b64encode(raw: bytes) -> str:
    return base64.urlsafe_b64encode(raw).decode("ascii").rstrip("=")


def _b64decode(value: str) -> bytes:
    padding = "=" * (-len(value) % 4)
    try:
        return base64.urlsafe_b64decode(value + padding)
    except (binascii.Error, ValueError) as exc:
        raise ValidationError(_INVALID) from exc


def _payload(address: str, issued_at: int) -> bytes:
    return f"{_PAYLOAD_VERSION}:{issued_at}:{address}".encode()


def _signature(payload: bytes, secret: str) -> bytes:
    return hmac.new(secret.encode("utf-8"), payload, sha256).digest()


class UnsubscribeSecretMissing(RuntimeError):
    """No signing key is configured.

    Deliberately NOT an ApiError: every ApiError subclass maps to a 400, and a
    400 tells the caller they got something wrong when in fact the deployment
    did. This reaches app_factory's catch-all instead, which logs the
    traceback and answers 500 — loud, in the logs, and not the caller's fault.
    """


def _require_secret(secret: str | None) -> str:
    if not secret:
        # A missing secret must never degrade to "no signature required" —
        # that would turn every token into a valid one.
        raise UnsubscribeSecretMissing("unsubscribe secret is not configured")
    return secret


def normalize_address(address: str) -> str:
    """Lower-case and strip, matching the mailer's `recipient_hash`.

    The mailer hashes `address.strip().lower()`, so a token minted for
    `Alex@Firm.com` and one minted for `alex@firm.com` must resolve to the
    same suppression entry. Normalizing here rather than at the boundary
    keeps mint and verify agreeing by construction.
    """
    return address.strip().lower()


def mint_token(address: str, *, secret: str, issued_at: int | None = None) -> str:
    """Build a token proving the holder was sent mail at `address`."""
    secret = _require_secret(secret)
    normalized = normalize_address(address)
    if not normalized or len(normalized) > MAX_ADDRESS_CHARS:
        raise ValidationError("address is not a valid unsubscribe subject")
    if ":" in normalized:
        # The payload is colon-delimited; an address containing one would let
        # a caller shift the field boundaries and claim a different address.
        raise ValidationError("address is not a valid unsubscribe subject")
    stamp = int(time.time()) if issued_at is None else issued_at
    payload = _payload(normalized, stamp)
    return _SEPARATOR.join(
        [TOKEN_VERSION, _b64encode(payload), _b64encode(_signature(payload, secret))]
    )


def verify_token(token: str, *, secret: str) -> str:
    """Return the address a valid token names, or raise ValidationError.

    Never returns a partially trusted result: either the signature checks out
    and the address is returned, or nothing is.
    """
    secret = _require_secret(secret)
    if not isinstance(token, str) or not 0 < len(token) <= MAX_TOKEN_CHARS:
        raise ValidationError(_INVALID)

    parts = token.split(_SEPARATOR)
    if len(parts) != 3:
        raise ValidationError(_INVALID)
    version, encoded_payload, encoded_signature = parts
    if version != TOKEN_VERSION:
        raise ValidationError(_INVALID)

    payload = _b64decode(encoded_payload)
    if not hmac.compare_digest(
        _signature(payload, secret), _b64decode(encoded_signature)
    ):
        raise ValidationError(_INVALID)

    # Only now is the payload trusted enough to parse. Doing it before the
    # signature check would run the parser on attacker-chosen bytes.
    try:
        payload_version, _issued_at, address = payload.decode("utf-8").split(":", 2)
    except (UnicodeDecodeError, ValueError) as exc:
        raise ValidationError(_INVALID) from exc
    if payload_version != _PAYLOAD_VERSION or not address:
        raise ValidationError(_INVALID)
    return address


def token_age_seconds(token: str, *, secret: str, now: int | None = None) -> int:
    """How long ago a token was minted. Verifies first — an unsigned token has
    no age worth reporting. Not used to reject anything today; see the module
    docstring on why unsubscribe links do not expire."""
    verify_token(token, secret=secret)
    payload = _b64decode(token.split(_SEPARATOR)[1]).decode("utf-8")
    issued_at = int(payload.split(":", 2)[1])
    return (int(time.time()) if now is None else now) - issued_at


def unsubscribe_url(address: str, *, secret: str, marketing_origin: str) -> str:
    """The link that goes in an email footer and its List-Unsubscribe header.

    Points at the marketing site rather than this API on purpose: a person
    clicking it needs a page, and per docs/adr/0001 the marketing site is a
    dumb client that forwards to this API. The whole path is
    marketing /unsubscribe -> API POST /v1/unsubscribe -> mailer suppressions.
    """
    token = mint_token(address, secret=secret)
    return f"{marketing_origin.rstrip('/')}/unsubscribe?token={token}"
