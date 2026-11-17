"""The B101 entity parsers' own rules (issue #93) — what the generic
collection suite (test_case_entities.py) cannot know: which enum values each
module admits, and the petition's nested hazardous-property block. Roundtrip,
provenance, storage, and route behaviour are the framework's and are covered
there, parametrised over the registry."""

from __future__ import annotations

import pytest
from insolvia_core.errors import FieldValidationError
from insolvia_core.petitions import (
    parse_filing_professional,
    parse_petition,
    parse_prior_case,
    parse_sole_proprietorship,
)


def test_a_full_petition_parses() -> None:
    body = parse_petition(
        {
            "fee_handling": "waiver",
            "small_business_status": "chapter_11_subchapter_v",
            "hazardous_property": {
                "description": "Chemical drums",
                "why_immediate": "Corroding",
                "address": {"line1": "9 Dock Rd", "state": "FL"},
            },
            "debt_character": "other",
            "debt_character_other": "Mixed personal guarantees",
            "estimated_creditors": "50_99",
        }
    )
    assert body.fee_handling == "waiver"
    assert body.hazardous_property.address.state == "FL"
    assert body.debt_character_other == "Mixed personal guarantees"


@pytest.mark.parametrize(
    ("payload", "bad_field"),
    [
        pytest.param({"fee_handling": "monthly"}, "fee_handling", id="fee-handling"),
        pytest.param(
            {"small_business_status": "yes"},
            "small_business_status",
            id="small-business-status",
        ),
        pytest.param(
            {"debt_character": "personal"}, "debt_character", id="debt-character"
        ),
        pytest.param(
            {"estimated_creditors": "1-49"},
            "estimated_creditors",
            id="band-must-be-the-enum-not-the-printed-label",
        ),
        pytest.param(
            {"estimated_assets": "plenty"}, "estimated_assets", id="asset-band"
        ),
        pytest.param(
            {"hazardous_property": "propane"},
            "hazardous_property",
            id="hazardous-property-not-an-object",
        ),
    ],
)
def test_petition_enums_are_closed(payload: dict[str, object], bad_field: str) -> None:
    with pytest.raises(FieldValidationError) as excinfo:
        parse_petition(payload)
    assert bad_field in excinfo.value.fields


def test_expected_filing_date_is_a_form_date() -> None:
    body = parse_petition({"expected_filing_date": "2026-06-15"})
    assert body.expected_filing_date == "2026-06-15"
    with pytest.raises(FieldValidationError) as excinfo:
        parse_petition({"expected_filing_date": "06/15/2026"})
    assert "expected_filing_date" in excinfo.value.fields


def test_prior_case_dates_are_form_dates() -> None:
    with pytest.raises(FieldValidationError) as excinfo:
        parse_prior_case({"filed_on": "03/04/2019"})
    assert "filed_on" in excinfo.value.fields


def test_sole_proprietorship_business_type_is_closed() -> None:
    with pytest.raises(FieldValidationError) as excinfo:
        parse_sole_proprietorship({"business_type": "restaurant"})
    assert "business_type" in excinfo.value.fields


def test_filing_professional_role_and_bar_state() -> None:
    body = parse_filing_professional(
        {"role": "bankruptcy_petition_preparer", "bar_state": "FL"}
    )
    assert body.role == "bankruptcy_petition_preparer"
    with pytest.raises(FieldValidationError) as excinfo:
        parse_filing_professional({"role": "notary", "bar_state": "Florida"})
    assert set(excinfo.value.fields) == {"role", "bar_state"}


def test_filing_professional_compensation_fields_parse() -> None:
    """B2030's facts (issue #351): two money amounts, two closed sources
    with their `other` names, the sharing answer, and two narratives."""
    body = parse_filing_professional(
        {
            "role": "attorney",
            "compensation_agreed": "1500",
            "compensation_received": "1000.5",
            "compensation_source_paid": "debtor",
            "compensation_source_to_be_paid": "other",
            "compensation_source_to_be_paid_other": "A relative",
            "compensation_shared": False,
            "services_other": "Reaffirmation negotiations.",
            "services_excluded": "Adversary proceedings.",
        }
    )
    assert body.compensation_agreed == "1500.00"
    assert body.compensation_received == "1000.50"
    assert body.compensation_source_paid == "debtor"
    assert body.compensation_source_to_be_paid == "other"
    assert body.compensation_source_to_be_paid_other == "A relative"
    assert body.compensation_shared is False
    assert body.services_excluded == "Adversary proceedings."
    # Absent everywhere by default — the disclosure is progressive too.
    assert parse_filing_professional({}).compensation_agreed is None


@pytest.mark.parametrize(
    ("payload", "bad_field"),
    [
        ({"compensation_agreed": 1500}, "compensation_agreed"),
        ({"compensation_received": "-1"}, "compensation_received"),
        ({"compensation_source_paid": "trustee"}, "compensation_source_paid"),
        (
            {"compensation_source_to_be_paid": "Debtor"},
            "compensation_source_to_be_paid",
        ),
        ({"compensation_shared": "no"}, "compensation_shared"),
    ],
)
def test_filing_professional_compensation_fields_are_validated(
    payload: dict[str, object], bad_field: str
) -> None:
    with pytest.raises(FieldValidationError) as excinfo:
        parse_filing_professional(payload)
    assert set(excinfo.value.fields) == {bad_field}
