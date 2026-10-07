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

from insolvia_core.errors import ForbiddenError

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
    # `auth_time`: when the person last actually signed in (typed a password
    # at the pool), in epoch seconds. NOT the token's age — Cognito keeps the
    # original `auth_time` on every token a refresh mints, which is exactly
    # why it can answer "did they just re-authenticate?" when `iat` cannot.
    # None when the token carries no usable claim; `require_recent_
    # authentication` treats that as stale, never as fresh.
    authenticated_at: int | None = None


def settings_or_raise(issuer_url: str | None, client_id: str | None) -> AuthSettings:
    """Require both settings, or refuse to verify anything.

    Missing configuration must never degrade to "allow the request" — the same
    rule UNSUBSCRIBE_SECRET follows. A protected route on a deployment with no
    auth configured fails closed (401), it does not open.
    """
    if not issuer_url or not client_id:
        raise AuthenticationError(AuthFailureReason.NOT_CONFIGURED)
    return AuthSettings(issuer_url=issuer_url.rstrip("/"), client_id=client_id)


def portal_settings_or_raise(
    issuer_url: str | None,
    portal_client_id: str | None,
    *,
    staff_client_id: str | None,
) -> AuthSettings:
    """The client-portal verify profile (ADR 0023): the same pool's issuer,
    the portal's OWN app client — and a refusal to verify anything when that
    client is not disjoint from the staff one.

    Everything else about the check is `verify_access_token` unchanged:
    signature, issuer, `token_use == access`, and `client_id` compared for
    EQUALITY against exactly one id. Cognito access tokens carry no `aud`, so
    "this token was issued for the portal" is "this token names the portal's
    client" — which is only an audience check if the two ids differ. A
    deployment configured with the same id twice would let every staff token
    through every portal route and back; that is a misconfiguration to fail
    closed on, not a state to serve. Missing staff config does not excuse it:
    disjointness cannot be shown against a value that is not there.
    """
    if not portal_client_id or not staff_client_id:
        raise AuthenticationError(AuthFailureReason.NOT_CONFIGURED)
    if portal_client_id == staff_client_id:
        raise AuthenticationError(AuthFailureReason.NOT_CONFIGURED)
    return settings_or_raise(issuer_url, portal_client_id)


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
    claims = _decode_verified_claims(
        token, signing_key=signing_key, issuer_url=settings.issuer_url
    )
    return principal_from_claims(claims, settings=settings)


def _decode_verified_claims(
    token: str, *, signing_key: Any, issuer_url: str
) -> Mapping[str, Any]:
    """The PyJWT half of verification: signature, issuer, lifetime, and the
    required-claim set — everything that does not depend on WHICH app clients
    a service accepts. Shared by the single-client and allowlist paths so the
    cryptographic checks cannot drift between them."""
    try:
        claims: Mapping[str, Any] = jwt.decode(
            token,
            key=signing_key,
            algorithms=list(ALGORITHMS),
            issuer=issuer_url,
            options={
                # Cognito ACCESS tokens carry client_id, never aud. Verifying
                # aud here would reject every valid token; the app-client
                # check happens in principal_from_claims against client_id
                # instead.
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
    return claims


# ── Client allowlists (issue #261) ──────────────────────────────────
#
# The MCP service verifies a SET of app clients — one pre-registered Cognito
# client per harness (Cognito has no dynamic client registration), all
# minting tokens this one resource server accepts. The set is the audience
# check: Cognito access tokens carry no RFC 8707 `aud`, so "issued for this
# server" is approximated by "issued to one of this server's own clients" —
# a set DISJOINT from the app's client id, so an app token presented to the
# MCP endpoint fails closed and vice versa (ADR 0016). The tenant API keeps
# the single-client profile above: exactly one surface, exactly one client.


@dataclass(frozen=True)
class MultiClientAuthSettings:
    """The issuer plus the client allowlist that decide whether a token is
    one of ours. Built from config at composition time; both required — see
    `multi_client_settings_or_raise`, there is no "unset means allow"."""

    issuer_url: str
    client_ids: tuple[str, ...]


def multi_client_settings_or_raise(
    issuer_url: str | None, client_ids: tuple[str, ...] | None
) -> MultiClientAuthSettings:
    """Require the issuer and a non-empty allowlist, or refuse to verify
    anything — `settings_or_raise`'s fail-closed rule, for the set shape."""
    if not issuer_url or not client_ids or not all(client_ids):
        raise AuthenticationError(AuthFailureReason.NOT_CONFIGURED)
    return MultiClientAuthSettings(
        issuer_url=issuer_url.rstrip("/"), client_ids=tuple(client_ids)
    )


def verify_access_token_for_clients(
    token: str, *, signing_key: Any, settings: MultiClientAuthSettings
) -> Principal:
    """`verify_access_token`, with the client check against an allowlist.

    Same composition contract: the key arrives from a JwksProvider, this
    function only decides whether the result is trustworthy.
    """
    claims = _decode_verified_claims(
        token, signing_key=signing_key, issuer_url=settings.issuer_url
    )
    if claims.get("token_use") != TOKEN_USE_ACCESS:
        raise AuthenticationError(AuthFailureReason.WRONG_TOKEN_USE)

    client_id = claims.get("client_id")
    if not isinstance(client_id, str) or client_id not in settings.client_ids:
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
        authenticated_at=_auth_time(claims),
    )


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
        authenticated_at=_auth_time(claims),
    )


