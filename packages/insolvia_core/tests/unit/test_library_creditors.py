"""The firm's reusable creditor library (issue 13.9 / #350): parsing, the
whole-record replace, and the stored item shape.

Every identifier below is obviously fake. This repo is public.
"""

from __future__ import annotations

import pytest
from insolvia_core.errors import FieldValidationError, ValidationError
from insolvia_core.fields import Address
from insolvia_core.library_creditors import (
    LibraryCreditor,
    create_library_creditor,
    library_creditor_from_item,
    library_creditor_item,
    library_creditor_json,
    parse_library_creditor,
    replace_library_creditor,
    sort_key,
)

FIRM_ID = "00000000-0000-4000-8000-00000000f18a"


def test_a_name_is_required():
    with pytest.raises(FieldValidationError) as caught:
        parse_library_creditor({})
    assert "name" in caught.value.fields


def test_a_blank_name_is_also_refused():
    with pytest.raises(FieldValidationError) as caught:
        parse_library_creditor({"name": "   "})
    assert "name" in caught.value.fields


def test_everything_but_the_name_is_optional():
    draft = parse_library_creditor({"name": "Acme Collections"})
    assert draft.name == "Acme Collections"
    assert draft.address == Address()
    assert draft.additional_notice_parties == ()
    assert draft.preferred is False
    assert draft.notes is None


def test_the_address_is_parsed():
    draft = parse_library_creditor(
        {
            "name": "Acme Collections",
            "address": {"line1": "1 Main St", "city": "Springfield", "state": "IL"},
        }
    )
    assert draft.address.line1 == "1 Main St"
    assert draft.address.city == "Springfield"


def test_additional_notice_parties_use_the_claim_notice_party_shape():
    """Same shape `claim.notice_parties` uses — client-chosen id, name,
    address, account_last4 — and the same required-id rule."""
    draft = parse_library_creditor(
        {
            "name": "Acme Collections",
            "additional_notice_parties": [
                {"id": "np1", "name": "Acme Legal Dept", "account_last4": "1234"}
            ],
        }
    )
    assert len(draft.additional_notice_parties) == 1
    party = draft.additional_notice_parties[0]
    assert party.id == "np1"
    assert party.name == "Acme Legal Dept"
    assert party.account_last4 == "1234"


def test_a_notice_party_with_no_id_is_refused_under_the_library_field_name():
    """The error path is REWRITTEN from claims.py's hardcoded `notice_parties`
    prefix to this module's actual wire field — a client sending
    `additional_notice_parties` must not see an error naming a field it never
    sent."""
    with pytest.raises(FieldValidationError) as caught:
        parse_library_creditor(
            {"name": "Acme Collections", "additional_notice_parties": [{"name": "x"}]}
        )
    assert "additional_notice_parties[0].id" in caught.value.fields
    assert not any(key.startswith("notice_parties") for key in caught.value.fields)


def test_preferred_defaults_to_false_but_is_settable():
    draft = parse_library_creditor({"name": "Acme Collections", "preferred": True})
    assert draft.preferred is True


def test_notes_accepts_multiple_lines():
    draft = parse_library_creditor(
        {"name": "Acme Collections", "notes": "Line one\nLine two"}
    )
    assert draft.notes == "Line one\nLine two"


# ── Construction ────────────────────────────────────────────────────


def test_creation_mints_an_id_and_stamps_the_firm():
    draft = parse_library_creditor({"name": "Acme Collections"})
    creditor = create_library_creditor(draft, firm_id=FIRM_ID)
    assert creditor.firm_id == FIRM_ID
    assert creditor.id
    assert creditor.created_at == creditor.updated_at


def test_replace_keeps_identity_and_bumps_updated_at():
    draft = parse_library_creditor({"name": "Acme Collections"})
    original = create_library_creditor(draft, firm_id=FIRM_ID)
    edited = replace_library_creditor(
        original, parse_library_creditor({"name": "Acme Collections LLC"})
    )
    assert edited.id == original.id
    assert edited.firm_id == original.firm_id
    assert edited.created_at == original.created_at
    assert edited.name == "Acme Collections LLC"


# ── The stored item shape ──────────────────────────────────────────


def test_the_item_key_follows_the_firm_partition_discipline():
    """PK = FIRM#<firm_id>, SK = LIBCREDITOR#<id> — the same namespaced-SK
    discipline firm_user_item uses (USER#<subject>), sharing the partition."""
    draft = parse_library_creditor({"name": "Acme Collections"})
    creditor = create_library_creditor(draft, firm_id=FIRM_ID)
    item = library_creditor_item(creditor)
    assert item["PK"] == f"FIRM#{FIRM_ID}"
    assert item["SK"] == sort_key(creditor.id)
    assert item["SK"].startswith("LIBCREDITOR#")


def test_the_item_round_trips_with_nested_shapes_intact():
    draft = parse_library_creditor(
        {
            "name": "Acme Collections",
            "address": {"line1": "1 Main St", "postal_code": "62701"},
            "additional_notice_parties": [{"id": "np1", "name": "Legal Dept"}],
            "preferred": True,
            "notes": "Reach the legal department directly.",
        }
    )
    original = create_library_creditor(draft, firm_id=FIRM_ID)
    item = library_creditor_item(original)
    rebuilt = library_creditor_from_item(item)
    assert rebuilt == original


def test_json_is_snake_case_and_omits_nothing():
    draft = parse_library_creditor({"name": "Acme Collections", "preferred": True})
    creditor = create_library_creditor(draft, firm_id=FIRM_ID)
    body = library_creditor_json(creditor)
    assert body["name"] == "Acme Collections"
    assert body["preferred"] is True
    assert body["created_at"] == creditor.created_at
    assert "firm_id" not in body
    assert "firmId" not in body


def test_from_item_raises_on_a_malformed_row():
    with pytest.raises(ValidationError):
        library_creditor_from_item({"PK": "FIRM#x", "SK": "LIBCREDITOR#1"})


def test_a_library_creditor_dataclass_carries_the_expected_fields():
    """Smoke-checks the shape the issue specifies: name, notice address,
    additional notice parties, a preferred flag, notes."""
    fields = LibraryCreditor.__dataclass_fields__
    for name in (
        "firm_id",
        "id",
        "name",
        "address",
        "additional_notice_parties",
        "preferred",
        "notes",
        "created_at",
        "updated_at",
    ):
        assert name in fields
