"""The debtor's tax identifier (issue 13.12 / #382): the SSN/ITIN shape rules,
the sealed item, and the two reads — through the memory adapters, which run
the same envelope code the KMS cipher does minus the KMS call.

Every number below is synthetic. The SSN fixtures come from the SSA's
advertising block (987-65-4320..4329), which the Administration has said it
will never issue — the reason tax_ids.py accepts it despite the 9xx rule. No
such block exists for ITINs, so the ITIN fixtures are merely structurally
valid and describe nobody. This repo is public.
"""

from __future__ import annotations

import os

import pytest
from cryptography.exceptions import InvalidTag
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from insolvia_core.access_log import ACTIONS, access_item, record_access
from insolvia_core.adapters.aws.dynamo import from_attributes, to_attributes
from insolvia_core.adapters.aws.tax_id_cipher import KmsTaxIdCipher, case_key_alias
from insolvia_core.adapters.envelope import NONCE_BYTES, context_aad, new_data_key
from insolvia_core.adapters.memory.access_log import MemoryAccessLog
from insolvia_core.adapters.memory.tax_id_cipher import LocalTaxIdCipher
from insolvia_core.adapters.memory.tax_id_store import MemoryTaxIdStore
from insolvia_core.errors import FieldValidationError
from insolvia_core.tax_ids import (
    TAX_ID_PURPOSE,
    TaxIdInput,
    TaxIdRef,
    encryption_context,
    format_for_b121,
    parse_tax_id,
    read_tax_id,
    store_tax_id,
    tax_id_from_item,
    tax_id_item,
    tax_id_json,
)

FIRM = "firm-0001"
OTHER_FIRM = "firm-0002"
CASE = "case-0001"
SSN = "987654321"
ITIN = "987654329"


def parse(value: object) -> TaxIdInput | None:
    errors: dict[str, str] = {}
    result = parse_tax_id(value, "tax_id", errors)
    if errors:
        raise FieldValidationError(errors)
    return result


# ── The shape rules ─────────────────────────────────────────────


class TestParsing:
    def test_absent_is_absent(self) -> None:
        assert parse(None) is None

    @pytest.mark.parametrize("spelling", ["987-65-4321", "987654321", "987 65 4321"])
    def test_dashes_and_spaces_are_stripped(self, spelling: str) -> None:
        result = parse({"kind": "ssn", "value": spelling})
        assert result == TaxIdInput(kind="ssn", value=SSN)

    def test_an_itin_is_nine_digits_starting_with_nine(self) -> None:
        assert parse({"kind": "itin", "value": "987-65-4329"}) == TaxIdInput(
            kind="itin", value=ITIN
        )

    @pytest.mark.parametrize(
        ("kind", "value", "path"),
        [
            # Not nine digits at all.
            ("ssn", "12345", "tax_id.value"),
            ("ssn", "987-65-432A", "tax_id.value"),
            # SSN area rules: 000, 666, 9xx (outside the advertising block).
            ("ssn", "000-12-3456", "tax_id.value"),
            ("ssn", "666-12-3456", "tax_id.value"),
            ("ssn", "900-12-3456", "tax_id.value"),
            ("ssn", "987-65-4330", "tax_id.value"),
            # SSN group and serial rules.
            ("ssn", "123-00-3456", "tax_id.value"),
            ("ssn", "123-45-0000", "tax_id.value"),
            # ITIN prefix and group rules.
            ("itin", "123-45-6789", "tax_id.value"),
            ("itin", "987-66-4329", "tax_id.value"),
            ("itin", "987-89-4329", "tax_id.value"),
            # An unknown kind.
            ("ein", "987-65-4321", "tax_id.kind"),
        ],
    )
    def test_rejects_a_value_that_is_not_the_claimed_kind(
        self, kind: str, value: str, path: str
    ) -> None:
        with pytest.raises(FieldValidationError) as caught:
            parse({"kind": kind, "value": value})
        assert path in caught.value.fields

    @pytest.mark.parametrize("group", ["50", "65", "70", "88", "90", "92", "94", "99"])
    def test_accepts_every_itin_group_range_boundary(self, group: str) -> None:
        assert parse({"kind": "itin", "value": f"900{group}1234"}) is not None

    def test_the_advertising_block_is_accepted_as_an_ssn(self) -> None:
        # The one deliberate exception: a number the SSA will never issue,
        # so a committed fixture passes the same parser a request does.
        for serial in range(4320, 4330):
            assert parse({"kind": "ssn", "value": f"98765{serial}"}) is not None

    def test_the_last_four_alone_means_keep(self) -> None:
        assert parse({"kind": "ssn", "last_four": "4321"}) == TaxIdInput(
            kind="ssn", last_four="4321"
        )

    @pytest.mark.parametrize("kept", ["432", "43210", "abcd", 4321])
    def test_a_malformed_last_four_is_refused(self, kept: object) -> None:
        with pytest.raises(FieldValidationError) as caught:
            parse({"kind": "ssn", "last_four": kept})
        assert "tax_id.last_four" in caught.value.fields

    def test_both_or_neither_member_is_refused(self) -> None:
        with pytest.raises(FieldValidationError) as caught:
            parse({"kind": "ssn", "value": "987-65-4321", "last_four": "4321"})
        assert "tax_id" in caught.value.fields
        with pytest.raises(FieldValidationError) as caught:
            parse({"kind": "ssn"})
        assert "tax_id" in caught.value.fields

    def test_a_non_object_is_refused(self) -> None:
        with pytest.raises(FieldValidationError) as caught:
            parse("987-65-4321")
        assert "tax_id" in caught.value.fields