def _auth_time(claims: Mapping[str, Any]) -> int | None:
    raw = claims.get("auth_time")
    # `bool` is an `int` subclass; `"auth_time": true` is not a time.
    if isinstance(raw, bool) or not isinstance(raw, int) or raw <= 0:
        return None
    return raw


def _scopes(raw: Any) -> tuple[str, ...]:
    """Cognito's `scope` is a single space-delimited string, not a list."""
    if not isinstance(raw, str):
        return ()
    return tuple(raw.split())


# ── Recent authentication (ADR 0024, guardrails 1 and 2) ────────────
#
# "Signs after re-authenticating" and "approves each filing, re-
# authenticating at approval time" are the same check: the token's
# `auth_time` — when the person last typed their password at the pool — is
# no more than a few minutes old. NOT the token's `iat` and not the session's
# age: Cognito copies the original `auth_time` into every token a refresh
# mints, so an hour-old session with a just-refreshed token is still an
# hour-old sign-in, and is refused. The app forces a fresh sign-in with
# `prompt=login` on the authorize request (managed login only, which every
# environment's pool uses — infra/modules/auth's `managed_login_version`).
#
# The window is the caller's to choose, per act. Guardrail 2's signature
# uses `SIGNATURE_MAX_AGE_SECONDS`; PR 6's approval chooses its own.

# Clock skew tolerated in the other direction: an `auth_time` this far in
# the future (the pool's clock ahead of the API's) is still accepted, and one
# further ahead is refused, because a time from the future is not a sign-in
# that happened.
AUTH_TIME_SKEW_SECONDS = 60


class ReauthenticationRequiredError(ForbiddenError):
    """The caller is signed in, but not RECENTLY enough for this act.

    A 403, not a 401, on purpose: the token is valid and the session is fine
    for everything else; a 401 would send the app's refresh-and-retry loop
    round, and a refresh cannot help — it keeps `auth_time`. The API answers
    `{"error": "ReauthenticationRequired"}` so the client knows the remedy
    is a fresh sign-in (`prompt=login`), not a missing permission.
    """


def require_recent_authentication(
    authenticated_at: int | None, *, now: float, max_age_seconds: int
) -> int:
    """`authenticated_at` (a Principal's `auth_time`) if it is within
    `max_age_seconds` of `now`, else ReauthenticationRequiredError.

    Refuses a missing time (never assume fresh), one older than the window,
    and one more than AUTH_TIME_SKEW_SECONDS in the future. Pure: `now` is
    an argument so the boundary is testable to the second.
    """
    if max_age_seconds <= 0:
        raise ValueError("max_age_seconds must be positive")
    if authenticated_at is None:
        raise ReauthenticationRequiredError("sign in again to continue")
    age = now - authenticated_at
    if age > max_age_seconds or age < -AUTH_TIME_SKEW_SECONDS:
        raise ReauthenticationRequiredError("sign in again to continue")
    return authenticated_at


# ── Google Workspace ID tokens (issue #209) ─────────────────────────
#
# The admin service's principal class. Staff sign in directly against Google
# (authorization-code + PKCE, a public per-environment client marked Internal
# in the Workspace org), and the service verifies the ID TOKEN — Google's
# access tokens are opaque, so for this first-party, single-audience internal
# API the ID token is the credential. That is a deliberate deviation from the
# Cognito profile above (which verifies access tokens and refuses ID tokens);
# ADR 0011 records it.
#
# What is verified, and why exactly these things:
#   - RS256 signature against Google's published JWKS (the adapter fetches
#     https://www.googleapis.com/oauth2/v3/certs — note Google's jwks_uri is
#     NOT derivable as <issuer>/.well-known/jwks.json, which is why
#     CognitoJwksProvider takes an explicit jwks_url for this profile).
#   - `iss` is one of Google's two documented forms. Both are Google; a token
#     from any other issuer is somebody else's users.
#   - `aud` equals the environment's client id. Cross-environment tokens
#     (a dev sign-in replayed against prod) fail here.
#   - `hd` equals the Workspace domain. This is the tenant gate restated on
#     our side: the client being Internal means Google refuses outside
#     accounts at sign-in, and this check means a verifier bug or console
#     misconfiguration still cannot admit a personal Gmail. Deny-side
#     redundancy — the two cannot disagree in the dangerous direction.
#   - `email_verified` is true and `email` present: the audit trail records
#     who provisioned what by address, and an unverified address is not an
#     identity.
#   - `exp`/`iat` via PyJWT, `sub` present — same rules as the profile above.

