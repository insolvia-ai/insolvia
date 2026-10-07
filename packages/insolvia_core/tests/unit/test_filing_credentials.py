"""The credential vault (ADR 0024, guardrail 3): enrol seals, status never
shows secret material, revoke destroys, every open is logged first, and an
envelope opens only under the context it was sealed with.

NO REAL CREDENTIAL APPEARS HERE, OR ANYWHERE. The login is the literal
`FAKE-ECF-USER`, the password is a fixed obviously-fake string, and the TOTP
seed is GENERATED AT TEST TIME from os.urandom — so it is not even stable
between runs, let alone anybody's. This repo is public.
"""

from __future__ import annotations

import base64
import os
import re
from dataclasses import replace
from typing import Any

import pytest
from cryptography.exceptions import InvalidTag
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from insolvia_core.access_log import ACTIONS, access_item
from insolvia_core.adapters.aws.dynamo import from_attributes
from insolvia_core.adapters.aws.filing_credentials import (
    DynamoDbFilingCredentialStore,
    KmsCredentialOpener,
    KmsCredentialSealer,
    filing_credentials_key_alias,
    filing_credentials_table_name,
)
from insolvia_core.adapters.envelope import NONCE_BYTES, context_aad, new_data_key
from insolvia_core.adapters.memory.access_log import MemoryAccessLog
from insolvia_core.adapters.memory.filing_credentials import (
    LocalCredentialOpener,
    LocalCredentialSealer,
    MemoryFilingCredentialStore,
)
from insolvia_core.adapters.memory.tax_id_cipher import LOCAL_MASTER_KEY
from insolvia_core.errors import ConflictError, FieldValidationError
from insolvia_core.filing_credentials import (
    CREDENTIAL_PURPOSE,
    CredentialSecret,
    CredentialUnavailableError,
    FilingCredential,
    credential_from_item,
    credential_item,
    credential_json,
    encryption_context,
    enrol_credential,
    list_credentials,
    open_credential,
    parse_enrolment,
    revoke_credential,
)

from tests import paths

FIRM = "firm-0001"
OTHER_FIRM = "firm-0002"
ATTORNEY = "00000000-0000-4000-8000-00000000a771"
OTHER_ATTORNEY = "00000000-0000-4000-8000-00000000b771"
FILING = "filing-0001"
LOGIN = "FAKE-ECF-USER"
PASSWORD = "FAKE-ECF-PASSWORD-not-a-real-one"


def fresh_seed() -> str:
    """A TOTP seed minted now: 20 random bytes, base32 — RFC 4226's size."""
    return base64.b32encode(os.urandom(20)).decode("ascii")


class Vault:
    """The memory composition: what the API holds (sealer, store, log) and,
    separately, what only the worker will (opener)."""

    def __init__(self) -> None:
        self.sealer = LocalCredentialSealer()
        self.opener = LocalCredentialOpener()
        self.store = MemoryFilingCredentialStore()
        self.log = MemoryAccessLog()

    def enrol(
        self, *, seed: str, login: str = LOGIN, attorney: str = ATTORNEY
    ) -> FilingCredential:
        return enrol_credential(
            parse_enrolment({"login": login, "password": PASSWORD, "totp_seed": seed}),
            firm_id=FIRM,
            attorney_id=attorney,
            sealer=self.sealer,
            store=self.store,
            access_log=self.log,
        )

    def open(
        self, credential_id: str, *, attorney: str = ATTORNEY, firm: str = FIRM
    ) -> CredentialSecret:
        return open_credential(
            firm_id=firm,
            attorney_id=attorney,
            credential_id=credential_id,
            filing_id=FILING,
            purpose="sign_in",
            opener=self.opener,
            store=self.store,
            access_log=self.log,
        )


# ── Parsing ─────────────────────────────────────────────────────


