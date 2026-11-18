"""The development-only key adapters (#624, ADR 0030): KMS's local stand-in.

A laptop with no AWS session seals tax ids and filing credentials under the
deterministic local key (adapters/memory/tax_id_cipher) — the real envelope
code path minus the network. That key secures nothing, so composing it is
refused anywhere but INSOLVIA_ENV=local: a deployed service that lost its KMS
settings must fail to start, never fall back to a key that is in this repo.
"""

from __future__ import annotations

from insolvia_core.adapters.memory.filing_credentials import LocalCredentialSealer
from insolvia_core.adapters.memory.tax_id_cipher import LocalTaxIdCipher


def development_key_adapters(
    environment: str,
) -> tuple[LocalTaxIdCipher, LocalCredentialSealer]:
    if environment != "local":
        raise RuntimeError(
            "the development key adapters are refused outside INSOLVIA_ENV=local "
            f"(got {environment!r}); a deployed service seals under KMS"
        )
    return LocalTaxIdCipher(), LocalCredentialSealer()
