"""The credential vault over AWS — the dedicated table and the dedicated key
(insolvia_core.filing_credentials owns the design; infra/modules/
filing_credentials owns what AWS enforces).

NOTHING HERE IS CONFIGURED. The table and the key alias are both
`insolvia-<env>-filing-credentials` — one `local.name` in the module — and
both are derived from the case table name every composer already holds
(`insolvia-<env>-cases`), the way `tax_id_cipher.case_key_alias` derives the
case key. So enabling the vault needs no new environment variable, no SSM
parameter and no `.env` line, in any of the three environments.
"""

from __future__ import annotations

from collections.abc import Mapping
from dataclasses import replace
from typing import Any, Final

import boto3
from botocore.exceptions import ClientError

from insolvia_core.adapters.aws.dynamo import from_attributes, to_attributes
from insolvia_core.adapters.envelope import (
    open_with_data_key,
    seal_with_data_key,
    wrapped_key_bytes,
)
from insolvia_core.errors import ConflictError
from insolvia_core.filing_authorization import (
    CURRENT_SORT_KEY,
    FilingAuthorization,
    authorization_from_item,
    authorization_item,
    history_sort_key,
)
from insolvia_core.filing_credentials import (
    FilingCredential,
    credential_from_item,
    credential_item,
    partition_key,
    sort_key,
)
from insolvia_core.tax_ids import Envelope

_CASE_TABLE_SUFFIX: Final = "-cases"
_VAULT_SUFFIX: Final = "-filing-credentials"


def filing_credentials_table_name(case_table_name: str) -> str:
    """`insolvia-<env>-cases` → `insolvia-<env>-filing-credentials`.

    Refuses a name that is not a case table's rather than guessing, because
    a wrong guess here is a vault that silently writes nowhere the key policy
    protects."""
    if not case_table_name.endswith(_CASE_TABLE_SUFFIX):
        raise ValueError(
            f"{case_table_name!r} is not an insolvia-<env>-cases table name"
        )
    return case_table_name[: -len(_CASE_TABLE_SUFFIX)] + _VAULT_SUFFIX


def filing_credentials_key_alias(case_table_name: str) -> str:
    """The vault key's alias — the table's name under `alias/`, exactly as
    the module names it."""
    return f"alias/{filing_credentials_table_name(case_table_name)}"


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


class DynamoDbFilingCredentialStore:
    """FilingCredentialStore over the dedicated vault table. Both key halves
    come from the functions that own them (`partition_key`, `sort_key`)."""

    def __init__(self, table_name: str, *, client: Any = None) -> None:
        self.table_name = table_name
        self.client = client if client is not None else boto3.client("dynamodb")

    def _key(
        self, firm_id: str, attorney_id: str, credential_id: str
    ) -> dict[str, Any]:
        return {
            "PK": {"S": partition_key(firm_id, attorney_id)},
            "SK": {"S": sort_key(credential_id)},
        }

    def create(self, credential: FilingCredential) -> None:
        # Ids are server-minted uuid4s, so the condition should never fire —
        # and a replace here would be a second way to overwrite a sealed
        # secret, which is what revoke-then-enrol exists to prevent.
        try:
            self.client.put_item(
                TableName=self.table_name,
                Item=to_attributes(credential_item(credential)),
                ConditionExpression="attribute_not_exists(SK)",
            )
        except ClientError as error:
            if (
                error.response.get("Error", {}).get("Code")
                == "ConditionalCheckFailedException"
            ):
                raise RuntimeError("credential id already exists") from error
            raise

    def get(
        self, firm_id: str, attorney_id: str, credential_id: str
    ) -> FilingCredential | None:
        response = self.client.get_item(
            TableName=self.table_name,
            Key=self._key(firm_id, attorney_id, credential_id),
            ConsistentRead=True,
        )
        item = response.get("Item")
        return credential_from_item(from_attributes(item)) if item else None

    def list_for_attorney(
        self, firm_id: str, attorney_id: str
    ) -> tuple[FilingCredential, ...]:
        found: list[FilingCredential] = []
        kwargs: dict[str, Any] = {
            "TableName": self.table_name,
            "KeyConditionExpression": "PK = :pk AND begins_with(SK, :sk)",
            "ExpressionAttributeValues": {
                ":pk": {"S": partition_key(firm_id, attorney_id)},
                ":sk": {"S": sort_key("")},
            },
            "ConsistentRead": True,
        }
        while True:
            response = self.client.query(**kwargs)
            found.extend(
                credential_from_item(from_attributes(item))
                for item in response.get("Items", [])
            )
            last = response.get("LastEvaluatedKey")
            if not last:
                break
            kwargs["ExclusiveStartKey"] = last
        return tuple(sorted(found, key=lambda c: (c.created_at, c.credential_id)))

    def delete(self, firm_id: str, attorney_id: str, credential_id: str) -> bool:
        response = self.client.delete_item(
            TableName=self.table_name,
            Key=self._key(firm_id, attorney_id, credential_id),
            ReturnValues="ALL_OLD",
        )
        return bool(response.get("Attributes"))