class TestParsing:
    def test_a_complete_enrolment_parses(self) -> None:
        seed = fresh_seed()
        enrolment = parse_enrolment(
            {
                "login": LOGIN,
                "password": PASSWORD,
                "totp_seed": seed,
                "courts": ["txsb", "flmb", "txsb"],
            }
        )
        assert enrolment.login == LOGIN
        assert enrolment.secret == CredentialSecret(password=PASSWORD, totp_seed=seed)
        assert enrolment.courts == ("flmb", "txsb")

    def test_a_seed_shown_grouped_and_lowercase_is_normalised(self) -> None:
        seed = fresh_seed()
        grouped = " ".join(seed[i : i + 4] for i in range(0, len(seed), 4)).lower()
        enrolment = parse_enrolment(
            {"login": LOGIN, "password": PASSWORD, "totp_seed": grouped}
        )
        assert enrolment.secret.totp_seed == seed

    @pytest.mark.parametrize(
        ("body", "field"),
        [
            ({"password": PASSWORD, "totp_seed": "A" * 32}, "login"),
            (
                {"login": "has space", "password": PASSWORD, "totp_seed": "A" * 32},
                "login",
            ),
            ({"login": LOGIN, "totp_seed": "A" * 32}, "password"),
            (
                {"login": LOGIN, "password": "x" * 129, "totp_seed": "A" * 32},
                "password",
            ),
            (
                {"login": LOGIN, "password": "tab\there", "totp_seed": "A" * 32},
                "password",
            ),
            ({"login": LOGIN, "password": PASSWORD, "totp_seed": "ABC"}, "totp_seed"),
            (
                {"login": LOGIN, "password": PASSWORD, "totp_seed": "0" * 32},
                "totp_seed",
            ),
            ({"login": LOGIN, "password": PASSWORD}, "totp_seed"),
            (
                {
                    "login": LOGIN,
                    "password": PASSWORD,
                    "totp_seed": "A" * 32,
                    "courts": ["zzzz"],
                },
                "courts",
            ),
            (
                {
                    "login": LOGIN,
                    "password": PASSWORD,
                    "totp_seed": "A" * 32,
                    "courts": "txsb",
                },
                "courts",
            ),
        ],
    )
    def test_each_problem_is_reported_on_its_field(
        self, body: dict[str, object], field: str
    ) -> None:
        with pytest.raises(FieldValidationError) as caught:
            parse_enrolment(body)
        assert field in caught.value.fields

    def test_a_refusal_never_echoes_what_was_submitted(self) -> None:
        secret_ish = "NOT-ECHOED-" + fresh_seed()
        with pytest.raises(FieldValidationError) as caught:
            parse_enrolment(
                {"login": secret_ish + " x", "password": "", "totp_seed": secret_ish}
            )
        assert secret_ish not in repr(caught.value.fields)

    def test_the_secret_does_not_print(self) -> None:
        secret = CredentialSecret(password=PASSWORD, totp_seed=fresh_seed())
        assert PASSWORD not in repr(secret)
        assert PASSWORD not in repr(
            parse_enrolment(
                {"login": LOGIN, "password": PASSWORD, "totp_seed": secret.totp_seed}
            )
        )


# ── Enrol and status ────────────────────────────────────────────


def test_enrolment_seals_and_the_stored_item_holds_no_plaintext() -> None:
    vault = Vault()
    seed = fresh_seed()
    credential = vault.enrol(seed=seed)
    stored = credential_item(
        vault.store.items[(FIRM, ATTORNEY, credential.credential_id)]
    )
    flat = repr(stored)
    assert PASSWORD not in flat
    assert seed not in flat
    assert stored["login"] == LOGIN
    assert stored["status"] == "active"


def test_the_status_view_is_pinned_and_carries_no_secret_material() -> None:
    vault = Vault()
    seed = fresh_seed()
    credential = vault.enrol(seed=seed)
    view = credential_json(credential)
    # PINNED. A field added to the item must be added here on purpose.
    assert set(view) == {"id", "login", "courts", "status", "created_at", "updated_at"}
    flat = repr(view)
    for secret in (
        PASSWORD,
        seed,
        credential.envelope.ciphertext,
        credential.envelope.wrapped_key,
    ):
        assert secret not in flat


def test_enrolment_is_logged_under_the_credential() -> None:
    vault = Vault()
    credential = vault.enrol(seed=fresh_seed())
    (event,) = vault.log.events
    assert event.action == "credential.enrol"
    assert event.credential_id == credential.credential_id
    assert event.principal == ATTORNEY
    assert event.case_id is None


def test_the_same_login_twice_is_a_conflict_whatever_its_case() -> None:
    vault = Vault()
    vault.enrol(seed=fresh_seed())
    with pytest.raises(ConflictError):
        vault.enrol(seed=fresh_seed(), login=LOGIN.lower())


def test_another_attorney_may_enrol_the_same_login_name() -> None:
    vault = Vault()
    vault.enrol(seed=fresh_seed())
    vault.enrol(seed=fresh_seed(), attorney=OTHER_ATTORNEY)
    assert (
        len(list_credentials(firm_id=FIRM, attorney_id=ATTORNEY, store=vault.store))
        == 1
    )


# ── Open ────────────────────────────────────────────────────────


def test_the_worker_opens_what_the_api_sealed() -> None:
    vault = Vault()
    seed = fresh_seed()
    credential = vault.enrol(seed=seed)
    assert vault.open(credential.credential_id) == CredentialSecret(
        password=PASSWORD, totp_seed=seed
    )


