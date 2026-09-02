"""The B101 entity parsers' own rules (issue #93) — what the generic
collection suite (test_case_entities.py) cannot know: which enum values each
module admits, and the petition's nested hazardous-property block. Roundtrip,
provenance, storage, and route behaviour are the framework's and are covered
there, parametrised over the registry."""

from __future__ import annotations

import pytest
from insolvia_api.core.petitions import (
    parse_filing_professional,
    parse_petition,
    parse_prior_case,
    parse_sole_proprietorship,
)
from insolvia_core.errors import FieldValidationError


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