def _condition_failed(error: ClientError) -> bool:
    code: object = error.response.get("Error", {}).get("Code")
    return code == "ConditionalCheckFailedException"


class DynamoDbFilingAuthorizationStore:
    """FilingAuthorizationStore over the SAME vault table, in the attorney's
    partition: `SK = AUTHORIZATION` (the current one, at a fixed key the
    worker's GetItem-only grant can read) and `SK = AUTHORIZATION#<id>` (the
    history). PutItem, GetItem and DeleteItem only — exactly the verbs the
    API's grant on this table already holds (infra/modules/
    filing_credentials), so guardrail 2 needs no IAM change.

    Not transactional, and the write ORDER is what makes that safe:
    `put_current` writes the current item first (a lost race fails there,
    before any history exists), and `withdraw` deletes it first (from that
    write on nothing can open, whatever happens to the history update).
    """

    def __init__(self, table_name: str, *, client: Any = None) -> None:
        self.table_name = table_name
        self.client = client if client is not None else boto3.client("dynamodb")

    def _item(
        self, authorization: FilingAuthorization, sort_key_value: str
    ) -> dict[str, Any]:
        item = {
            "PK": partition_key(authorization.firm_id, authorization.attorney_id),
            **authorization_item(authorization, sort_key=sort_key_value),
        }
        return to_attributes(item)

    def get_current(self, firm_id: str, attorney_id: str) -> FilingAuthorization | None:
        response = self.client.get_item(
            TableName=self.table_name,
            Key={
                "PK": {"S": partition_key(firm_id, attorney_id)},
                "SK": {"S": CURRENT_SORT_KEY},
            },
            ConsistentRead=True,
        )
        item = response.get("Item")
        return authorization_from_item(from_attributes(item)) if item else None

    def put_current(
        self,
        authorization: FilingAuthorization,
        *,
        replacing: FilingAuthorization | None,
    ) -> None:
        condition: dict[str, Any]
        if replacing is None:
            condition = {"ConditionExpression": "attribute_not_exists(SK)"}
        else:
            condition = {
                "ConditionExpression": "authorizationId = :held",
                "ExpressionAttributeValues": {
                    ":held": {"S": replacing.authorization_id}
                },
            }
        try:
            self.client.put_item(
                TableName=self.table_name,
                Item=self._item(authorization, CURRENT_SORT_KEY),
                **condition,
            )
        except ClientError as error:
            if _condition_failed(error):
                raise ConflictError("the authorization changed") from error
            raise
        self.client.put_item(
            TableName=self.table_name,
            Item=self._item(
                authorization, history_sort_key(authorization.authorization_id)
            ),
            ConditionExpression="attribute_not_exists(SK)",
        )
        if replacing is not None:
            self.client.put_item(
                TableName=self.table_name,
                Item=self._item(
                    replace(replacing, status="superseded"),
                    history_sort_key(replacing.authorization_id),
                ),
            )

    def withdraw(
        self, authorization: FilingAuthorization, *, withdrawn_at: str
    ) -> bool:
        pk = partition_key(authorization.firm_id, authorization.attorney_id)
        try:
            self.client.delete_item(
                TableName=self.table_name,
                Key={"PK": {"S": pk}, "SK": {"S": CURRENT_SORT_KEY}},
                ConditionExpression="authorizationId = :held",
                ExpressionAttributeValues={
                    ":held": {"S": authorization.authorization_id}
                },
            )
        except ClientError as error:
            if _condition_failed(error):
                return False
            raise
        self.client.put_item(
            TableName=self.table_name,
            Item=self._item(
                replace(authorization, status="withdrawn", withdrawn_at=withdrawn_at),
                history_sort_key(authorization.authorization_id),
            ),
        )
        return True
