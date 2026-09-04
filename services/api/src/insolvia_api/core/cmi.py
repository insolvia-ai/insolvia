"""Current Monthly Income under § 101(10A) — the figure the means test keys
off (issue #100).

CMI is the average monthly income the debtor (and, in column B, a spouse)
received during the six full calendar months before the case is filed:
§ 101(10A)(A)'s window runs to "the last day of the calendar month
immediately preceding the date of the commencement of the case". The
computation here is DETERMINISTIC over the case's dated income history —
`pay_period_record` and `other_income_record` (insolvia_core.income) — and
Claude never touches the numbers (the register's LOGIC rule).

What lands where, and why:

- **Receipt dates control.** A paycheck counts by `pay_date` and a non-wage
  receipt by `received_on` — § 101(10A) counts income *received*, which is
  also why the data model stores those dates at all (case-data-model.md,
  "Income: 106I is not the income model").
- **Wages count gross.** CMI is income "without regard to whether such
  income is taxable income"; payroll deductions are 122A-2's business, not
  this module's.
- **Business and rental receipts net their expenses at the line, floored at
  zero** — B122A-1 lines 5-6's own arithmetic ("Do not enter a number less
  than zero"). The floor is applied to the line's six-month net, and the
  line says so when it fired.
- **Exclusions are shown, not dropped.** § 101(10A)(B)(ii)'s excluded kinds
  (Social Security Act benefits, the HAVEN Act's veterans' compensation,
  war-crime and terrorism victim payments) are recorded like any receipt
  and reported on the result as excluded lines with the citation — a
  reviewer comparing against a bank statement must see the money and the
  reason it does not count.
- **Gaps are surfaced, not silently zeroed.** A window month with no
  paycheck from an employment that should have produced one is listed on
  the result; whether it means missing records or a real gap in earnings
  is the preparer's call, and hiding it would make an understated CMI look
  complete.
- **Columns follow B122A-1.** Column A is debtor 1; column B is debtor 2
  — or a non-filing spouse, whose income the form includes when the
  debtor is married and not separated. Whether column B applies at all
  (the line 2 marital question) is the form projection's gate; this module
  computes every column the records populate.

Everything is pure over its inputs — no store, no clock — which is what
makes the known-answer tests possible. The derivation is line by line:
every line carries the entries that fed it, so each figure in the output
traces to dated records.
"""

from __future__ import annotations

from collections.abc import Sequence
from dataclasses import dataclass
from datetime import date, timedelta
from decimal import ROUND_HALF_UP, Decimal
from typing import Final

from insolvia_core.debtors import Debtor
from insolvia_core.income import (
    EXCLUDED_INCOME_CATEGORIES,
    EmploymentBody,
    OtherIncomeRecordBody,
    PayPeriodRecordBody,
)

# The § 101(10A)(B)(ii) citation every excluded line carries.
EXCLUSION_CITATION: Final = "11 U.S.C. § 101(10A)(B)(ii)"

# How each other-income category prints, in B122A-1's own line order.
_INCOME_LABELS: Final = {
    "wages": "Gross wages, salary, tips, bonuses, overtime, commissions",
    "alimony_maintenance": "Alimony and maintenance payments",
    "household_contributions": ("Amounts contributed by others to household expenses"),
    "business": "Net income from operating a business, profession, or farm",
    "rental": "Net income from rental and other real property",
    "interest_dividends_royalties": "Interest, dividends, and royalties",
    "unemployment": "Unemployment compensation",
    "pension_retirement": "Pension or retirement income",
    "other": "Income from all other sources",
    "social_security_act_benefit": "Benefits received under the Social Security Act",
    "veterans_disability_compensation": (
        "Veterans' disability or combat-related compensation (HAVEN Act)"
    ),
    "war_crime_victim_payment": "Payments to victims of war crimes",
    "terrorism_victim_payment": "Payments to victims of terrorism",
}

_LINE_ORDER: Final = (
    "wages",
    "alimony_maintenance",
    "household_contributions",
    "business",
    "rental",
    "interest_dividends_royalties",
    "unemployment",
    "pension_retirement",
    "other",
)

_CENT: Final = Decimal("0.01")


def _money(value: Decimal) -> str:
    return f"{value.quantize(_CENT):f}"


def _monthly_average(total: Decimal) -> str:
    return f"{(total / 6).quantize(_CENT, rounding=ROUND_HALF_UP):f}"


@dataclass(frozen=True)
class CmiWindow:
    """The six-month lookback: the first day of the earliest month through
    the last day of the calendar month preceding the filing month."""

    filing_date: date
    start: date
    end: date
    months: tuple[str, ...]  # "YYYY-MM", earliest first

    def contains(self, when: date) -> bool:
        return self.start <= when <= self.end


