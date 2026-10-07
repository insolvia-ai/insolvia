"""The attorney's written authorization (ADR 0024, guardrail 2): the text
Insolvia asks an attorney to sign before it will hold their CM/ECF login, and
the signed record that the credential vault stands on.

"Before a credential can be stored, the attorney signs an authorization for
Insolvia to submit filings under their CM/ECF login ... The signed instrument
(its text versioned in this repository, the version and a digest recorded
with the signature) is stored *with* the credential and is a precondition for
it: the vault refuses a credential with no current authorization, and
withdrawing the authorization revokes the credential."

This module is the instrument and the record. The vault's half of the rule —
enrolment refuses, `is_openable` refuses, withdrawal destroys — is in
`insolvia_core.filing_credentials`, which imports this one (never the other
way round).

## The text

`agreements/filing-authorization/<version>.txt`, shipped in the wheel
(pyproject's package-data). `TEXT_VERSION` names the one an attorney is asked
to sign today. APPEND-ONLY, like the court registry: a change is a new file
and a bump of `TEXT_VERSION`, never an edit — and an edit would not go
unnoticed anyway, because a signature records the SHA-256 of the exact bytes
it was given and `is_current` compares it against the file as it ships now.

The text the API serves, the text the attorney reads, and the text that is
digested are one string: the app renders what `GET /v1/me/filing-
authorization` returns, and signing posts back the version and digest it
rendered, so a client that showed a stale text is refused rather than
recorded as having agreed to the new one.

## "Current"

A signature is CURRENT when it is this attorney's, in this firm, not
withdrawn, and over `TEXT_VERSION` with the digest of that text as it ships.
A NEW VERSION INVALIDATES EVERY OLDER SIGNATURE: the terms an attorney agreed
to are the terms in the text, so a changed text is not something they have
agreed to. Their stored logins are kept, sealed, but nothing can be enrolled
and nothing can be opened (`filing_credentials.is_openable`) until they sign
the new version — which they do without re-entering a PACER password, since
the credential's authority is the attorney's current signature, not the one
it happened to be enrolled under.

## Where it is stored

In the vault's own table (`insolvia-<env>-filing-credentials`), in the
attorney's own partition, beside their credentials — "stored with the
credential". Two kinds of item:

- `SK = AUTHORIZATION` — THE CURRENT ONE, at a fixed key, so the filing
  worker's GetItem-only grant can read it at the final-submit re-check
  without a Query. Present while the attorney has a signature in force;
  deleted on withdrawal.
- `SK = AUTHORIZATION#<id>` — the history: one immutable record per
  signature (status `signed`, later `superseded` or `withdrawn`), the
  evidence of what was in force when a filing was made.

## Re-authentication

`sign_authorization` takes the principal's `auth_time` and refuses one older
than `SIGNATURE_MAX_AGE_SECONDS` (`auth.require_recent_authentication`). The
check is in the domain function, not only the route, so no second caller can
record a signature from a stale session.
"""

from __future__ import annotations

import hashlib
import uuid
from collections.abc import Mapping
from dataclasses import dataclass
from functools import cache
from importlib import resources
from typing import TYPE_CHECKING, Final

from insolvia_core.auth import require_recent_authentication
from insolvia_core.errors import ConflictError, ValidationError

from .access_log import record_access
from .fields import timestamp as _timestamp

if TYPE_CHECKING:
    from .ports import AccessLog, FilingAuthorizationStore

__all__ = [
    "AUTHORIZATION_STATUSES",
    "SIGNATURE_MAX_AGE_SECONDS",
    "TEXT_VERSION",
    "FilingAuthorization",
    "authorization_from_item",
    "authorization_item",
    "authorization_json",
    "authorization_text",
    "current_authorization",
    "history_sort_key",
    "is_current",
    "sign_authorization",
    "text_digest",
    "text_json",
]

# The version an attorney is asked to sign today. `-draft` is part of the
# name on purpose: this text has not been reviewed by the maintainer or by
# counsel, and the record of anyone who signs it says so.
TEXT_VERSION: Final = "2026-10-07-draft"

