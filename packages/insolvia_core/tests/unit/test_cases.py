"""The case root's court reference (issue #360): `{court, division}` is
validated against the registry, the printed `district` is derived from it,
and a row written before the registry still reads.

The route-level behaviour — who may open a case, what the API echoes — is
services/api's test_cases.py; this file pins the domain rule itself.
"""

from __future__ import annotations

import pytest
from insolvia_core.cases import (
    CaseChanges,
    apply_changes,
    case_from_item,
    case_item,
    case_json,
    create_case,
    parse_case_creation,
    parse_case_update,
)
from insolvia_core.errors import FieldValidationError

FIRM = "00000000-0000-4000-8000-00000000f18a"
ALICE = "00000000-0000-4000-8000-00000000a11c"


def test_a_case_names_its_court_and_the_district_is_derived():
    draft = parse_case_creation({"chapter": 7, "court": "flmb", "division": "tampa"})
    case, _ = create_case(draft, firm_id=FIRM, created_by=ALICE)
    assert (case.court, case.division) == ("flmb", "tampa")
    # The B101 dropdown's own spelling, never typed by a client.
    assert case.district == "Middle District of Florida"
    body = case_json(case)
    assert body["court"] == "flmb"
    assert body["division"] == "tampa"
    assert body["district"] == "Middle District of Florida"


@pytest.mark.parametrize(
    ("payload", "field"),
    [
        ({"chapter": 7}, "court"),
        ({"chapter": 7, "court": "flmb"}, "division"),
        ({"chapter": 7, "court": "nyeb", "division": "brooklyn"}, "court"),
        ({"chapter": 7, "court": "flmb", "division": "miami"}, "division"),
        (
            {"chapter": 7, "court": "flmb", "division": "tampa", "district": "x"},
            "district",
        ),
    ],
)
def test_an_unknown_or_half_reference_is_refused_by_name(payload, field):
    with pytest.raises(FieldValidationError) as caught:
        parse_case_creation(payload)
    assert field in caught.value.fields


def test_a_court_change_rewrites_all_three_fields_together():
    case, _ = create_case(
        parse_case_creation({"chapter": 7, "court": "flmb", "division": "tampa"}),
        firm_id=FIRM,
        created_by=ALICE,
    )
    moved = apply_changes(
        case, parse_case_update({"court": "txsb", "division": "houston"})
    )
    assert (moved.court, moved.division) == ("txsb", "houston")
    assert moved.district == "Southern District of Texas"
    # A chapter-only PATCH leaves the reference alone.
    same = apply_changes(moved, CaseChanges(chapter=13))
    assert (same.court, same.division, same.district) == (
        "txsb",
        "houston",
        "Southern District of Texas",
    )


def test_a_patch_carrying_one_half_is_refused():
    with pytest.raises(FieldValidationError) as caught:
        parse_case_update({"division": "orlando"})
    assert "court" in caught.value.fields


def test_the_item_round_trips_the_reference():
    case, _ = create_case(
        parse_case_creation({"chapter": 7, "court": "flnb", "division": "pensacola"}),
        firm_id=FIRM,
        created_by=ALICE,
    )
    item = case_item(case)
    assert item["court"] == "flnb"
    assert item["division"] == "pensacola"
    assert case_from_item(item) == case


def test_a_row_written_before_the_registry_still_reads():
    """THE MIGRATION. A legacy row holds the typed district and no reference;
    it reads, prints its old text, and says so by carrying no `court`."""
    legacy = {
        "PK": "CASE#legacy",
        "SK": "META",
        "id": "legacy",
        "firmId": FIRM,
        "createdBy": ALICE,
        "chapter": 7,
        "district": "NDCA",
        "status": "intake",
        "createdAt": "2026-01-01T00:00:00.000000Z",
        "updatedAt": "2026-01-01T00:00:00.000000Z",
    }
    case = case_from_item(legacy)
    assert case.district == "NDCA"
    assert case.court is None
    assert case.division is None
    body = case_json(case)
    assert body["district"] == "NDCA"
    assert "court" not in body
    assert "division" not in body
    # Writing it back stores exactly what was read — no invented reference.
    assert "court" not in case_item(case)
