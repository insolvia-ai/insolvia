"""The credential vault (ADR 0024, guardrail 3): an attorney's CM/ECF login,
sealed so that the API can store it and only the filing worker can open it.

ADR 0024 decides that Insolvia files the case itself, signing in to the
court's CM/ECF as the attorney with the attorney's PACER password and TOTP
seed. That is the credential ADR 0001 refuses to hold anywhere a client can
reach, and guardrail 3 is how it is fenced. This module is the domain half;
`infra/modules/filing_credentials` is the half AWS enforces, and the two are
written to say the same thing:

- A DEDICATED STORE AND KEY. Not the case table and not the case key: the
  items live in `insolvia-<env>-filing-credentials`, and the secret in each
  is sealed under that table's own KMS key.
- SEAL WITHOUT OPEN. Enrolment is the API: it mints a data key
  (`GenerateDataKey`), seals the password and seed with it, and keeps the
  wrapped half. It never holds a way to unwrap that half again — the key
  policy denies Decrypt to every principal but the filing worker's role. In
  code the split is two ports, `FilingCredentialSealer` and
  `FilingCredentialOpener`, so the API's composition root cannot be handed
  an opener by accident: the API composes the first and nothing composes
  the second until services/filing exists (ADR 0024 PR 7).
- EVERY OPEN IS LOGGED, FIRST. `open_credential` writes a `credential.open`
  access row — the attorney, the filing, the purpose — before it reads the
  item, so a failed open still shows as attempted (`tax_ids.read_tax_id`'s
  order, for its reason).
- REVOCATION DESTROYS. `revoke_credential` deletes the item, envelope and
  all. It is not a status flag a worker might read stale: after it, there is
  nothing to open, and the next `open_credential` raises.

## What is sealed, and under what context

ONE ENVELOPE PER CREDENTIAL, holding the password and the seed together as
one JSON document. They are only ever used together (sign in, then answer
the TOTP challenge), they are enrolled together, and they are revoked
together; two envelopes would be two KMS calls and two chances to store one
without the other, for nothing.

THE ENCRYPTION CONTEXT BINDS THE FIRM, THE ATTORNEY AND THE CREDENTIAL:
`{purpose, firm_id, attorney_id, credential_id}`. KMS refuses to unwrap the
data key under any other context, and the same mapping is the AES-GCM
associated data (adapters/envelope), so an envelope cannot be copied onto
another attorney's item, another firm's, or another credential of the same
attorney. `purpose` is what the key policy and both IAM grants condition on.

## What a client ever sees

`credential_json` — the login name, the courts, the status, the times. Never
the password, never the seed, and never the envelope: there is no route by
which sealed material leaves the server, and no route by which plaintext
does. The enrolment request is the one place the seed crosses a client
(ADR 0024: "the screen that collects the TOTP seed is the one place the seed
crosses a client"), and nothing echoes it back.

## Where PR 5 (guardrail 2) attaches

"The vault refuses a credential with no current authorization." Two seams,
both marked `ADR 0024 PR 5` below: `enrol_credential` takes the
authorization reference and stores it on the item (None until PR 5 makes
it required), and `is_openable` is the ONE predicate `open_credential`
consults — PR 5 adds "and its authorization is current" there, and every
caller inherits it.
"""

from __future__ import annotations

import base64
import binascii
import json
import re
import uuid
from collections.abc import Mapping
from dataclasses import dataclass, field
from typing import TYPE_CHECKING, Final

from insolvia_core import courts
from insolvia_core.errors import ConflictError, FieldValidationError

from .access_log import record_access
from .fields import mapping as _mapping
from .fields import timestamp as _timestamp
from .tax_ids import Envelope

if TYPE_CHECKING:
    from .ports import (
        AccessLog,
        FilingCredentialOpener,
        FilingCredentialSealer,
        FilingCredentialStore,
    )

__all__ = [
    "CREDENTIAL_PURPOSE",
    "CREDENTIAL_STATUSES",
    "CredentialSecret",
    "CredentialUnavailableError",
    "Enrolment",
    "FilingCredential",
    "credential_from_item",
    "credential_item",
    "credential_json",
    "encryption_context",
    "enrol_credential",
    "is_openable",
    "list_credentials",
    "open_credential",
    "parse_enrolment",
    "partition_key",
    "revoke_credential",
    "sort_key",
]

# The one value the key policy and both IAM grants condition on
# (infra/modules/filing_credentials, `local.credential_purpose`). Renaming it
# here without renaming it there does not fail an apply — every enrolment
# starts failing with AccessDenied in the deployed environments.
CREDENTIAL_PURPOSE: Final = "filing-credential"

