"""The means-test input record's parser (issues #101, #349): the inputs
the screen added — the marital status, the three household sizes, the
exemptions, the per-line income overrides and the per-claim secured
payment rows — validated for shape, and absent everywhere by default."""

from __future__ import annotations

import pytest
from insolvia_core.errors import FieldValidationError
from insolvia_core.means_test_inputs import (
    INCOME_LINE_CATEGORIES,
    MARITAL_FILING_STATUSES,
    SECURED_PAYMENT_BUCKETS,
    parse_means_test_input,
)


def errors_of(payload: dict[str, object]) -> dict[str, str]:
    with pytest.raises(FieldValidationError) as caught:
        parse_means_test_input(payload)
    return dict(caught.value.fields)


def test_an_empty_payload_is_an_empty_record() -> None:
    body = parse_means_test_input({})
    assert body.marital_filing_status is None
    assert body.median_household_size is None
    assert body.irs_family_size is None
    assert body.irs_housing_family_size is None
    assert body.non_consumer_debts is None
    assert body.disabled_veteran is None
    assert body.reservist_national_guard is None
    assert body.income_overrides == ()
    assert body.other_secured_payments == ()


@pytest.mark.parametrize("status", MARITAL_FILING_STATUSES)
def test_every_marital_status_is_accepted(status: str) -> None:
    assert parse_means_test_input({"marital_filing_status": status}) is not None


def test_an_unknown_marital_status_is_rejected() -> None:
    assert "marital_filing_status" in errors_of({"marital_filing_status": "widowed"})


@pytest.mark.parametrize(
    "field",
    ["median_household_size", "irs_family_size", "irs_housing_family_size"],
)
def test_a_household_size_override_must_have_at_least_one_person(field: str) -> None:
    assert parse_means_test_input({field: 3}) is not None
    assert field in errors_of({field: 0})
    assert field in errors_of({field: "3"})


@pytest.mark.parametrize(
    "flag", ["non_consumer_debts", "disabled_veteran", "reservist_national_guard"]
)
def test_an_exemption_is_a_yes_no_answer(flag: str) -> None:
    assert getattr(parse_means_test_input({flag: True}), flag) is True
    assert getattr(parse_means_test_input({flag: False}), flag) is False
    assert flag in errors_of({flag: "yes"})


# ── income overrides ─────────────────────────────────────────────


def test_an_income_override_row_parses_whole() -> None:
    body = parse_means_test_input(
        {
            "income_overrides": [
                {
                    "id": "ov-1",
                    "column": "A",
                    "category": "wages",
                    "monthly_amount": "5000",
                }
            ]
        }
    )
    [row] = body.income_overrides
    assert row.id == "ov-1"
    assert row.column == "A"
    assert row.category == "wages"
    assert row.monthly_amount == "5000.00"


@pytest.mark.parametrize("category", INCOME_LINE_CATEGORIES)
def test_every_income_line_including_the_exclusions_can_be_overridden(
    category: str,
) -> None:
    body = parse_means_test_input(
        {"income_overrides": [{"id": "r", "column": "B", "category": category}]}
    )
    assert body.income_overrides[0].category == category


def test_an_income_override_needs_an_addressable_id() -> None:
    errors = errors_of({"income_overrides": [{"column": "A", "category": "wages"}]})
    assert "income_overrides[0].id" in errors


def test_an_income_override_column_is_a_or_b() -> None:
    errors = errors_of({"income_overrides": [{"id": "r", "column": "C"}]})
    assert "income_overrides[0].column" in errors


def test_an_income_override_category_must_be_a_form_line() -> None:
    errors = errors_of({"income_overrides": [{"id": "r", "category": "lottery"}]})
    assert "income_overrides[0].category" in errors


def test_two_overrides_for_one_columns_line_are_refused() -> None:
    errors = errors_of(
        {
            "income_overrides": [
                {"id": "a", "column": "A", "category": "wages"},
                {"id": "b", "column": "A", "category": "wages"},
            ]
        }
    )
    assert "income_overrides[1].category" in errors


def test_the_same_line_may_be_overridden_in_each_column() -> None:
    body = parse_means_test_input(
        {
            "income_overrides": [
                {"id": "a", "column": "A", "category": "wages"},
                {"id": "b", "column": "B", "category": "wages"},
            ]
        }
    )
    assert len(body.income_overrides) == 2


# ── per-claim secured payments ───────────────────────────────────


def test_a_secured_payment_row_carries_its_claim_bucket_and_cure() -> None:
    body = parse_means_test_input(
        {
            "other_secured_payments": [
                {
                    "id": "sp-1",
                    "claim_id": "claim-0001",
                    "bucket": "vehicle_1",
                    "monthly_payment": "415",
                    "cure_total": "1200.50",
                }
            ]
        }
    )
    [row] = body.other_secured_payments
    assert row.claim_id == "claim-0001"
    assert row.bucket == "vehicle_1"
    assert row.monthly_payment == "415.00"
    assert row.cure_total == "1200.50"


@pytest.mark.parametrize("bucket", SECURED_PAYMENT_BUCKETS)
def test_every_ownership_bucket_is_accepted(bucket: str) -> None:
    body = parse_means_test_input(
        {"other_secured_payments": [{"id": "r", "bucket": bucket}]}
    )
    assert body.other_secured_payments[0].bucket == bucket


def test_an_unknown_bucket_is_rejected() -> None:
    errors = errors_of({"other_secured_payments": [{"id": "r", "bucket": "boat"}]})
    assert "other_secured_payments[0].bucket" in errors


def test_a_row_without_the_new_members_still_parses() -> None:
    # The rows entered before issue #349 carried only the three printed
    # columns; they must keep loading unchanged.
    body = parse_means_test_input(
        {
            "other_secured_payments": [
                {
                    "id": "os-1",
                    "creditor_name": "Example Finance",
                    "property_description": "Boat",
                    "monthly_payment": "120.00",
                }
            ]
        }
    )
    [row] = body.other_secured_payments
    assert row.claim_id is None
    assert row.bucket is None
    assert row.cure_total is None
