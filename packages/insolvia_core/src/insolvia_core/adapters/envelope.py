"""The symmetric half of tax-id envelope encryption, shared by both
`TaxIdCipher` implementations (insolvia_core.tax_ids owns the design).

WHY ONE MODULE UNDER `adapters/` RATHER THAN TWO COPIES OR A DOMAIN MODULE.
The two ciphers differ in exactly one call: where the data key is wrapped —
KMS in adapters/aws, a fixed local key in adapters/memory. Everything after
that (AES-256-GCM over the digits, the encryption context as associated data,
the base64 envelope) must be byte-for-byte the same code, or a unit test
against the memory cipher would prove nothing about what the deployed one
stores. It lives here and not in the domain because it imports
`cryptography`, and the domain modules import nothing but each other and
the stdlib (tests/unit/test_architecture.py; PyJWT is the one deliberate
exception) — `cryptography` already arrives with `PyJWT[crypto]`, and
pyproject names it directly now that a module here imports it by name.

The associated data is the encryption context in canonical form (sorted keys,
no whitespace). That is what ties the ciphertext to `{purpose, firm_id,
tax_id_ref}` even before the wrapped key is presented to KMS: opening under
any other context fails the GCM tag, whichever cipher does the unwrapping.
"""

from __future__ import annotations

import base64
import json
import os
from collections.abc import Mapping
from typing import Final

from cryptography.hazmat.primitives.ciphers.aead import AESGCM

from insolvia_core.tax_ids import SEALED_SCHEME, Envelope

# AES-256-GCM: 32-byte keys, 12-byte nonces (the size NIST SP 800-38D
# recommends and the only one AESGCM does not have to hash first).
DATA_KEY_BYTES: Final = 32
NONCE_BYTES: Final = 12


def context_aad(context: Mapping[str, str]) -> bytes:
    """The encryption context as GCM associated data — canonical JSON, so
    the same mapping always produces the same bytes."""
    return json.dumps(dict(context), sort_keys=True, separators=(",", ":")).encode()


def new_data_key() -> bytes:
    return os.urandom(DATA_KEY_BYTES)


def seal_with_data_key(
    data_key: bytes, plaintext: str, *, context: Mapping[str, str], wrapped_key: bytes
) -> Envelope:
    """Encrypt `plaintext` under `data_key`, binding `context`, and package
    it with the caller's already-wrapped copy of the key."""
    nonce = os.urandom(NONCE_BYTES)
    ciphertext = AESGCM(data_key).encrypt(
        nonce, plaintext.encode(), context_aad(context)
    )
    return Envelope(
        scheme=SEALED_SCHEME,
        ciphertext=_b64(ciphertext),
        nonce=_b64(nonce),
        wrapped_key=_b64(wrapped_key),
    )


def open_with_data_key(
    data_key: bytes, envelope: Envelope, *, context: Mapping[str, str]
) -> str:
    """The inverse. A wrong key or a wrong context fails the GCM tag and
    raises (`cryptography.exceptions.InvalidTag`); an envelope under a scheme
    this code does not know is refused before any key is touched."""
    if envelope.scheme != SEALED_SCHEME:
        raise ValueError(f"unknown sealed tax-id scheme {envelope.scheme!r}")
    plaintext = AESGCM(data_key).decrypt(
        _unb64(envelope.nonce), _unb64(envelope.ciphertext), context_aad(context)
    )
    return plaintext.decode()


def wrapped_key_bytes(envelope: Envelope) -> bytes:
    return _unb64(envelope.wrapped_key)


def _b64(raw: bytes) -> str:
    return base64.b64encode(raw).decode("ascii")


def _unb64(text: str) -> bytes:
    return base64.b64decode(text.encode("ascii"), validate=True)