# `active` is the only stored status today: revocation deletes the item
# rather than flagging it (module docstring). PR 5's authorization
# withdrawal revokes the same way. The field exists so a later state — a
# credential the court has locked, say — has somewhere to live without a
# shape change.
CREDENTIAL_STATUSES: Final = ("active",)

# PACER usernames are short identifiers; the cap is generous and the
# alphabet refuses anything that could collide with a key separator.
_LOGIN_RE: Final = re.compile(r"^[A-Za-z0-9._@-]{1,64}\Z")
MAX_PASSWORD: Final = 128
# A TOTP seed is base32 (RFC 4648), as every authenticator enrolment screen
# shows it. 16 characters is 80 bits — RFC 4226's floor is 128, but PACER's
# own seed length is not something this module gets to decide, so the floor
# is the shortest seed any common authenticator accepts.
_SEED_RE: Final = re.compile(r"^[A-Z2-7]{16,128}={0,6}\Z")


@dataclass(frozen=True)
class CredentialSecret:
    """The plaintext — what the worker signs in with. `repr` is redacted so
    the value cannot reach a log line through an f-string or a traceback."""

    password: str = field(repr=False)
    totp_seed: str = field(repr=False)

    def __repr__(self) -> str:
        return "CredentialSecret(<redacted>)"


@dataclass(frozen=True)
class Enrolment:
    """A parsed enrolment request: the login, the secret to seal, and the
    launch courts the attorney says they are registered to e-file in."""

    login: str
    secret: CredentialSecret = field(repr=False)
    courts: tuple[str, ...]


@dataclass(frozen=True)
class FilingCredential:
    """The stored item. Everything but `envelope` is plain and is what the
    status view is built from; `envelope` is opaque to everything but the
    cipher adapters."""

    credential_id: str
    firm_id: str
    attorney_id: str
    login: str
    courts: tuple[str, ...]
    status: str
    envelope: Envelope = field(repr=False)
    created_at: str
    updated_at: str
    # ADR 0024 PR 5: the signed authorization this credential stands on.
    # None until PR 5 makes enrolment require one.
    authorization_ref: str | None = None


class CredentialUnavailableError(Exception):
    """There is nothing to open: the credential was never enrolled, was
    revoked (the item is gone), or is not openable (`is_openable`). The
    worker treats this as a stop-and-hand-back, never a retry."""


def new_credential_id() -> str:
    return str(uuid.uuid4())


def encryption_context(
    *, firm_id: str, attorney_id: str, credential_id: str
) -> dict[str, str]:
    """The KMS encryption context AND the AES-GCM associated data for one
    credential — the module docstring says what each member buys."""
    return {
        "purpose": CREDENTIAL_PURPOSE,
        "firm_id": firm_id,
        "attorney_id": attorney_id,
        "credential_id": credential_id,
    }


# ── Parsing the enrolment ───────────────────────────────────────


def _normalise_seed(value: str) -> str:
    """Seeds are shown grouped and sometimes lower-case; the canonical form
    is upper-case base32 with the spaces removed."""
    return "".join(value.split()).upper()


def _seed_decodes(seed: str) -> bool:
    padded = seed.rstrip("=")
    padded += "=" * (-len(padded) % 8)
    try:
        base64.b32decode(padded)
    except (binascii.Error, ValueError):
        return False
    return True