GOOGLE_ISSUERS = ("https://accounts.google.com", "accounts.google.com")

_GOOGLE_JWKS_URL = "https://www.googleapis.com/oauth2/v3/certs"


def google_jwks_url() -> str:
    """Where Google publishes the keys `verify_google_id_token` needs."""
    return _GOOGLE_JWKS_URL


@dataclass(frozen=True)
class GoogleAuthSettings:
    """The two values that decide whether a Google ID token is ours."""

    client_id: str
    workspace_domain: str


@dataclass(frozen=True)
class StaffPrincipal:
    """The authenticated staff caller, derived purely from verified claims.

    `subject` is Google's `sub` — stable and immutable for the account, and
    what audit rows key on. `email` is display + audit; it can be re-pointed
    on Google's side, which is why it is never the key.
    """

    subject: str
    email: str
    expires_at: int | None


def google_settings_or_raise(
    client_id: str | None, workspace_domain: str | None
) -> GoogleAuthSettings:
    """Require both settings, or refuse to verify anything — same fail-closed
    rule as `settings_or_raise`."""
    if not client_id or not workspace_domain:
        raise AuthenticationError(AuthFailureReason.NOT_CONFIGURED)
    return GoogleAuthSettings(client_id=client_id, workspace_domain=workspace_domain)


def verify_google_id_token(
    token: str, *, signing_key: Any, settings: GoogleAuthSettings
) -> StaffPrincipal:
    """Verify a Google Workspace ID token and return the staff principal.

    Same composition contract as `verify_access_token`: the key arrives from a
    JwksProvider (pointed at Google's certs URL), this function only decides
    whether the result is trustworthy.
    """
    try:
        claims: Mapping[str, Any] = jwt.decode(
            token,
            key=signing_key,
            algorithms=list(ALGORITHMS),
            audience=settings.client_id,
            options={
                # `aud` IS verified here — the opposite of the Cognito
                # profile, because Google ID tokens carry the client id there.
                "verify_aud": True,
                "require": ["exp", "iss", "sub", "aud"],
                "verify_exp": True,
                "verify_iat": True,
                "verify_nbf": True,
                "verify_signature": True,
            },
        )
    except jwt.ExpiredSignatureError as exc:
        raise AuthenticationError(AuthFailureReason.EXPIRED) from exc
    except jwt.InvalidAudienceError as exc:
        raise AuthenticationError(AuthFailureReason.INVALID_CLIENT) from exc
    except jwt.MissingRequiredClaimError as exc:
        raise AuthenticationError(AuthFailureReason.INVALID_CLAIMS) from exc
    except jwt.PyJWTError as exc:
        raise AuthenticationError(AuthFailureReason.INVALID_SIGNATURE) from exc

    return staff_principal_from_claims(claims, settings=settings)


def staff_principal_from_claims(
    claims: Mapping[str, Any], *, settings: GoogleAuthSettings
) -> StaffPrincipal:
    """The claim checks PyJWT does not do, then the identity itself.

    `iss` is checked here rather than via decode(issuer=...) because Google
    documents two forms and PyJWT verifies against exactly one.
    """
    if claims.get("iss") not in GOOGLE_ISSUERS:
        raise AuthenticationError(AuthFailureReason.INVALID_ISSUER)

    # The Workspace-domain gate. `hd` is present only on Workspace accounts —
    # a personal Gmail carries none and fails the comparison, which is the
    # correct reading without a special case.
    if claims.get("hd") != settings.workspace_domain:
        raise AuthenticationError(AuthFailureReason.INVALID_CLAIMS)

    email = claims.get("email")
    if (
        claims.get("email_verified") is not True
        or not isinstance(email, str)
        or not email
    ):
        raise AuthenticationError(AuthFailureReason.INVALID_CLAIMS)

    subject = claims.get("sub")
    if not isinstance(subject, str) or not subject:
        raise AuthenticationError(AuthFailureReason.INVALID_CLAIMS)

    expires_at = claims.get("exp")

    return StaffPrincipal(
        subject=subject,
        email=email,
        expires_at=expires_at if isinstance(expires_at, int) else None,
    )
