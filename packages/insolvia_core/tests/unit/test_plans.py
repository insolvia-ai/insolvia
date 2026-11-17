"""The Chapter 13 plan record's parser (issue 16.2 / #366): the proposal's
shape — the term under § 1322(d), the trustee's fee under § 586(e), the
per-claim treatments, the step payments and lump sums — validated for shape,
and absent everywhere by default."""

from __future__ import annotations

import pytest
from insolvia_core.errors import FieldValidationError
from insolvia_core.fields import percentage
from insolvia_core.plans import (
    PAYMENT_SOURCES,
    SECURED_TREATMENTS,
    UNSECURED_TREATMENTS,
    parse_plan,
)


def errors_of(payload: dict[str, object]) -> dict[str, str]:
    with pytest.raises(FieldValidationError) as caught:
        parse_plan(payload)
    return dict(caught.value.fields)


def test_an_empty_payload_is_an_empty_plan() -> None:
    body = parse_plan({})
    assert body.term_months is None
    assert body.payment_source is None
    assert body.secured_treatments == ()
    assert body.step_payments == ()
    assert body.lump_sums == ()


@pytest.mark.parametrize("term", [1, 36, 60])
def test_a_term_inside_five_years_is_accepted(term: int) -> None:
    assert parse_plan({"term_months": term}).term_months == term


@pytest.mark.parametrize("term", [0, 61, 84])
def test_a_term_past_five_years_is_refused(term: int) -> None:
    assert "term_months" in errors_of({"term_months": term})


def test_the_trustee_percentage_is_capped_at_ten() -> None:
    assert parse_plan({"trustee_percentage": "10"}).trustee_percentage == "10.00"
    assert "trustee_percentage" in errors_of({"trustee_percentage": "10.5"})


@pytest.mark.parametrize(
    ("given", "stored"),
    [("8.5", "8.50"), ("8.500", "8.50"), ("6.375", "6.375"), ("0", "0.00")],
)
def test_a_percentage_is_stored_canonically(given: str, stored: str) -> None:
    errors: dict[str, str] = {}
    assert percentage(given, "rate", errors) == stored
    assert errors == {}


@pytest.mark.parametrize("given", [8.5, "-1", "100.01", "6.3755", "eight", "NaN"])
def test_a_malformed_percentage_is_refused(given: object) -> None:
    errors: dict[str, str] = {}
    assert percentage(given, "rate", errors) is None
    assert "rate" in errors


@pytest.mark.parametrize("source", PAYMENT_SOURCES)
def test_every_payment_source_is_accepted(source: str) -> None:
    assert parse_plan({"payment_source": source}).payment_source == source


@pytest.mark.parametrize("treatment", UNSECURED_TREATMENTS)
def test_every_unsecured_treatment_is_accepted(treatment: str) -> None:
    body = parse_plan({"unsecured_treatment": treatment})
    assert body.unsecured_treatment == treatment


@pytest.mark.parametrize("treatment", SECURED_TREATMENTS)
def test_every_secured_treatment_parses_whole(treatment: str) -> None:
    body = parse_plan(
        {
            "secured_treatments": [
                {
                    "id": "t1",
                    "claim_id": "claim-1",
                    "treatment": treatment,
                    "arrearage": "1200",
                    "interest_rate": "7.25",
                }
            ]
        }
    )
    (row,) = body.secured_treatments
    assert row.treatment == treatment
    assert row.arrearage == "1200.00"
    assert row.interest_rate == "7.25"


def test_an_unknown_secured_treatment_is_refused() -> None:
    errors = errors_of({"secured_treatments": [{"id": "t1", "treatment": "strip_off"}]})
    assert "secured_treatments[0].treatment" in errors


def test_one_claim_cannot_carry_two_treatments() -> None:
    errors = errors_of(
        {
            "secured_treatments": [
                {"id": "t1", "claim_id": "c", "treatment": "surrender"},
                {"id": "t2", "claim_id": "c", "treatment": "cramdown"},
            ]
        }
    )
    assert "secured_treatments[1].claim_id" in errors


def test_a_row_without_an_id_is_refused() -> None:
    errors = errors_of({"lump_sums": [{"month": 3, "amount": "100"}]})
    assert "lump_sums[0].id" in errors


def test_two_steps_cannot_start_in_one_month() -> None:
    errors = errors_of(
        {
            "step_payments": [
                {"id": "s1", "start_month": 13, "monthly_payment": "500"},
                {"id": "s2", "start_month": 13, "monthly_payment": "600"},
            ]
        }
    )
    assert "step_payments[1].start_month" in errors


@pytest.mark.parametrize("month", [0, 61])
def test_a_lump_sum_must_fall_inside_the_plan(month: int) -> None:
    errors = errors_of({"lump_sums": [{"id": "l1", "month": month}]})
    assert "lump_sums[0].month" in errors