def parse_enrolment(payload: object) -> Enrolment:
    """Validate an enrolment body: `{login, password, totp_seed, courts?}`.

    Every problem lands in one FieldValidationError keyed by field, and the
    messages never echo a submitted value — a 400 body is logged by nobody,
    but it is rendered, and a secret has no business in either.
    """
    errors: dict[str, str] = {}
    raw = _mapping(payload, "body", errors)

    login = raw.get("login")
    if not isinstance(login, str) or not _LOGIN_RE.match(login.strip()):
        errors["login"] = (
            "Enter the PACER username — letters, digits and . _ @ - only, "
            "at most 64 characters."
        )

    password = raw.get("password")
    if not isinstance(password, str) or not password:
        errors["password"] = "Enter the PACER password."
    elif len(password) > MAX_PASSWORD:
        errors["password"] = f"At most {MAX_PASSWORD} characters."
    elif any(ord(ch) < 0x20 or ord(ch) == 0x7F for ch in password):
        errors["password"] = "The password contains a control character."

    seed_raw = raw.get("totp_seed")
    seed = _normalise_seed(seed_raw) if isinstance(seed_raw, str) else ""
    if not _SEED_RE.match(seed) or not _seed_decodes(seed):
        errors["totp_seed"] = (
            "Enter the authenticator key PACER showed you — the base32 text "
            "(letters A to Z and digits 2 to 7), at least 16 characters."
        )

    courts_raw = raw.get("courts", [])
    chosen: list[str] = []
    if not isinstance(courts_raw, list) or not all(
        isinstance(c, str) for c in courts_raw
    ):
        errors["courts"] = "Must be a list of court codes."
    else:
        known = set(courts.district_codes())
        unknown = sorted({c for c in courts_raw if c not in known})
        if unknown:
            errors["courts"] = "Unknown court: " + ", ".join(unknown) + "."
        chosen = sorted(set(courts_raw))

    if errors:
        raise FieldValidationError(errors)
    assert isinstance(login, str)
    assert isinstance(password, str)
    return Enrolment(
        login=login.strip(),
        secret=CredentialSecret(password=password, totp_seed=seed),
        courts=tuple(chosen),
    )


# ── The item and the wire shape ─────────────────────────────────


def partition_key(firm_id: str, attorney_id: str) -> str:
    return f"ATTORNEY#{firm_id}#{attorney_id}"


def sort_key(credential_id: str) -> str:
    return f"CREDENTIAL#{credential_id}"


def credential_item(credential: FilingCredential) -> dict[str, object]:
    item: dict[str, object] = {
        "PK": partition_key(credential.firm_id, credential.attorney_id),
        "SK": sort_key(credential.credential_id),
        "credentialId": credential.credential_id,
        "firmId": credential.firm_id,
        "attorneyId": credential.attorney_id,
        "login": credential.login,
        "courts": list(credential.courts),
        "status": credential.status,
        "scheme": credential.envelope.scheme,
        "ciphertext": credential.envelope.ciphertext,
        "nonce": credential.envelope.nonce,
        "wrappedKey": credential.envelope.wrapped_key,
        "createdAt": credential.created_at,
        "updatedAt": credential.updated_at,
    }
    if credential.authorization_ref is not None:
        item["authorizationRef"] = credential.authorization_ref
    return item


def credential_from_item(item: Mapping[str, object]) -> FilingCredential:
    def text(key: str) -> str:
        value = item.get(key)
        if not isinstance(value, str) or not value:
            raise ValueError(f"filing credential item is missing {key}")
        return value

    raw_courts = item.get("courts", [])
    if not isinstance(raw_courts, list):
        raise ValueError("filing credential item has malformed courts")
    authorization_ref = item.get("authorizationRef")
    return FilingCredential(
        credential_id=text("credentialId"),
        firm_id=text("firmId"),
        attorney_id=text("attorneyId"),
        login=text("login"),
        courts=tuple(str(c) for c in raw_courts),
        status=text("status"),
        envelope=Envelope(
            scheme=text("scheme"),
            ciphertext=text("ciphertext"),
            nonce=text("nonce"),
            wrapped_key=text("wrappedKey"),
        ),
        created_at=text("createdAt"),
        updated_at=text("updatedAt"),
        authorization_ref=authorization_ref
        if isinstance(authorization_ref, str)
        else None,
    )


def credential_json(credential: FilingCredential) -> dict[str, object]:
    """THE STATUS VIEW — and the only shape a client ever receives. No
    password, no seed, no envelope; `test_filing_credentials` pins the key
    set so a field added to the item cannot leak into the view unreviewed."""
    return {
        "id": credential.credential_id,
        "login": credential.login,
        "courts": list(credential.courts),
        "status": credential.status,
        "created_at": credential.created_at,
        "updated_at": credential.updated_at,
    }


# ── Enrol, status, revoke (the API) ─────────────────────────────


def _payload(secret: CredentialSecret) -> str:
    return json.dumps(
        {"password": secret.password, "totp_seed": secret.totp_seed},
        sort_keys=True,
        separators=(",", ":"),
    )


