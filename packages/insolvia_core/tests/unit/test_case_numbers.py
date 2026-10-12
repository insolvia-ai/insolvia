"""The court case number read into its parts, and the key a notice is
matched on (ADR 0024 PR 8, for #369)."""

from __future__ import annotations

from dataclasses import replace

import pytest
from insolvia_core.case_numbers import (
    CaseNumber,
    case_number_key,
    parse_case_number,
    petition_date,
)
from insolvia_core.cases import Case, case_from_item, case_item, case_json

KEY = "flmb:6:26-bk-10000"


@pytest.mark.parametrize(
    "printed",
    [
        "6:26-bk-10000",
        "6:26-BK-10000",
        "  6:26-bk-10000  ",
        "6:26-bk-10000-LVV",
        "6:26-bk-10000 - lvv",
        "6:26-bk-10000 (LVV)",
        "06:26-bk-10000",
        "6:2026-bk-10000",
        "6:26-bk-010000",
        "6 : 26-bk-10000",
    ],
)
def test_every_printing_of_one_case_has_one_key(printed):
    assert case_number_key("flmb", printed) == KEY


def test_the_parts():
    assert parse_case_number("8:26-bk-01234-RCT") == CaseNumber(
        office="8", year="26", type="bk", sequence="01234", judge="RCT"
    )


def test_a_short_sequence_is_padded_and_the_court_is_lower_case():
    assert case_number_key("FLMB", "6:26-bk-42") == "flmb:6:26-bk-00042"


def test_the_judge_is_not_part_of_the_case():
    # A reassignment changes the initials; the case is the same one.
    assert case_number_key("flmb", "6:26-bk-10000-ABC") == case_number_key(
        "flmb", "6:26-bk-10000-XYZ"
    )


def test_the_court_is_part_of_the_key():
    assert case_number_key("flmb", "6:26-bk-10000") != case_number_key(
        "txsb", "6:26-bk-10000"
    )


@pytest.mark.parametrize(
    ("one", "other"),
    [
        ("6:26-bk-10000", "6:26-bk-10001"),
        ("6:26-bk-10000", "6:25-bk-10000"),
        ("6:26-bk-10000", "3:26-bk-10000"),
        ("6:26-bk-10000", "6:26-ap-10000"),
    ],
)
def test_different_cases_have_different_keys(one, other):
    assert case_number_key("flmb", one) != case_number_key("flmb", other)


def test_a_number_without_its_office_parses_but_has_no_key():
    parsed = parse_case_number("26-10000-mg")
    assert parsed == CaseNumber(
        office=None, year="26", type="bk", sequence="10000", judge="MG"
    )
    assert parse_case_number("26-10000-mg", require_office=True) is None
    assert case_number_key("nysb", "26-10000-mg") is None
    assert parsed is not None
    with pytest.raises(ValueError, match="needs the office"):
        parsed.key("nysb")


def test_the_matcher_treats_a_missing_office_as_any_office():
    stored = parse_case_number("1:26-bk-10000")
    assert stored is not None
    for printed in ("26-10000", "26-bk-10000-mg", "1:26-bk-10000"):
        notice = parse_case_number(printed)
        assert notice is not None
        assert notice.matches(stored)
    other_office = parse_case_number("2:26-bk-10000")
    assert other_office is not None
    assert not other_office.matches(stored)


@pytest.mark.parametrize(
    "text",
    ["", "pending", "10000", "6:26-bk-", "6:26-bk-1000a", "Case No. 6:26-bk-10000"],
)
def test_text_that_is_not_a_case_number_has_no_key(text):
    assert parse_case_number(text) is None
    assert case_number_key("flmb", text) is None


def test_no_court_or_no_number_is_no_key():
    assert case_number_key(None, "6:26-bk-10000") is None
    assert case_number_key("flmb", None) is None


def test_the_canonical_form_drops_the_judge():
    parsed = parse_case_number("6:2026-BK-10000-lvv")
    assert parsed is not None
    assert parsed.canonical == "6:26-bk-10000"


@pytest.mark.parametrize(
    ("court_said", "date"),
    [
        ("2099-01-15", "2099-01-15"),
        ("2099-01-15T12:01:00Z", "2099-01-15"),
        # The court's local date, as printed — never converted to UTC.
        ("2099-01-15T23:10:00-05:00", "2099-01-15"),
        ("2099-01-15 23:10:00", "2099-01-15"),
        ("January 15, 2099", None),
        ("", None),
    ],
)
def test_the_petition_date_is_the_date_the_court_printed(court_said, date):
    assert petition_date(court_said) == date


def _case(**changes: object) -> Case:
    base = Case(
        id="00000000-0000-4000-8000-0000000000c1",
        firm_id="firm",
        created_by="someone",
        chapter=7,
        district="Middle District of Florida",
        status="filed",
        created_at="2099-01-01T00:00:00Z",
        updated_at="2099-01-01T00:00:00Z",
        court="flmb",
        division="3",
        filed_at="2099-01-15",
        case_number="6:26-bk-10000-LVV",
    )
    return replace(base, **changes)  # type: ignore[arg-type]


def test_the_stored_case_carries_the_key_and_the_wire_does_not():
    item = case_item(_case())
    assert item["caseNumberKey"] == KEY
    assert item["caseNumber"] == "6:26-bk-10000-LVV"
    assert "caseNumberKey" not in case_json(_case())
    assert case_from_item(item) == _case()


def test_a_number_it_cannot_read_stores_no_key():
    assert "caseNumberKey" not in case_item(_case(case_number="BK 26/10000"))
    assert "caseNumberKey" not in case_item(_case(court=None))
