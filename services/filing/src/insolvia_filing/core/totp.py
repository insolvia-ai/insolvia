"""TOTP (RFC 6238, HMAC-SHA1, 30-second steps, six digits) — the code PACER's
multifactor sign-in asks for, computed from the attorney's sealed seed at the
moment the court asks, and never stored (ADR 0024: "it generates the TOTP
code at submit time from the seed and never stores one").

The seed arrives in a `CredentialSecret` opened for this run; the code this
returns is handed straight to the court's form and goes nowhere else — not to
a log line, not to the filing record, not to an exception message.
"""

from __future__ import annotations

import base64
import hashlib
import hmac
import struct
from typing import Final

STEP_SECONDS: Final = 30
DIGITS: Final = 6


def _key(seed: str) -> bytes:
    normalised = seed.replace(" ", "").upper()
    padding = "=" * (-len(normalised) % 8)
    return base64.b32decode(normalised + padding)


def totp_at(seed: str, moment: float) -> str:
    """The code valid at `moment` (epoch seconds) for a base32 `seed`."""
    counter = int(moment) // STEP_SECONDS
    digest = hmac.new(_key(seed), struct.pack(">Q", counter), hashlib.sha1).digest()
    offset = digest[-1] & 0x0F
    value = struct.unpack(">I", digest[offset : offset + 4])[0] & 0x7FFFFFFF
    return str(value % (10**DIGITS)).zfill(DIGITS)