def enrol_credential(
    enrolment: Enrolment,
    *,
    firm_id: str,
    attorney_id: str,
    sealer: FilingCredentialSealer,
    store: FilingCredentialStore,
    access_log: AccessLog,
    authorization_ref: str | None = None,
) -> FilingCredential:
    """Seal and store one credential for the signed-in attorney.

    `attorney_id` is the CALLER's own subject — the route passes the token's,
    never a body field — so nobody enrols a credential for somebody else.

    A login the attorney already has enrolled is refused (a ConflictError
    from the store's conditional write would also refuse a racing duplicate
    credential id; the login check here is the one an honest client hits).
    Rotating a PACER password is revoke-then-enrol, deliberately: an enrol
    that silently replaced a sealed secret would be a second write path to
    the one item revocation exists to destroy.

    ADR 0024 PR 5: `authorization_ref` becomes required, and this function
    refuses an enrolment whose authorization is not current.
    """
    if any(
        existing.login.lower() == enrolment.login.lower()
        for existing in store.list_for_attorney(firm_id, attorney_id)
    ):
        raise ConflictError(
            "That PACER login is already enrolled — revoke it first to replace it."
        )

    credential_id = new_credential_id()
    now = _timestamp()
    envelope = sealer.seal(
        _payload(enrolment.secret),
        context=encryption_context(
            firm_id=firm_id, attorney_id=attorney_id, credential_id=credential_id
        ),
    )
    credential = FilingCredential(
        credential_id=credential_id,
        firm_id=firm_id,
        attorney_id=attorney_id,
        login=enrolment.login,
        courts=enrolment.courts,
        status="active",
        envelope=envelope,
        created_at=now,
        updated_at=now,
        authorization_ref=authorization_ref,
    )
    store.create(credential)
    access_log.record(
        record_access(
            credential_id=credential_id,
            principal=attorney_id,
            action="credential.enrol",
        )
    )
    return credential


def list_credentials(
    *, firm_id: str, attorney_id: str, store: FilingCredentialStore
) -> tuple[FilingCredential, ...]:
    """The attorney's own credentials, for the status view. Reads plain
    attributes only — status never opens an envelope."""
    return store.list_for_attorney(firm_id, attorney_id)


def revoke_credential(
    credential_id: str,
    *,
    firm_id: str,
    attorney_id: str,
    store: FilingCredentialStore,
    access_log: AccessLog,
) -> bool:
    """DESTROY the item — envelope, wrapped key and all. True when there was
    one. Keyed by the caller's own partition, so a credential id belonging
    to another attorney simply is not found (the route answers 404, the
    anti-oracle answer). Logged either way: an attempt to revoke something
    that is not there is still an act on a credential id."""
    deleted = store.delete(firm_id, attorney_id, credential_id)
    access_log.record(
        record_access(
            credential_id=credential_id,
            principal=attorney_id,
            action="credential.revoke",
            outcome="allowed" if deleted else "denied",
        )
    )
    return deleted


# ── The open (the filing worker) ────────────────────────────────


def is_openable(credential: FilingCredential) -> bool:
    """THE ONE PREDICATE `open_credential` consults before it decrypts.

    ADR 0024 PR 5 adds: "and its authorization is current". ADR 0024 PR 7's
    worker calls `open_credential` again immediately before the final
    submit, so whatever this says is re-checked at that moment, not only
    when the run starts.
    """
    return credential.status == "active"


def open_credential(
    *,
    firm_id: str,
    attorney_id: str,
    credential_id: str,
    filing_id: str,
    purpose: str,
    opener: FilingCredentialOpener,
    store: FilingCredentialStore,
    access_log: AccessLog,
) -> CredentialSecret:
    """THE PLAINTEXT — and the one place it is produced. The filing worker's
    call (services/filing, ADR 0024 PR 7); nothing in the API composes an
    opener, and the key policy would refuse the API's role if it did.

    Records a `credential.open` row FIRST — the attorney on whose behalf, the
    filing, the purpose (`sign_in`, `final_submit_recheck`) — so an open
    that then fails still shows as attempted. Then reads the item, refuses
    one that is gone or not openable, and opens the envelope under the
    context it was sealed with: a wrong firm, attorney or credential fails
    at KMS (and at the GCM tag) rather than returning anything.
    """
    access_log.record(
        record_access(
            credential_id=credential_id,
            principal=attorney_id,
            action="credential.open",
            purpose=purpose,
            filing_id=filing_id,
        )
    )
    credential = store.get(firm_id, attorney_id, credential_id)
    if credential is None or not is_openable(credential):
        raise CredentialUnavailableError(credential_id)
    plaintext = opener.open(
        credential.envelope,
        context=encryption_context(
            firm_id=credential.firm_id,
            attorney_id=credential.attorney_id,
            credential_id=credential.credential_id,
        ),
    )
    document = json.loads(plaintext)
    return CredentialSecret(
        password=str(document["password"]), totp_seed=str(document["totp_seed"])
    )