def test_every_open_records_the_attorney_the_filing_and_the_purpose() -> None:
    vault = Vault()
    credential = vault.enrol(seed=fresh_seed())
    vault.open(credential.credential_id)
    event = vault.log.events[-1]
    assert event.action == "credential.open"
    assert event.credential_id == credential.credential_id
    assert event.principal == ATTORNEY
    assert event.filing_id == FILING
    assert event.purpose == "sign_in"
    item = access_item(event)
    assert item["PK"] == f"CREDENTIAL#{credential.credential_id}"
    assert item["filingId"] == FILING
    assert item["purpose"] == "sign_in"


def test_revoke_destroys_the_item_and_the_next_open_fails_and_is_still_logged() -> None:
    vault = Vault()
    credential = vault.enrol(seed=fresh_seed())
    assert revoke_credential(
        credential.credential_id,
        firm_id=FIRM,
        attorney_id=ATTORNEY,
        store=vault.store,
        access_log=vault.log,
    )
    assert vault.store.items == {}
    with pytest.raises(CredentialUnavailableError):
        vault.open(credential.credential_id)
    actions = [e.action for e in vault.log.events]
    assert actions == ["credential.enrol", "credential.revoke", "credential.open"]


def test_another_attorney_cannot_revoke_it() -> None:
    vault = Vault()
    credential = vault.enrol(seed=fresh_seed())
    assert not revoke_credential(
        credential.credential_id,
        firm_id=FIRM,
        attorney_id=OTHER_ATTORNEY,
        store=vault.store,
        access_log=vault.log,
    )
    assert vault.log.events[-1].outcome == "denied"
    assert vault.open(credential.credential_id).password == PASSWORD


def test_an_envelope_copied_onto_another_attorneys_item_does_not_open() -> None:
    """The encryption context is the binding: the same ciphertext, re-addressed
    to another attorney (or firm, or credential), fails at the unwrap."""
    vault = Vault()
    victim = vault.enrol(seed=fresh_seed())
    thief = vault.enrol(seed=fresh_seed(), attorney=OTHER_ATTORNEY)
    vault.store.items[(FIRM, OTHER_ATTORNEY, thief.credential_id)] = replace(
        thief, envelope=victim.envelope
    )
    with pytest.raises(InvalidTag):
        vault.open(thief.credential_id, attorney=OTHER_ATTORNEY)


@pytest.mark.parametrize(
    "wrong",
    [
        {"firm_id": OTHER_FIRM},
        {"attorney_id": OTHER_ATTORNEY},
        {"credential_id": "another-credential"},
    ],
)
def test_the_wrong_context_is_refused(wrong: dict[str, str]) -> None:
    vault = Vault()
    credential = vault.enrol(seed=fresh_seed())
    context = {
        **encryption_context(
            firm_id=FIRM, attorney_id=ATTORNEY, credential_id=credential.credential_id
        ),
        **wrong,
    }
    with pytest.raises(InvalidTag):
        vault.opener.open(credential.envelope, context=context)


def test_the_context_names_the_purpose_the_key_policy_conditions_on() -> None:
    assert encryption_context(
        firm_id=FIRM, attorney_id=ATTORNEY, credential_id="c1"
    ) == {
        "purpose": "filing-credential",
        "firm_id": FIRM,
        "attorney_id": ATTORNEY,
        "credential_id": "c1",
    }
    # THE SAME STRING THE INFRA CONDITIONS ON. Renaming one side alone fails
    # every deployed enrolment with AccessDenied; this fails first, here.
    module = (paths.REPO_ROOT / "infra/modules/filing_credentials/main.tf").read_text()
    assert re.search(
        r'credential_purpose\s*=\s*"' + re.escape(CREDENTIAL_PURPOSE) + '"', module
    )


def test_the_item_round_trips() -> None:
    vault = Vault()
    credential = vault.enrol(seed=fresh_seed())
    assert credential_from_item(credential_item(credential)) == credential


def test_the_access_actions_are_registered() -> None:
    assert {"credential.enrol", "credential.revoke", "credential.open"} <= set(ACTIONS)


# ── The AWS adapters, with the transports faked at the client ───


class FakeKms:
    """GenerateDataKey / Decrypt as KMS answers them, wrapped under a local
    key so the ciphertexts are real and a mismatched context refuses."""

    def __init__(self) -> None:
        self.calls: list[tuple[str, dict[str, Any]]] = []

    def generate_data_key(self, **kwargs: Any) -> dict[str, bytes]:
        self.calls.append(("generate_data_key", kwargs))
        key = new_data_key()
        nonce = os.urandom(NONCE_BYTES)
        wrapped = nonce + AESGCM(LOCAL_MASTER_KEY).encrypt(
            nonce, key, context_aad(kwargs["EncryptionContext"])
        )
        return {"Plaintext": key, "CiphertextBlob": wrapped}

    def decrypt(self, **kwargs: Any) -> dict[str, bytes]:
        self.calls.append(("decrypt", kwargs))
        blob = kwargs["CiphertextBlob"]
        key = AESGCM(LOCAL_MASTER_KEY).decrypt(
            blob[:NONCE_BYTES],
            blob[NONCE_BYTES:],
            context_aad(kwargs["EncryptionContext"]),
        )
        return {"Plaintext": key}


