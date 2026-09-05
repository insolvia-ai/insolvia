"""The dated income history's own parse rules (issue #100).

The generic machinery — provenance invariants, round-trips, empty bodies —
is test_case_entities.py's, parametrised over the registry. What lives here
is what is genuinely these parsers' own: the deduction rows' addressable
ids, the pay-frequency and category vocabularies, and the rule that only
business and rental receipts carry operating expenses.
"""

from __future__ import annotations

import pytest
from insolvia_core.errors import FieldValidationError
from insolvia_core.income import (
    EXCLUDED_INCOME_CATEGORIES,
    OTHER_INCOME_CATEGORIES,
    parse_other_income_record,
    parse_pay_period_record,
)


def test_a_full_pay_period_record_parses() -> None:
    body = parse_pay_period_record(
        {
            "employment_id": "em-1",
            "period_start": "2026-03-01",
            "period_end": "2026-03-14",
            "pay_date": "2026-03-20",
            "gross": "2600.00",
            "net": "2015.00",
            "deductions": [
                {"id": "d1", "category": "tax", "amount": "455.00"},
                {"id": "d2", "category": "other", "description": "Parking"},
            ],
            "frequency": "biweekly",
        }
    )
    assert body.pay_date == "2026-03-20"
    assert [d.category for d in body.deductions] == ["tax", "other"]


@pytest.mark.parametrize(
    ("payload", "path"),
    [
        ({"frequency": "fortnightly"}, "frequency"),
        ({"pay_date": "03/20/2026"}, "pay_date"),
        ({"gross": "2600.001"}, "gross"),
        ({"deductions": [{"category": "tax"}]}, "deductions[0].id"),
        (
            {"deductions": [{"id": "d1", "category": "yacht"}]},
            "deductions[0].category",
        ),
        (
            {
                "deductions": [
                    {"id": "d1", "category": "tax"},
                    {"id": "d1", "category": "insurance"},
                ]
            },
            "deductions[1].id",
        ),
    ],
)
def test_malformed_pay_period_fields_are_named(
    payload: dict[str, object], path: str
) -> None:
    with pytest.raises(FieldValidationError) as failure:
        parse_pay_period_record(payload)
    assert path in failure.value.fields


def test_every_income_category_parses_including_the_excluded_set() -> None:
    # § 101(10A)(B)(ii)'s excluded kinds are stored like any other receipt —
    # the CMI derivation shows the exclusion; storage does not enforce it.
    for category in OTHER_INCOME_CATEGORIES + EXCLUDED_INCOME_CATEGORIES:
        body = parse_other_income_record(
            {"category": category, "amount": "100.00", "received_on": "2026-04-01"}
        )
        assert body.category == category


def test_expenses_belong_only_to_business_and_rental_receipts() -> None:
    for category in ("business", "rental"):
        body = parse_other_income_record(
            {"category": category, "amount": "1800.00", "expenses": "600.00"}
        )
        assert body.expenses == "600.00"
    with pytest.raises(FieldValidationError) as failure:
        parse_other_income_record(
            {"category": "unemployment", "amount": "275.00", "expenses": "10.00"}
        )
    assert "expenses" in failure.value.fields