class TestPrinting:
    def test_an_ssn_prints_with_dashes(self) -> None:
        assert format_for_b121("ssn", SSN) == "987-65-4321"

    def test_an_itin_prints_the_ten_characters_after_the_preprinted_nine(self) -> None:
        # forms/specs/b121.json: the box caps at 10 and the leading 9 is
        # pre-printed, so the value is the number after it.
        assert format_for_b121("itin", ITIN) == "87-65-4329"

    def test_the_view_is_the_kind_and_the_last_four_only(self) -> None:
        assert tax_id_json(TaxIdRef(kind="ssn", last_four="4321", ref="r1")) == {
            "kind": "ssn",
            "last_four": "4321",
        }


# ── The write, the item, and the logged read ────────────────────


class Env:
    def __init__(self) -> None:
        self.cipher = LocalTaxIdCipher()
        self.store = MemoryTaxIdStore()
        self.log = MemoryAccessLog()

    def store_(self, given: TaxIdInput | None, existing: TaxIdRef | None = None):
        return store_tax_id(
            given,
            existing=existing,
            firm_id=FIRM,
            case_id=CASE,
            cipher=self.cipher,
            store=self.store,
        )

    def read(self, ref: TaxIdRef, *, firm_id: str = FIRM, purpose: str = "b121"):
        return read_tax_id(
            ref,
            firm_id=firm_id,
            case_id=CASE,
            filing_role="debtor_1",
            principal="subject-0001",
            purpose=purpose,
            cipher=self.cipher,
            store=self.store,
            access_log=self.log,
        )


def test_a_number_is_sealed_and_the_debtor_gets_a_reference() -> None:
    env = Env()
    ref = env.store_(TaxIdInput(kind="ssn", value=SSN))
    assert ref is not None
    assert (ref.kind, ref.last_four) == ("ssn", "4321")
    sealed = env.store.get(CASE, ref.ref)
    assert sealed is not None
    # The digits are nowhere in the item — only their last four.
    assert SSN not in str(tax_id_item(sealed))
    assert sealed.last_four == "4321"
    assert sealed.firm_id == FIRM


def test_the_logged_read_opens_the_number() -> None:
    env = Env()
    ref = env.store_(TaxIdInput(kind="ssn", value=SSN))
    assert ref is not None
    assert env.read(ref) == SSN


