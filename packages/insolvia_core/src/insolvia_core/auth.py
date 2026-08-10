"""Cognito access-token verification (issue #79 / 7.4).

Pure — no framework, no AWS, no network. `jwt` is imported here on purpose:
PyJWT is a crypto library, and the signature check *is* the domain rule. What
this module deliberately does NOT do is go and fetch a key: the signing key
arrives as an argument, and finding it is an adapter's job
(`core.ports.JwksProvider`, implemented in `adapters/aws/jwks_provider.py`).
That
keeps `tests/test_architecture.py`'s "core depends on nothing" honest, and it
keeps the security-relevant logic testable without a socket.

## What is verified, and why exactly these things

The client sends the Cognito **access token**, not the ID token. Cognito's
access tokens carry `client_id`; they carry **no `aud`** — so `aud`
verification is switched off and the app-client check is done against
`client_id` instead. Getting this wrong in the other direction (verifying
`aud` on an access token) fails every valid token; getting it wrong by
skipping the check entirely accepts tokens minted for a *different* app
client in the same pool.

Every check below is load-bearing:

- **RS256 signature** against the pool's JWKS, keyed by the header `kid`.
  Only asymmetric algorithms are accepted — an `alg: none` or an HS256 token
  signed with the (public) modulus is rejected before any key lookup.
- **`iss`** equals the configured issuer. A token from another Cognito pool
  is a valid signature over someone else's users.
- **`token_use == "access"`**. An ID token from the same pool is signed by the
  same key and would otherwise sail through; it is not an authorization
  credential and is not what this API accepts.
- **`client_id`** equals the configured app client.
- **`exp`**, plus `nbf`/`iat` when present. PyJWT enforces these.
- **`sub`** is present and non-empty — it is the principal's identity, and a
  token without one authenticates nobody.

## What it does not do

No call to Cognito. Identity is derived purely from verified claims. The pool
uses `username_attributes = ["email"]`, so `username` is a Cognito-generated
UUID and there is **no email in an access token** — that is expected. The app
already holds the ID token and displays the email from there.

## Failure shape

One exception type, `AuthenticationError`, carrying a coarse `reason`
category (`AuthFailureReason`) for logging. The reason is a fixed enum member,
never token content, so a log line can say *why* verification failed without
ever recording the token, a claim, or PII (GLBA).
"""

from __future__ import annotations

from collections.abc import Mapping
from dataclasses import dataclass
from enum import Enum
from typing import Any

import jwt

# Cognito signs with RS256 and only RS256. Listing it explicitly is what stops
# an attacker downgrading to `none`, or to HS256 with the public key as the
# shared secret.
ALGORITHMS = ("RS256",)

TOKEN_USE_ACCESS = "access"

_BEARER_PREFIX = "bearer"


class AuthFailureReason(Enum):
    """Why verification failed, at a granularity that is safe to log.

    Categories only — deliberately coarse. A log line records the member
    name; it must never record the token, a claim value, or anything derived
    from them.
    """

    MISSING_CREDENTIALS = "missing_credentials"
    MALFORMED_HEADER = "malformed_header"
    MALFORMED_TOKEN = "malformed_token"
    UNKNOWN_KEY = "unknown_key"
    INVALID_SIGNATURE = "invalid_signature"
    EXPIRED = "expired"
    INVALID_ISSUER = "invalid_issuer"
    INVALID_CLIENT = "invalid_client"
    WRONG_TOKEN_USE = "wrong_token_use"
    INVALID_CLAIMS = "invalid_claims"
    NOT_CONFIGURED = "not_configured"


class AuthenticationError(Exception):
    """The request carried no usable proof of identity.

    Deliberately NOT an ApiError: every ApiError subclass maps to a 400, and
    this is a 401. The API layer catches it explicitly and answers 401 with a
    generic body — the caller learns "unauthorized", never which check failed.
    """

    def __init__(self, reason: AuthFailureReason) -> None:
        super().__init__(f"authentication failed: {reason.value}")
        self.reason = reason


@dataclass(frozen=True)
class AuthSettings:
    """The two values that decide whether a token is ours.

    Built from AppConfig at composition time. Both are required — see
    `settings_or_raise`; there is no "unset means allow".
    """

    issuer_url: str
    client_id: str


@dataclass(frozen=True)
class Principal:
    """The authenticated caller, derived purely from verified claims.

    - `subject` is `sub`: the pool's stable, immutable user identifier. This
      is the only thing that should ever key user-owned data.
    - `username` is Cognito's `username`. With
      `username_attributes = ["email"]` on the pool this is a generated UUID,
      *not* an email address — do not display it and do not treat it as one.
    - `client_id` and `scopes` are carried for auditing and for future
      per-scope authorization; nothing enforces scopes today.
    """

    subject: str
    username: str | None
    client_id: str
    scopes: tuple[str, ...]
    expires_at: int | None


