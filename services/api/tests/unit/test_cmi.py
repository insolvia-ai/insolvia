"""Known-answer checks for the § 101(10A) CMI derivation (issue #100).

Every scenario is arithmetic a reviewer can redo on paper: a regular salary,
a biweekly pay cycle (13 paychecks in 6 months), a mid-window job change
with the gap months surfaced, and non-wage income with the § 101(10A)(B)(ii)
exclusions shown rather than dropped. The window derivation gets its own
cases, including the year wrap — an off-by-one month there silently moves a
paycheck in or out of CMI.
"""

from __future__ import annotations

from datetime import date

import pytest
from insolvia_api.core.cmi import (
    EXCLUSION_CITATION,
    cmi_window,
    current_monthly_income,
)
from insolvia_core.debtors import Debtor
from insolvia_core.income import (
    EmploymentBody,
    OtherIncomeRecordBody,
    PayPeriodRecordBody,
)


def debtor(role: str, debtor_id: str) -> Debtor:
    return Debtor(
        id=debtor_id,
        case_id="case-0001",
        filing_role=role,
        created_at="2026-08-01T12:00:00Z",
        updated_at="2026-08-01T12:00:00Z",
    )


DEBTOR_1 = debtor("debtor_1", "d-1")
DEBTOR_2 = debtor("debtor_2", "d-2")

EMPLOYMENT = EmploymentBody(
    debtor_id="d-1", status="employed", employer_name="Acme Staffing"
)


def paycheck(pay_date: str, gross: str, employment_id: str = "em-1"):
    return PayPeriodRecordBody(
        employment_id=employment_id,
        pay_date=pay_date,
        gross=gross,
        frequency="monthly",
    )


def compute(**overrides):
    arguments = {
        "filing_date": date(2026, 9, 15),
        "debtors": [DEBTOR_1],
        "employments": [("em-1", EMPLOYMENT)],
        "pay_periods": [],
        "other_income": [],
    }
    arguments.update(overrides)
    return current_monthly_income(**arguments)


# --- the window ---------------------------------------------------------------


@pytest.mark.parametrize(
    ("filing", "start", "end", "first_month", "last_month"),
    [
        # Filed mid-September: March through August, whole months.
        (date(2026, 9, 15), date(2026, 3, 1), date(2026, 8, 31), "2026-03", "2026-08"),
        # Filed on the 1st: the window still ends with the PRIOR month.
        (date(2026, 9, 1), date(2026, 3, 1), date(2026, 8, 31), "2026-03", "2026-08"),
        # A year wrap: filed in February, the window reaches into last year.
        (date(2026, 2, 10), date(2025, 8, 1), date(2026, 1, 31), "2025-08", "2026-01"),
        # January: the whole window is last year.
        (date(2026, 1, 2), date(2025, 7, 1), date(2025, 12, 31), "2025-07", "2025-12"),
    ],
)
def test_the_window_is_six_whole_calendar_months(
    filing: date, start: date, end: date, first_month: str, last_month: str
) -> None:
    window = cmi_window(filing)
    assert window.start == start
    assert window.end == end
    assert len(window.months) == 6
    assert window.months[0] == first_month
    assert window.months[-1] == last_month


# --- known-answer scenarios ---------------------------------------------------


def test_a_regular_monthly_salary_averages_to_itself() -> None:
    result = compute(
        pay_periods=[paycheck(f"2026-0{month}-25", "5200.00") for month in range(3, 9)]
    )
    [column] = result.columns
    assert column.column == "A"
    [wages] = column.lines
    assert wages.total_received == "31200.00"
    assert wages.monthly_average == "5200.00"
    assert len(wages.entries) == 6
    assert column.monthly_total == "5200.00"
    assert result.combined_monthly_total == "5200.00"
    assert result.annualized == "62400.00"
    assert result.gaps == ()
    assert result.problems == ()