def test_the_read_records_who_which_debtor_and_why() -> None:
    env = Env()
    ref = env.store_(TaxIdInput(kind="itin", value=ITIN))
    assert ref is not None
    env.read(ref, purpose="b121")
    (event,) = env.log.events
    assert event.action == "taxid.read"
    assert "taxid.read" in ACTIONS
    assert event.principal == "subject-0001"
    assert event.case_id == CASE
    assert event.filing_role == "debtor_1"
    assert event.purpose == "b121"
    item = access_item(event)
    assert item["filingRole"] == "debtor_1"
    assert item["purpose"] == "b121"


def test_an_ordinary_row_carries_neither_extra_member() -> None:
    item = access_item(record_access(case_id=CASE, principal="p", action="case.read"))
    assert "filingRole" not in item
    assert "purpose" not in item


def test_the_read_is_recorded_even_when_the_item_is_missing() -> None:
    env = Env()
    dangling = TaxIdRef(kind="ssn", last_four="4321", ref="never-written")
    assert env.read(dangling) is None
    assert [e.action for e in env.log.events] == ["taxid.read"]


def test_another_firm_cannot_open_the_envelope() -> None:
    # The encryption context binds the firm: the same store, the same ref,
    # a different firm — refused by the tag, never answered with the number.
    env = Env()
    ref = env.store_(TaxIdInput(kind="ssn", value=SSN))
    assert ref is not None
    with pytest.raises(InvalidTag):
        env.read(ref, firm_id=OTHER_FIRM)


def test_a_ciphertext_cannot_be_replayed_under_another_ref() -> None:
    env = Env()
    ref = env.store_(TaxIdInput(kind="ssn", value=SSN))
    assert ref is not None
    sealed = env.store.get(CASE, ref.ref)
    assert sealed is not None
    with pytest.raises(InvalidTag):
        env.cipher.open(
            sealed.envelope, context=encryption_context(firm_id=FIRM, ref="other-ref")
        )


def test_keeping_the_stored_number_echoes_the_view() -> None:
    env = Env()
    stored = env.store_(TaxIdInput(kind="ssn", value=SSN))
    kept = env.store_(TaxIdInput(kind="ssn", last_four="4321"), existing=stored)
    assert kept == stored


@pytest.mark.parametrize(
    "echo",
    [
        TaxIdInput(kind="ssn", last_four="9999"),
        TaxIdInput(kind="itin", last_four="4321"),
    ],
)
def test_an_echo_that_does_not_match_is_refused(echo: TaxIdInput) -> None:
    env = Env()
    stored = env.store_(TaxIdInput(kind="ssn", value=SSN))
    with pytest.raises(FieldValidationError) as caught:
        env.store_(echo, existing=stored)
    assert "tax_id" in caught.value.fields


def test_keeping_with_nothing_stored_is_refused() -> None:
    env = Env()
    with pytest.raises(FieldValidationError):
        env.store_(TaxIdInput(kind="ssn", last_four="4321"), existing=None)


def test_a_corrected_number_keeps_the_ref_and_replaces_the_item() -> None:
    # A correction reaches every matter pointing at the ref (ADR 0022).
    env = Env()
    first = env.store_(TaxIdInput(kind="ssn", value="987654321"))
    second = env.store_(TaxIdInput(kind="ssn", value="987654322"), existing=first)
    assert first is not None
    assert second is not None
    assert second.ref == first.ref
    assert second.last_four == "4322"
    assert env.read(second) == "987654322"
    sealed = env.store.get(CASE, second.ref)
    assert sealed is not None
    assert sealed.created_at <= sealed.updated_at


def test_clearing_leaves_the_sealed_item_in_place() -> None:
    env = Env()
    stored = env.store_(TaxIdInput(kind="ssn", value=SSN))
    assert stored is not None
    assert env.store_(None, existing=stored) is None
    assert env.store.get(CASE, stored.ref) is not None