# Five minutes from typing the password at the sign-in page to pressing
# Sign. The flow is: read the text, press "Sign in again", sign in, land back
# on /account, press Sign — a minute for someone who has already read it, and
# the window has to absorb a slow MFA prompt. Shorter than any session the
# app holds (access tokens last an hour, refresh tokens days), which is the
# point: a browser left signed in cannot sign for its owner.
SIGNATURE_MAX_AGE_SECONDS: Final = 300

# `superseded`: a later version was signed over it. `withdrawn`: the
# attorney withdrew it (and so destroyed their credentials).
AUTHORIZATION_STATUSES: Final = ("signed", "superseded", "withdrawn")

CURRENT_SORT_KEY: Final = "AUTHORIZATION"
_HISTORY_PREFIX: Final = "AUTHORIZATION#"

_VERSION_ALPHABET: Final = frozenset("abcdefghijklmnopqrstuvwxyz0123456789-")


@dataclass(frozen=True)
class FilingAuthorization:
    """One signature over one version of the text.

    `attorney_id` is the signer — the token's subject, never a body field.
    `authenticated_at` is the `auth_time` the signature was made under
    (epoch seconds), so the record itself shows the sign-in was fresh.
    """

    authorization_id: str
    firm_id: str
    attorney_id: str
    text_version: str
    text_digest: str
    signed_at: str
    authenticated_at: int
    status: str = "signed"
    withdrawn_at: str | None = None


# ── The text ────────────────────────────────────────────────────


@cache
def authorization_text(version: str = TEXT_VERSION) -> str:
    """The exact text of `version`, as shipped. Raises for a version that
    is not in the package (or a name that could escape its directory)."""
    if not version or not set(version) <= _VERSION_ALPHABET:
        raise ValueError(f"not an authorization text version: {version!r}")
    path = (
        resources.files("insolvia_core")
        .joinpath("agreements")
        .joinpath("filing-authorization")
        .joinpath(f"{version}.txt")
    )
    return path.read_text(encoding="utf-8")


def text_digest(text: str) -> str:
    """SHA-256 of the text's UTF-8 bytes, lower-case hex. What a signature
    records, and what `is_current` recomputes."""
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def text_json() -> dict[str, str]:
    """The text an attorney is asked to sign, with what they must post back."""
    text = authorization_text()
    return {"version": TEXT_VERSION, "digest": text_digest(text), "text": text}


# ── The record ──────────────────────────────────────────────────


def is_current(
    authorization: FilingAuthorization | None, *, firm_id: str, attorney_id: str
) -> bool:
    """THE definition of "a current authorization" (module docstring)."""
    return (
        authorization is not None
        and authorization.status == "signed"
        and authorization.firm_id == firm_id
        and authorization.attorney_id == attorney_id
        and authorization.text_version == TEXT_VERSION
        and authorization.text_digest == text_digest(authorization_text())
    )


def history_sort_key(authorization_id: str) -> str:
    return f"{_HISTORY_PREFIX}{authorization_id}"


def authorization_item(
    authorization: FilingAuthorization, *, sort_key: str
) -> dict[str, object]:
    """The stored shape — under `CURRENT_SORT_KEY` or a `history_sort_key`;
    the attributes are the same either way. The partition is the vault's
    (`filing_credentials.partition_key`), which the adapter supplies."""
    item: dict[str, object] = {
        "SK": sort_key,
        "authorizationId": authorization.authorization_id,
        "firmId": authorization.firm_id,
        "attorneyId": authorization.attorney_id,
        "textVersion": authorization.text_version,
        "textDigest": authorization.text_digest,
        "signedAt": authorization.signed_at,
        "authTime": authorization.authenticated_at,
        "status": authorization.status,
    }
    if authorization.withdrawn_at is not None:
        item["withdrawnAt"] = authorization.withdrawn_at
    return item