def settings_or_raise(issuer_url: str | None, client_id: str | None) -> AuthSettings:
    """Require both settings, or refuse to verify anything.

    Missing configuration must never degrade to "allow the request" — the same
    rule UNSUBSCRIBE_SECRET follows. A protected route on a deployment with no
    auth configured fails closed (401), it does not open.
    """
    if not issuer_url or not client_id:
        raise AuthenticationError(AuthFailureReason.NOT_CONFIGURED)
    return AuthSettings(issuer_url=issuer_url.rstrip("/"), client_id=client_id)


def bearer_token(header_value: str | None) -> str:
    """Pull the token out of an `Authorization: Bearer <jwt>` header.

    Absent header and malformed header are different reasons on purpose: the
    first is "you sent no credentials", the second is "you sent something that
    is not a credential". Both answer 401; only the log line tells them apart.
    """
    if header_value is None or not header_value.strip():
        raise AuthenticationError(AuthFailureReason.MISSING_CREDENTIALS)
    parts = header_value.split()
    if len(parts) != 2 or parts[0].lower() != _BEARER_PREFIX or not parts[1]:
        raise AuthenticationError(AuthFailureReason.MALFORMED_HEADER)
    return parts[1]


def key_id(token: str) -> str:
    """The `kid` from the token's (still unverified) header.

    Reading the header before verifying is unavoidable — it is how the right
    key is found — so nothing here trusts it beyond "which key to try". The
    `alg` is checked too: an attacker-chosen algorithm must be rejected
    before a key lookup, not after.
    """
    try:
        header = jwt.get_unverified_header(token)
    except jwt.PyJWTError as exc:
        raise AuthenticationError(AuthFailureReason.MALFORMED_TOKEN) from exc
    kid = header.get("kid")
    if not isinstance(kid, str) or not kid:
        raise AuthenticationError(AuthFailureReason.MALFORMED_TOKEN)
    if header.get("alg") not in ALGORITHMS:
        raise AuthenticationError(AuthFailureReason.INVALID_SIGNATURE)
    return kid


def verify_access_token(
    token: str, *, signing_key: Any, settings: AuthSettings
) -> Principal:
    """Verify `token` against `signing_key` and return the principal.

    `signing_key` is whatever the JWKS provider produced for the token's
    `kid` (a `jwt.PyJWK`, or any key PyJWT accepts). Fetching it is the
    adapter's job; this function only decides whether the result is
    trustworthy.
    """
    try:
        claims: Mapping[str, Any] = jwt.decode(
            token,
            key=signing_key,
            algorithms=list(ALGORITHMS),
            issuer=settings.issuer_url,
            options={
                # Cognito ACCESS tokens carry client_id, never aud. Verifying
                # aud here would reject every valid token; the app-client
                # check happens below against client_id instead.
                "verify_aud": False,
                "require": ["exp", "iss", "sub", "token_use", "client_id"],
                "verify_exp": True,
                "verify_iat": True,
                "verify_nbf": True,
                "verify_signature": True,
            },
        )
    except jwt.ExpiredSignatureError as exc:
        raise AuthenticationError(AuthFailureReason.EXPIRED) from exc
    except jwt.InvalidIssuerError as exc:
        raise AuthenticationError(AuthFailureReason.INVALID_ISSUER) from exc
    except jwt.InvalidSignatureError as exc:
        raise AuthenticationError(AuthFailureReason.INVALID_SIGNATURE) from exc
    except jwt.MissingRequiredClaimError as exc:
        raise AuthenticationError(AuthFailureReason.INVALID_CLAIMS) from exc
    except jwt.PyJWTError as exc:
        # DecodeError, ImmatureSignatureError, InvalidAlgorithmError, and
        # anything PyJWT adds later. Everything unrecognised is a rejection.
        raise AuthenticationError(AuthFailureReason.INVALID_SIGNATURE) from exc

    return principal_from_claims(claims, settings=settings)


def principal_from_claims(
    claims: Mapping[str, Any], *, settings: AuthSettings
) -> Principal:
    """The claim checks PyJWT does not do, then the identity itself.

    Split out from `verify_access_token` so it can be read (and tested) as
    what it is: the rules that are specific to *Cognito access tokens for
    this app client*, as opposed to generic JWT validity.
    """
    if claims.get("token_use") != TOKEN_USE_ACCESS:
        # An ID token from the same pool is signed by the same key and would
        # otherwise verify. It is not an authorization credential.
        raise AuthenticationError(AuthFailureReason.WRONG_TOKEN_USE)

    client_id = claims.get("client_id")
    if not isinstance(client_id, str) or client_id != settings.client_id:
        raise AuthenticationError(AuthFailureReason.INVALID_CLIENT)

    subject = claims.get("sub")
    if not isinstance(subject, str) or not subject:
        raise AuthenticationError(AuthFailureReason.INVALID_CLAIMS)

    username = claims.get("username")
    expires_at = claims.get("exp")

    return Principal(
        subject=subject,
        username=username if isinstance(username, str) and username else None,
        client_id=client_id,
        scopes=_scopes(claims.get("scope")),
        expires_at=expires_at if isinstance(expires_at, int) else None,
    )


def _scopes(raw: Any) -> tuple[str, ...]:
    """Cognito's `scope` is a single space-delimited string, not a list."""
    if not isinstance(raw, str):
        return ()
    return tuple(raw.split())
