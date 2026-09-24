from __future__ import annotations

import hashlib
import os
from collections.abc import Mapping
from typing import Final

from cryptography.hazmat.primitives.ciphers.aead import AESGCM

from insolvia_core.adapters.envelope import (
    NONCE_BYTES,
    context_aad,
    new_data_key,
    open_with_data_key,
    seal_with_data_key,
    wrapped_key_bytes,
)
from insolvia_core.tax_ids import Envelope

# DETERMINISTIC, AND NEVER DEPLOYED. A fixed key derived from a fixed
# string, so every test process and every bare development server wraps
# data keys under the same key: a fixture sealed in one test can be opened
# in another, and a memory store handed from one test to the next stays
# readable. It secures nothing and is not meant to — the entrypoints
# compose KmsTaxIdCipher wherever a real table is named (api_lambda.py
# refuses to boot without one), and this class is reachable only from the
# in-memory group that has no real table to protect.
LOCAL_MASTER_KEY: Final = hashlib.sha256(
    b"insolvia local tax-id master key - tests and the bare dev server only"
).digest()


class LocalTaxIdCipher:
    """TaxIdCipher with the KMS call replaced by a local key wrap.

    THE REAL CODE PATH MINUS THE NETWORK: `seal` mints a data key exactly as
    KMS would, wraps it under LOCAL_MASTER_KEY with the encryption context
    as associated data (which is precisely the binding KMS enforces on its
    side), and hands the pair to the same `envelope` module the AWS cipher
    uses. `open` unwraps under the context it is given, so an envelope
    sealed for one firm and ref cannot be opened for another — the same
    refusal, raised by the GCM tag here and by KMS there.
    """

    def __init__(self, master_key: bytes = LOCAL_MASTER_KEY) -> None:
        self.master_key = master_key

    def seal(self, plaintext: str, *, context: Mapping[str, str]) -> Envelope:
        data_key = new_data_key()
        nonce = os.urandom(NONCE_BYTES)
        wrapped = nonce + AESGCM(self.master_key).encrypt(
            nonce, data_key, context_aad(context)
        )
        return seal_with_data_key(
            data_key, plaintext, context=context, wrapped_key=wrapped
        )

    def open(self, envelope: Envelope, *, context: Mapping[str, str]) -> str:
        wrapped = wrapped_key_bytes(envelope)
        data_key = AESGCM(self.master_key).decrypt(
            wrapped[:NONCE_BYTES], wrapped[NONCE_BYTES:], context_aad(context)
        )
        return open_with_data_key(data_key, envelope, context=context)
