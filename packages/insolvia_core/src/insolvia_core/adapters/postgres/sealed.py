"""The domain's `Envelope` (base64 text) as `bytea` columns and back.

Byte for byte: a column holds exactly the bytes the cipher produced, and
reading it back re-encodes them with the same standard base64 the cipher
wrote (adapters/envelope.py), so an envelope sealed before the move opens
unchanged after it.
"""

from __future__ import annotations

import base64
from collections.abc import Mapping
from typing import Any

from insolvia_core.tax_ids import Envelope

ENVELOPE_COLUMNS = ("scheme", "ciphertext", "nonce", "wrapped_key")


def envelope_values(envelope: Envelope) -> tuple[str, bytes, bytes, bytes]:
    """`ENVELOPE_COLUMNS`' values, in order."""
    return (
        envelope.scheme,
        _raw(envelope.ciphertext),
        _raw(envelope.nonce),
        _raw(envelope.wrapped_key),
    )


def envelope_from_row(row: Mapping[str, Any]) -> Envelope:
    return Envelope(
        scheme=row["scheme"],
        ciphertext=_text(row["ciphertext"]),
        nonce=_text(row["nonce"]),
        wrapped_key=_text(row["wrapped_key"]),
    )


def _raw(text: str) -> bytes:
    return base64.b64decode(text.encode("ascii"), validate=True)


def _text(raw: bytes) -> str:
    return base64.b64encode(raw).decode("ascii")