ALIAS = "alias/insolvia-staging-filing-credentials"


def test_the_sealer_only_ever_generates_a_data_key() -> None:
    kms = FakeKms()
    context = encryption_context(firm_id=FIRM, attorney_id=ATTORNEY, credential_id="c1")
    KmsCredentialSealer(ALIAS, client=kms).seal("plaintext", context=context)
    assert [name for name, _ in kms.calls] == ["generate_data_key"]
    assert kms.calls[0][1] == {
        "KeyId": ALIAS,
        "KeySpec": "AES_256",
        "EncryptionContext": context,
    }


def test_the_sealer_has_no_way_to_open() -> None:
    assert not hasattr(KmsCredentialSealer(ALIAS, client=FakeKms()), "open")
    assert not hasattr(LocalCredentialSealer(), "open")


def test_the_kms_opener_opens_under_the_sealing_context_only() -> None:
    kms = FakeKms()
    context = encryption_context(firm_id=FIRM, attorney_id=ATTORNEY, credential_id="c1")
    envelope = KmsCredentialSealer(ALIAS, client=kms).seal("plaintext", context=context)
    opener = KmsCredentialOpener(ALIAS, client=kms)
    assert opener.open(envelope, context=context) == "plaintext"
    assert kms.calls[-1][1]["KeyId"] == ALIAS
    with pytest.raises(InvalidTag):
        opener.open(envelope, context={**context, "attorney_id": OTHER_ATTORNEY})


def test_the_names_are_derived_from_the_case_table() -> None:
    assert filing_credentials_table_name("insolvia-staging-cases") == (
        "insolvia-staging-filing-credentials"
    )
    assert filing_credentials_key_alias("insolvia-dev-0123456789ab-cases") == (
        "alias/insolvia-dev-0123456789ab-filing-credentials"
    )
    with pytest.raises(ValueError, match="not an insolvia-<env>-cases"):
        filing_credentials_table_name("insolvia-staging-firms")


class FakeDynamo:
    """put/get/query/delete over attribute-value items — enough to prove the
    store's keys and its conditional create."""

    def __init__(self) -> None:
        self.items: dict[tuple[str, str], dict[str, Any]] = {}

    def put_item(self, **kwargs: Any) -> None:
        item = kwargs["Item"]
        key = (item["PK"]["S"], item["SK"]["S"])
        if "ConditionExpression" in kwargs and key in self.items:
            from botocore.exceptions import ClientError

            raise ClientError(
                {"Error": {"Code": "ConditionalCheckFailedException"}}, "PutItem"
            )
        self.items[key] = item

    def get_item(self, **kwargs: Any) -> dict[str, Any]:
        assert kwargs["ConsistentRead"] is True
        key = (kwargs["Key"]["PK"]["S"], kwargs["Key"]["SK"]["S"])
        return {"Item": self.items[key]} if key in self.items else {}

    def query(self, **kwargs: Any) -> dict[str, Any]:
        pk = kwargs["ExpressionAttributeValues"][":pk"]["S"]
        return {"Items": [v for (p, _), v in self.items.items() if p == pk]}

    def delete_item(self, **kwargs: Any) -> dict[str, Any]:
        key = (kwargs["Key"]["PK"]["S"], kwargs["Key"]["SK"]["S"])
        old = self.items.pop(key, None)
        return {"Attributes": old} if old else {}


def test_the_dynamodb_store_keys_by_attorney_and_destroys_on_delete() -> None:
    dynamo = FakeDynamo()
    store = DynamoDbFilingCredentialStore("t", client=dynamo)
    credential = Vault().enrol(seed=fresh_seed())
    store.create(credential)
    ((pk, sk),) = dynamo.items
    assert pk == f"ATTORNEY#{FIRM}#{ATTORNEY}"
    assert sk == f"CREDENTIAL#{credential.credential_id}"
    assert from_attributes(dynamo.items[(pk, sk)])["courts"] == []
    assert store.get(FIRM, ATTORNEY, credential.credential_id) == credential
    assert store.list_for_attorney(FIRM, ATTORNEY) == (credential,)
    assert store.list_for_attorney(FIRM, OTHER_ATTORNEY) == ()
    with pytest.raises(RuntimeError):
        store.create(credential)
    assert store.delete(FIRM, ATTORNEY, credential.credential_id)
    assert not store.delete(FIRM, ATTORNEY, credential.credential_id)
    assert store.get(FIRM, ATTORNEY, credential.credential_id) is None