def cmi_window(filing_date: date) -> CmiWindow:
    """The § 101(10A)(A)(i) window for an (anticipated) filing date."""
    first_of_filing_month = filing_date.replace(day=1)
    end = first_of_filing_month - timedelta(days=1)
    months: list[str] = []
    year, month = end.year, end.month
    for _ in range(6):
        months.append(f"{year:04d}-{month:02d}")
        month -= 1
        if month == 0:
            year, month = year - 1, 12
    start = date(year=int(months[-1][:4]), month=int(months[-1][5:]), day=1)
    return CmiWindow(
        filing_date=filing_date,
        start=start,
        end=end,
        months=tuple(reversed(months)),
    )


@dataclass(frozen=True)
class CmiEntry:
    """One dated receipt that fed a line — the derivation's atoms."""

    received_on: str  # YYYY-MM-DD
    amount: str
    description: str


@dataclass(frozen=True)
class CmiLine:
    """One income line of one column, with its six-month total, the monthly
    average, and every entry behind them. `note` explains any line-level
    arithmetic (the business/rental expense subtraction and its zero
    floor)."""

    category: str
    label: str
    total_received: str
    monthly_average: str
    entries: tuple[CmiEntry, ...]
    citation: str = ""
    note: str = ""


@dataclass(frozen=True)
class CmiGap:
    """Window months with no paycheck from an employment that looks like it
    should have produced one — surfaced for the preparer, never blocking."""

    employer: str
    months: tuple[str, ...]


@dataclass(frozen=True)
class CmiColumn:
    """One B122A-1 column: A is debtor_1, B is debtor_2 or a non-filing
    spouse. `excluded` lists § 101(10A)(B)(ii) receipts in the window —
    recorded, shown, and not counted in `monthly_total`."""

    column: str  # "A" | "B"
    lines: tuple[CmiLine, ...]
    excluded: tuple[CmiLine, ...]
    monthly_total: str


@dataclass(frozen=True)
class CmiResult:
    window: CmiWindow
    columns: tuple[CmiColumn, ...]
    combined_monthly_total: str
    annualized: str  # combined x 12 — B122A-1 line 12b's figure
    gaps: tuple[CmiGap, ...]
    # Records that could not enter the calculation at all (no date, no
    # amount, a dangling employment reference). Listing them is the
    # "surfaced, not silently zeroed" rule for malformed inputs.
    problems: tuple[str, ...]


def _column_for(debtor: Debtor | None) -> str | None:
    if debtor is None:
        return None
    if debtor.filing_role == "debtor_1":
        return "A"
    if debtor.filing_role in ("debtor_2", "non_filing_spouse"):
        return "B"
    return None


def _wage_lines(
    window: CmiWindow,
    employments: Sequence[tuple[str, EmploymentBody]],
    pay_periods: Sequence[PayPeriodRecordBody],
    debtors_by_id: dict[str, Debtor],
    problems: list[str],
) -> tuple[dict[str, list[CmiEntry]], dict[str, Decimal], list[CmiGap]]:
    """Column -> wage entries and totals, plus the per-employment gaps."""
    employments_by_id = dict(employments)
    entries: dict[str, list[CmiEntry]] = {}
    totals: dict[str, Decimal] = {}
    paid_months: dict[str, set[str]] = {emp_id: set() for emp_id, _ in employments}

    for record in pay_periods:
        where = f"pay period paid {record.pay_date or 'undated'}"
        employment = (
            employments_by_id.get(record.employment_id)
            if record.employment_id is not None
            else None
        )
        if employment is None:
            problems.append(
                f"{where}: names no existing employment — attach it to the "
                "employer it came from"
            )
            continue
        employer = employment.employer_name or "unnamed employer"
        if record.pay_date is None:
            problems.append(
                f"a pay period from {employer} has no pay date — the lookback "
                "window cannot place it"
            )
            continue
        pay_date = date.fromisoformat(record.pay_date)
        if not window.contains(pay_date):
            continue
        if record.gross is None:
            problems.append(
                f"the {record.pay_date} paycheck from {employer} has no gross "
                "amount — CMI counts gross wages"
            )
            continue
        column = _column_for(debtors_by_id.get(employment.debtor_id or ""))
        if column is None:
            problems.append(
                f"the employment at {employer} names no debtor — say whose "
                "income this is"
            )
            continue
        assert record.employment_id is not None  # employment resolved above
        paid_months[record.employment_id].add(record.pay_date[:7])
        amount = Decimal(record.gross)
        totals[column] = totals.get(column, Decimal("0")) + amount
        entries.setdefault(column, []).append(
            CmiEntry(
                received_on=record.pay_date,
                amount=record.gross,
                description=f"paycheck (gross) — {employer}",
            )
        )

    gaps: list[CmiGap] = []
    for emp_id, employment in employments:
        months_paid = paid_months.get(emp_id, set())
        active = employment.status == "employed" or bool(months_paid)
        if not active:
            continue
        expected = window.months
        if employment.employed_since is not None:
            hired_month = employment.employed_since[:7]
            expected = tuple(m for m in expected if m >= hired_month)
        missing = tuple(m for m in expected if m not in months_paid)
        if missing:
            gaps.append(
                CmiGap(
                    employer=employment.employer_name or "unnamed employer",
                    months=missing,
                )
            )
    return entries, totals, gaps


