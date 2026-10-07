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
    MemoryFilingAuthorizationStore,
    MemoryFilingCredentialStore,
)
from insolvia_core.adapters.memory.tax_id_cipher import LOCAL_MASTER_KEY
from insolvia_core.errors import ConflictError, FieldValidationError, NotFoundError
from insolvia_core.filing_authorization import (
    TEXT_VERSION,
    FilingAuthorization,
    sign_authorization,
    text_json,
)
from insolvia_core.filing_credentials import (
    CREDENTIAL_PURPOSE,
    AuthorizationRequiredError,
    CredentialSecret,
    CredentialUnavailableError,
    FilingCredential,
    credential_from_item,
    credential_item,
    credential_json,
    encryption_context,
    enrol_credential,
    is_openable,
    list_credentials,
    open_credential,
    parse_enrolment,
    revoke_credential,
    withdraw_authorization,
)

from tests import paths

FIRM = "firm-0001"
OTHER_FIRM = "firm-0002"
ATTORNEY = "00000000-0000-4000-8000-00000000a771"
OTHER_ATTORNEY = "00000000-0000-4000-8000-00000000b771"
FILING = "filing-0001"
LOGIN = "FAKE-ECF-USER"
PASSWORD = "FAKE-ECF-PASSWORD-not-a-real-one"
# A far-future sign-in instant (2100-01-01T00:00:00Z), so no test depends on
# today's clock.
SIGNED_IN_AT = 4_102_444_800


def fresh_seed() -> str:
    """A TOTP seed minted now: 20 random bytes, base32 — RFC 4226's size."""
    return base64.b32encode(os.urandom(20)).decode("ascii")


class Vault:
    """The memory composition: what the API holds (sealer, store, log) and,
    separately, what only the worker will (opener)."""

    def __init__(self, *, signed: tuple[str, ...] = (ATTORNEY, OTHER_ATTORNEY)) -> None:
        self.sealer = LocalCredentialSealer()
        self.opener = LocalCredentialOpener()
        self.store = MemoryFilingCredentialStore()
        self.authorizations = MemoryFilingAuthorizationStore()
        self.log = MemoryAccessLog()
        # Guardrail 2 is a precondition of every enrolment, so the vault
        # these tests share starts with both attorneys' authorizations
        # signed — and the log emptied, so each test's rows are its own.
        # test_filing_authorization owns what signing does.
        for attorney in signed:
            self.sign(attorney)
        self.log.events.clear()

    def sign(self, attorney: str = ATTORNEY) -> FilingAuthorization:
        return sign_authorization(
            {"text_version": TEXT_VERSION, "text_digest": text_json()["digest"]},
            firm_id=FIRM,
            attorney_id=attorney,
            authenticated_at=SIGNED_IN_AT,
            now=SIGNED_IN_AT + 30,
            store=self.authorizations,
            access_log=self.log,
        )

    def withdraw(self, attorney: str = ATTORNEY) -> int:
        return withdraw_authorization(
            firm_id=FIRM,
            attorney_id=attorney,
            authorizations=self.authorizations,
            store=self.store,
            access_log=self.log,
        )

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
            authorizations=self.authorizations,
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
            authorizations=self.authorizations,
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


# ── Guardrail 2: no current authorization, no credential ────────


def test_enrolment_without_a_signed_authorization_is_refused_before_sealing() -> None:
    vault = Vault(signed=())
    with pytest.raises(AuthorizationRequiredError):
        vault.enrol(seed=fresh_seed())
    assert vault.store.items == {}
    assert vault.log.events == []


def test_the_credential_records_the_signature_it_was_enrolled_under() -> None:
    vault = Vault()
    credential = vault.enrol(seed=fresh_seed())
    current = vault.authorizations.get_current(FIRM, ATTORNEY)
    assert current is not None
    assert credential.authorization_ref == current.authorization_id
    assert credential_item(credential)["authorizationRef"] == current.authorization_id


def test_withdrawing_destroys_every_credential_and_the_next_open_fails() -> None:
    vault = Vault()
    first = vault.enrol(seed=fresh_seed())
    second = vault.enrol(seed=fresh_seed(), login=f"{LOGIN}-2")
    assert vault.withdraw() == 2
    assert vault.store.items == {}
    assert vault.authorizations.get_current(FIRM, ATTORNEY) is None
    with pytest.raises(CredentialUnavailableError):
        vault.open(first.credential_id)
    actions = [(e.action, e.credential_id) for e in vault.log.events]
    assert actions[2:] == [
        ("credential.revoke", first.credential_id),
        ("credential.revoke", second.credential_id),
        ("authorization.withdraw", None),
        ("credential.open", first.credential_id),
    ]


def test_withdrawal_marks_the_history_record_and_keeps_it() -> None:
    vault = Vault()
    current = vault.authorizations.get_current(FIRM, ATTORNEY)
    assert current is not None
    vault.withdraw()
    kept = vault.authorizations.history[current.authorization_id]
    assert kept.status == "withdrawn"
    assert kept.withdrawn_at is not None
    assert kept.text_digest == current.text_digest


def test_a_credential_that_outlived_its_withdrawal_still_does_not_open() -> None:
    """The race: an enrolment that lands after the withdrawal's deletes. The
    authorization is gone first, so the straggler can never open."""
    vault = Vault()
    credential = vault.enrol(seed=fresh_seed())
    vault.withdraw()
    vault.store.create(credential)
    with pytest.raises(CredentialUnavailableError):
        vault.open(credential.credential_id)


def test_another_attorneys_withdrawal_leaves_mine_alone() -> None:
    vault = Vault()
    mine = vault.enrol(seed=fresh_seed())
    vault.enrol(seed=fresh_seed(), attorney=OTHER_ATTORNEY)
    assert vault.withdraw(OTHER_ATTORNEY) == 1
    assert vault.open(mine.credential_id).password == PASSWORD


def test_withdrawing_with_nothing_signed_is_not_found() -> None:
    vault = Vault(signed=())
    with pytest.raises(NotFoundError):
        vault.withdraw()


def test_a_credential_from_before_guardrail_2_never_opens() -> None:
    vault = Vault()
    credential = vault.enrol(seed=fresh_seed())
    legacy = replace(credential, authorization_ref=None)
    vault.store.items[(FIRM, ATTORNEY, credential.credential_id)] = legacy
    assert not is_openable(legacy, vault.authorizations.get_current(FIRM, ATTORNEY))
    with pytest.raises(CredentialUnavailableError):
        vault.open(credential.credential_id)


def test_a_new_text_version_stops_opens_and_enrolments_until_it_is_signed() -> None:
    """A signature over an older text is not current: the stored login is
    kept but cannot open, nothing new enrols, and signing the new version
    restores it — without re-entering the PACER password."""
    vault = Vault()
    credential = vault.enrol(seed=fresh_seed())
    held = vault.authorizations.current[(FIRM, ATTORNEY)]
    vault.authorizations.current[(FIRM, ATTORNEY)] = replace(
        held, text_version="2026-01-01-older"
    )
    with pytest.raises(CredentialUnavailableError):
        vault.open(credential.credential_id)
    with pytest.raises(AuthorizationRequiredError):
        vault.enrol(seed=fresh_seed(), login=f"{LOGIN}-2")

    vault.sign()

    assert vault.open(credential.credential_id).password == PASSWORD
    assert vault.authorizations.history[held.authorization_id].status == "superseded"


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
