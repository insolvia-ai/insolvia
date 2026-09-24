from __future__ import annotations

from collections.abc import Mapping
from typing import Any

import boto3

from insolvia_core.adapters.envelope import (
    open_with_data_key,
    seal_with_data_key,
    wrapped_key_bytes,
)
from insolvia_core.tax_ids import Envelope


def case_key_alias(case_table_name: str) -> str:
    """The case key's alias, from the case table's name.

    Both are `${project}-${environment}-cases` — ONE `local.name` in
    infra/modules/case_store/main.tf, which is what makes this a derivation
    rather than a coincidence: the table is `insolvia-<env>-cases` and its
    key is `alias/insolvia-<env>-cases`, in every environment including a
    developer's `insolvia-dev-<short-id>-cases`. So the ciphers need no
    configuration of their own — no SSM parameter, no `.env` line, nothing
    for scripts/dev-aws-setup.sh to write — and the deploy role's
    `DenyCaseDataDecryption` fence (`alias/insolvia-*-cases`) covers this
    key by the same pattern. The module's own comment names this function
    as the reason the two must stay one local.
    """
    return f"alias/{case_table_name}"


class KmsTaxIdCipher:
    """TaxIdCipher over the environment's case KMS key.

    `seal` is one `GenerateDataKey` (a fresh AES-256 key, returned plain and
    wrapped) and the shared AES-GCM seal; `open` is one `Decrypt` of the
    wrapped key under the same encryption context and the shared open. KMS
    refuses the unwrap under any other context, which is the replay
    protection the design counts on, and the IAM grant permits these two
    verbs only with `kms:EncryptionContext:purpose = debtor-tax-id`
    (infra/modules/case_store, TaxIdKeyUse) — the API role holds both, the
    worker role Decrypt alone, the MCP role neither.

    `KeyId` is passed on Decrypt as well as GenerateDataKey. Decrypt does not
    need it (the wrapped blob names its key), but passing it makes "this
    wrapped key was produced under the case key and no other" a check KMS
    performs rather than one this code assumes.
    """

    def __init__(self, key_id: str, *, client: Any = None) -> None:
        self.key_id = key_id
        self.client = client if client is not None else boto3.client("kms")

    def seal(self, plaintext: str, *, context: Mapping[str, str]) -> Envelope:
        response = self.client.generate_data_key(
            KeyId=self.key_id, KeySpec="AES_256", EncryptionContext=dict(context)
        )
        return seal_with_data_key(
            response["Plaintext"],
            plaintext,
            context=context,
            wrapped_key=response["CiphertextBlob"],
        )

    def open(self, envelope: Envelope, *, context: Mapping[str, str]) -> str:
        response = self.client.decrypt(
            KeyId=self.key_id,
            CiphertextBlob=wrapped_key_bytes(envelope),
            EncryptionContext=dict(context),
        )
        return open_with_data_key(response["Plaintext"], envelope, context=context)