def test_biweekly_pay_counts_thirteen_checks_by_pay_date() -> None:
    # Biweekly from 2026-03-06: 13 pay dates fall inside March-August. The
    # monthly average is NOT one paycheck x 26/12 — it is what was actually
    # received in the window, divided by six.
    pay_dates: list[str] = []
    current = date(2026, 3, 6)
    while current <= date(2026, 8, 31):
        pay_dates.append(current.isoformat())
        current = date.fromordinal(current.toordinal() + 14)
    assert len(pay_dates) == 13
    result = compute(pay_periods=[paycheck(day, "2000.00") for day in pay_dates])
    [column] = result.columns
    [wages] = column.lines
    assert wages.total_received == "26000.00"
    assert wages.monthly_average == "4333.33"  # 26000 / 6, rounded to cents
    assert result.combined_monthly_total == "4333.33"


def test_a_mid_window_job_change_surfaces_the_gap_months() -> None:
    old_job = EmploymentBody(
        debtor_id="d-1", status="not_employed", employer_name="Old Employer"
    )
    new_job = EmploymentBody(
        debtor_id="d-1",
        status="employed",
        employer_name="New Employer",
        employed_since="2026-06-15",
    )
    result = compute(
        employments=[("em-old", old_job), ("em-new", new_job)],
        pay_periods=[
            paycheck("2026-03-31", "4000.00", "em-old"),
            paycheck("2026-04-30", "4000.00", "em-old"),
            # May: between jobs — no income at all.
            paycheck("2026-06-30", "4500.00", "em-new"),
            paycheck("2026-07-31", "4500.00", "em-new"),
            paycheck("2026-08-31", "4500.00", "em-new"),
        ],
    )
    [column] = result.columns
    [wages] = column.lines
    assert wages.total_received == "21500.00"
    assert wages.monthly_average == "3583.33"
    # The old job's silent months surface; the new job's expected months
    # start at its hire month, so March-May are not false alarms.
    by_employer = {gap.employer: gap.months for gap in result.gaps}
    assert by_employer["Old Employer"] == (
        "2026-05",
        "2026-06",
        "2026-07",
        "2026-08",
    )
    assert "New Employer" not in by_employer


def test_non_wage_income_lands_on_its_lines_and_exclusions_are_shown() -> None:
    receipts = [
        OtherIncomeRecordBody(
            debtor_id="d-1",
            category="unemployment",
            received_on=f"2026-0{month}-07",
            amount="1200.00",
            payer="State Agency",
        )
        for month in range(3, 9)
    ] + [
        OtherIncomeRecordBody(
            debtor_id="d-1",
            category="social_security_act_benefit",
            received_on="2026-05-03",
            amount="1900.00",
        ),
    ]
    result = compute(employments=[], other_income=receipts)
    [column] = result.columns
    [unemployment] = column.lines
    assert unemployment.category == "unemployment"
    assert unemployment.monthly_average == "1200.00"
    [excluded] = column.excluded
    assert excluded.category == "social_security_act_benefit"
    assert excluded.total_received == "1900.00"
    assert excluded.citation == EXCLUSION_CITATION
    # The exclusion is visible AND uncounted.
    assert column.monthly_total == "1200.00"
    assert result.combined_monthly_total == "1200.00"


def test_business_receipts_net_their_expenses_with_a_zero_floor() -> None:
    result = compute(
        employments=[],
        other_income=[
            OtherIncomeRecordBody(
                debtor_id="d-1",
                category="business",
                received_on="2026-04-15",
                amount="3000.00",
                expenses="1200.00",
            ),
            OtherIncomeRecordBody(
                debtor_id="d-1",
                category="rental",
                received_on="2026-05-01",
                amount="1000.00",
                expenses="1600.00",
            ),
        ],
    )
    [column] = result.columns
    business, rental = column.lines
    assert business.total_received == "1800.00"
    assert "less ordinary and necessary operating expenses" in business.note
    # The rental lost money: the line floors at zero and says so.
    assert rental.total_received == "0.00"
    assert "enters as zero" in rental.note
    assert column.monthly_total == "300.00"  # 1800 / 6