def _line(
    category: str,
    entries: list[CmiEntry],
    total: Decimal,
    *,
    citation: str = "",
    note: str = "",
) -> CmiLine:
    ordered = tuple(sorted(entries, key=lambda e: (e.received_on, e.description)))
    return CmiLine(
        category=category,
        label=_INCOME_LABELS[category],
        total_received=_money(total),
        monthly_average=_monthly_average(total),
        entries=ordered,
        citation=citation,
        note=note,
    )


def current_monthly_income(
    *,
    filing_date: date,
    debtors: Sequence[Debtor],
    employments: Sequence[tuple[str, EmploymentBody]],
    pay_periods: Sequence[PayPeriodRecordBody],
    other_income: Sequence[OtherIncomeRecordBody],
) -> CmiResult:
    """The CMI derivation for a case, line by line.

    `filing_date` is the anticipated filing date while the case floats
    (resolution's as_of rule); `employments` are (id, body) pairs because
    pay periods reference them by id.
    """
    window = cmi_window(filing_date)
    problems: list[str] = []
    debtors_by_id = {debtor.id: debtor for debtor in debtors}

    wage_entries, wage_totals, gaps = _wage_lines(
        window, employments, pay_periods, debtors_by_id, problems
    )

    # column -> category -> (entries, gross total, expense total)
    buckets: dict[str, dict[str, tuple[list[CmiEntry], Decimal, Decimal]]] = {}
    for record in other_income:
        label = record.category or "uncategorised"
        if record.received_on is None:
            problems.append(
                f"an {label} receipt has no received-on date — the lookback "
                "window cannot place it"
            )
            continue
        if record.category is None:
            problems.append(
                f"the {record.received_on} receipt has no category — say what "
                "kind of income it is"
            )
            continue
        if record.amount is None:
            problems.append(f"the {record.received_on} {label} receipt has no amount")
            continue
        received = date.fromisoformat(record.received_on)
        if not window.contains(received):
            continue
        column = _column_for(debtors_by_id.get(record.debtor_id or ""))
        if column is None:
            problems.append(
                f"the {record.received_on} {label} receipt names no debtor — "
                "say whose income this is"
            )
            continue
        entries, gross, expenses = buckets.setdefault(column, {}).setdefault(
            record.category, ([], Decimal("0"), Decimal("0"))
        )
        gross += Decimal(record.amount)
        if record.expenses is not None:
            expenses += Decimal(record.expenses)
        entries.append(
            CmiEntry(
                received_on=record.received_on,
                amount=record.amount,
                description=(
                    f"{_INCOME_LABELS[record.category]}"
                    + (f" — {record.payer}" if record.payer else "")
                    + (
                        f" (expenses {record.expenses})"
                        if record.expenses is not None
                        else ""
                    )
                ),
            )
        )
        buckets[column][record.category] = (entries, gross, expenses)

    columns: list[CmiColumn] = []
    combined = Decimal("0")
    for column in ("A", "B"):
        lines: list[CmiLine] = []
        excluded: list[CmiLine] = []
        total = Decimal("0")

        if column in wage_entries:
            wage_total = wage_totals[column]
            lines.append(_line("wages", wage_entries[column], wage_total))
            total += wage_total

        for category in _LINE_ORDER[1:]:
            bucket = buckets.get(column, {}).get(category)
            if bucket is None:
                continue
            entries, gross, expenses = bucket
            if category in ("business", "rental"):
                net = gross - expenses
                note = (
                    f"gross receipts {_money(gross)} less ordinary and "
                    f"necessary operating expenses {_money(expenses)}"
                )
                if net < 0:
                    note += "; a net loss enters as zero (B122A-1 lines 5-6)"
                    net = Decimal("0")
                lines.append(_line(category, entries, net, note=note))
                total += net
            else:
                lines.append(_line(category, entries, gross))
                total += gross

        for category in EXCLUDED_INCOME_CATEGORIES:
            bucket = buckets.get(column, {}).get(category)
            if bucket is None:
                continue
            entries, gross, _expenses = bucket
            excluded.append(
                _line(
                    category,
                    entries,
                    gross,
                    citation=EXCLUSION_CITATION,
                    note="recorded and excluded from current monthly income",
                )
            )

        if lines or excluded:
            columns.append(
                CmiColumn(
                    column=column,
                    lines=tuple(lines),
                    excluded=tuple(excluded),
                    monthly_total=_monthly_average(total),
                )
            )
            combined += total

    monthly = (combined / 6).quantize(_CENT, rounding=ROUND_HALF_UP)
    return CmiResult(
        window=window,
        columns=tuple(columns),
        combined_monthly_total=f"{monthly:f}",
        annualized=f"{(monthly * 12).quantize(_CENT):f}",
        gaps=tuple(gaps),
        problems=tuple(problems),
    )
