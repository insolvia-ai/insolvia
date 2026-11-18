"""The credential vault's KMS half — the dedicated key (insolvia_core.
filing_credentials owns the design; infra/modules/filing_credentials owns
what AWS enforces). The rows live in Postgres
(adapters/postgres/filing_credentials.py, ADR 0030).

NOTHING HERE IS CONFIGURED. The key alias is
`alias/insolvia-<env>-filing-credentials` — the module's `local.name` — and
is derived from the case table name every composer already holds
(`insolvia-<env>-cases`), the way `tax_id_cipher.case_key_alias` derives the
case key. So the vault needs no new environment variable, no SSM parameter
and no `.env` line, in any of the three environments.
"""

from __future__ import annotations

from collections.abc import Mapping
from typing import Any, Final

import boto3

from insolvia_core.adapters.envelope import (
    open_with_data_key,
    seal_with_data_key,
    wrapped_key_bytes,
)
from insolvia_core.tax_ids import Envelope

_CASE_TABLE_SUFFIX: Final = "-cases"
_VAULT_SUFFIX: Final = "-filing-credentials"


def filing_credentials_key_alias(case_table_name: str) -> str:
    """`insolvia-<env>-cases` → `alias/insolvia-<env>-filing-credentials`,
    exactly as the module names the key.

    Refuses a name that is not a case table's rather than guessing, because
    a wrong guess here is a key alias that names nothing the policy protects."""
    if not case_table_name.endswith(_CASE_TABLE_SUFFIX):
        raise ValueError(
            f"{case_table_name!r} is not an insolvia-<env>-cases table name"
        )
    return f"alias/{case_table_name[: -len(_CASE_TABLE_SUFFIX)]}{_VAULT_SUFFIX}"


class KmsCredentialSealer:
    """FilingCredentialSealer — one `GenerateDataKey` under the vault key and
    the shared AES-GCM seal. The API's whole use of the key: its grant and
    the key policy allow exactly this verb, under exactly this purpose."""

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


class KmsCredentialOpener:
    """FilingCredentialOpener — one `Decrypt` of the wrapped data key under
    the context it was sealed with, then the shared AES-GCM open.

    The filing worker's (ADR 0024 PR 7). Constructed anywhere else it does
    nothing useful: the key policy denies Decrypt to every principal but the
    worker's role, and KMS answers AccessDeniedException before any key
    material moves. `KeyId` is passed so "this blob was wrapped under the
    vault key and no other" is a check KMS performs.
    """

    def __init__(self, key_id: str, *, client: Any = None) -> None:
        self.key_id = key_id
        self.client = client if client is not None else boto3.client("kms")

    def open(self, envelope: Envelope, *, context: Mapping[str, str]) -> str:
        response = self.client.decrypt(
            KeyId=self.key_id,
            CiphertextBlob=wrapped_key_bytes(envelope),
            EncryptionContext=dict(context),
        )
        return open_with_data_key(response["Plaintext"], envelope, context=context)