def test_the_item_round_trips_through_the_attribute_converter() -> None:
    env = Env()
    ref = env.store_(TaxIdInput(kind="ssn", value=SSN))
    assert ref is not None
    sealed = env.store.get(CASE, ref.ref)
    assert sealed is not None
    item = {"PK": "CASE#case-0001", **tax_id_item(sealed)}
    assert item["SK"] == f"TAXID#{ref.ref}"
    assert tax_id_from_item(from_attributes(to_attributes(item))) == sealed


def test_the_context_names_the_purpose_the_grant_conditions_on() -> None:
    context = encryption_context(firm_id=FIRM, ref="r1")
    assert context == {"purpose": TAX_ID_PURPOSE, "firm_id": FIRM, "tax_id_ref": "r1"}
    assert TAX_ID_PURPOSE == "debtor-tax-id"
    # Canonical: the same mapping in any order is the same associated data.
    assert context_aad(context) == context_aad(dict(reversed(list(context.items()))))


# ── The KMS cipher, with the transport faked at the client ──────


class FakeKms:
    """`generate_data_key` / `decrypt` as KMS answers them, with the wrap
    done under a local key so the ciphertexts are real and the context
    check is enforced the way KMS enforces it — a mismatched context refuses."""

    def __init__(self) -> None:
        self.inner = LocalTaxIdCipher()
        self.calls: list[tuple[str, dict[str, object]]] = []

    def generate_data_key(self, **kwargs: object) -> dict[str, bytes]:
        self.calls.append(("generate_data_key", kwargs))
        context = kwargs["EncryptionContext"]
        assert isinstance(context, dict)
        key = new_data_key()
        nonce = os.urandom(NONCE_BYTES)
        wrapped = nonce + AESGCM(self.inner.master_key).encrypt(
            nonce, key, context_aad(context)
        )
        return {"Plaintext": key, "CiphertextBlob": wrapped}

    def decrypt(self, **kwargs: object) -> dict[str, bytes]:
        self.calls.append(("decrypt", kwargs))
        context = kwargs["EncryptionContext"]
        blob = kwargs["CiphertextBlob"]
        assert isinstance(context, dict)
        assert isinstance(blob, bytes)
        key = AESGCM(self.inner.master_key).decrypt(
            blob[:NONCE_BYTES], blob[NONCE_BYTES:], context_aad(context)
        )
        return {"Plaintext": key}


def test_the_kms_cipher_seals_and_opens_through_the_shared_envelope() -> None:
    kms = FakeKms()
    cipher = KmsTaxIdCipher("alias/insolvia-staging-cases", client=kms)
    context = encryption_context(firm_id=FIRM, ref="r1")
    envelope = cipher.seal(SSN, context=context)
    assert cipher.open(envelope, context=context) == SSN
    generate, decrypt = kms.calls
    assert generate[1]["KeyId"] == "alias/insolvia-staging-cases"
    assert generate[1]["KeySpec"] == "AES_256"
    assert generate[1]["EncryptionContext"] == context
    assert decrypt[1]["KeyId"] == "alias/insolvia-staging-cases"
    assert decrypt[1]["EncryptionContext"] == context


def test_the_kms_cipher_refuses_another_context_at_the_unwrap() -> None:
    kms = FakeKms()
    cipher = KmsTaxIdCipher("alias/insolvia-staging-cases", client=kms)
    envelope = cipher.seal(SSN, context=encryption_context(firm_id=FIRM, ref="r1"))
    with pytest.raises(InvalidTag):
        cipher.open(envelope, context=encryption_context(firm_id=OTHER_FIRM, ref="r1"))


def test_the_key_alias_is_derived_from_the_case_table() -> None:
    assert case_key_alias("insolvia-staging-cases") == "alias/insolvia-staging-cases"
    assert (
        case_key_alias("insolvia-dev-0123456789ab-cases")
        == "alias/insolvia-dev-0123456789ab-cases"
    )