def test_receipts_outside_the_window_do_not_count() -> None:
    result = compute(
        pay_periods=[
            paycheck("2026-02-28", "9999.00"),  # the month before the window
            paycheck("2026-09-01", "9999.00"),  # the filing month itself
            paycheck("2026-08-31", "5000.00"),  # the window's last day
            paycheck("2026-03-01", "5000.00"),  # the window's first day
        ]
    )
    [column] = result.columns
    [wages] = column.lines
    assert wages.total_received == "10000.00"


def test_a_spouse_takes_column_b_and_both_combine() -> None:
    spouse_job = EmploymentBody(
        debtor_id="d-2", status="employed", employer_name="Spouse Employer"
    )
    result = compute(
        debtors=[DEBTOR_1, DEBTOR_2],
        employments=[("em-1", EMPLOYMENT), ("em-2", spouse_job)],
        pay_periods=[
            *[paycheck(f"2026-0{m}-25", "3000.00") for m in range(3, 9)],
            *[paycheck(f"2026-0{m}-25", "1500.00", "em-2") for m in range(3, 9)],
        ],
    )
    assert [column.column for column in result.columns] == ["A", "B"]
    assert result.columns[0].monthly_total == "3000.00"
    assert result.columns[1].monthly_total == "1500.00"
    assert result.combined_monthly_total == "4500.00"
    assert result.annualized == "54000.00"


def test_a_non_filing_spouse_also_takes_column_b() -> None:
    spouse = debtor("non_filing_spouse", "d-3")
    result = compute(
        debtors=[DEBTOR_1, spouse],
        employments=[],
        other_income=[
            OtherIncomeRecordBody(
                debtor_id="d-3",
                category="pension_retirement",
                received_on="2026-04-01",
                amount="600.00",
            )
        ],
    )
    [column] = result.columns
    assert column.column == "B"
    assert column.monthly_total == "100.00"  # 600 / 6


def test_unplaceable_records_become_problems_not_zeros() -> None:
    result = compute(
        pay_periods=[
            paycheck("2026-04-10", "1000.00", "em-unknown"),
            PayPeriodRecordBody(employment_id="em-1", gross="1000.00"),
            PayPeriodRecordBody(employment_id="em-1", pay_date="2026-05-08"),
        ],
        other_income=[
            OtherIncomeRecordBody(category="unemployment", amount="100.00"),
            OtherIncomeRecordBody(
                category="unemployment", received_on="2026-04-01", amount="100.00"
            ),
        ],
    )
    assert len(result.problems) == 5
    assert any("no existing employment" in p for p in result.problems)
    assert any("no pay date" in p for p in result.problems)
    assert any("no gross amount" in p for p in result.problems)
    assert any("no received-on date" in p for p in result.problems)
    assert any("names no debtor" in p for p in result.problems)


def test_every_line_derivation_is_traceable_entry_by_entry() -> None:
    # "A CMI figure with its derivation visible line by line" — the issue's
    # done-when, in assertable form: the entries of every line sum to the
    # line's total, and the columns' totals sum to the combined figure.
    result = compute(
        pay_periods=[paycheck(f"2026-0{m}-25", "5200.00") for m in range(3, 9)],
        other_income=[
            OtherIncomeRecordBody(
                debtor_id="d-1",
                category="interest_dividends_royalties",
                received_on="2026-06-30",
                amount="90.00",
            )
        ],
    )
    from decimal import Decimal

    for column in result.columns:
        for line in column.lines:
            if line.note:
                continue  # business/rental totals are net of expenses
            assert sum(Decimal(e.amount) for e in line.entries) == Decimal(
                line.total_received
            )
    assert result.combined_monthly_total == "5215.00"  # 5200 + 90/6