def authorization_from_item(item: Mapping[str, object]) -> FilingAuthorization:
    def text(key: str) -> str:
        value = item.get(key)
        if not isinstance(value, str) or not value:
            raise ValueError(f"filing authorization item is missing {key}")
        return value

    auth_time = item.get("authTime")
    if isinstance(auth_time, bool) or not isinstance(auth_time, int | float):
        raise ValueError("filing authorization item is missing authTime")
    withdrawn_at = item.get("withdrawnAt")
    return FilingAuthorization(
        authorization_id=text("authorizationId"),
        firm_id=text("firmId"),
        attorney_id=text("attorneyId"),
        text_version=text("textVersion"),
        text_digest=text("textDigest"),
        signed_at=text("signedAt"),
        authenticated_at=int(auth_time),
        status=text("status"),
        withdrawn_at=withdrawn_at if isinstance(withdrawn_at, str) else None,
    )


def authorization_json(
    authorization: FilingAuthorization | None, *, firm_id: str, attorney_id: str
) -> dict[str, object]:
    """The status view: the signature in force (or the stale one, flagged),
    and the version a new signature would be over."""
    signature: dict[str, object] | None = None
    if authorization is not None:
        signature = {
            "id": authorization.authorization_id,
            "text_version": authorization.text_version,
            "text_digest": authorization.text_digest,
            "signed_at": authorization.signed_at,
        }
    return {
        "current_version": TEXT_VERSION,
        "current": is_current(authorization, firm_id=firm_id, attorney_id=attorney_id),
        "signature": signature,
    }


# ── Sign ────────────────────────────────────────────────────────


def _parse_signature(payload: object) -> tuple[str, str]:
    if not isinstance(payload, Mapping):
        raise ValidationError("request body must be a JSON object")
    version = payload.get("text_version")
    digest = payload.get("text_digest")
    if not isinstance(version, str) or not isinstance(digest, str):
        raise ValidationError("text_version and text_digest are required")
    return version, digest


def current_authorization(
    *, firm_id: str, attorney_id: str, store: FilingAuthorizationStore
) -> FilingAuthorization | None:
    """The attorney's authorization in force — the `AUTHORIZATION` item — or
    None. Returned even when it is over an older version, so the status view
    can say "signed, but the text has changed"; `is_current` is the test."""
    return store.get_current(firm_id, attorney_id)


def sign_authorization(
    payload: object,
    *,
    firm_id: str,
    attorney_id: str,
    authenticated_at: int | None,
    now: float,
    store: FilingAuthorizationStore,
    access_log: AccessLog,
) -> FilingAuthorization:
    """Record the caller's signature over the current text.

    Refuses, in this order: a sign-in older than SIGNATURE_MAX_AGE_SECONDS
    (ReauthenticationRequiredError, 403); a body that names a version or
    digest other than the text as it ships (ConflictError — the client
    showed a different text, so the attorney has not read this one); and a
    second signature while one is already current (ConflictError).

    Signing over an older version's signature supersedes it: the history
    item is marked `superseded` and the `AUTHORIZATION` item replaced.
    """
    fresh = require_recent_authentication(
        authenticated_at, now=now, max_age_seconds=SIGNATURE_MAX_AGE_SECONDS
    )
    version, digest = _parse_signature(payload)
    expected = text_digest(authorization_text())
    if version != TEXT_VERSION or digest != expected:
        raise ConflictError(
            "The authorization text has changed since it was shown — read the "
            "current version and sign again."
        )
    existing = store.get_current(firm_id, attorney_id)
    if is_current(existing, firm_id=firm_id, attorney_id=attorney_id):
        raise ConflictError("You have already signed the current authorization.")

    authorization = FilingAuthorization(
        authorization_id=str(uuid.uuid4()),
        firm_id=firm_id,
        attorney_id=attorney_id,
        text_version=TEXT_VERSION,
        text_digest=expected,
        signed_at=_timestamp(),
        authenticated_at=fresh,
    )
    store.put_current(authorization, replacing=existing)
    access_log.record(
        record_access(
            authorization_id=authorization.authorization_id,
            principal=attorney_id,
            action="authorization.sign",
        )
    )
    return authorization
