"""B122A-1 @ 2019-12-01 (revision 12/19) — the CMI statement's mapping.

Every income line is the § 101(10A) derivation (core/cmi.py) projected onto
the form: Column A is the derivation's column A (debtor 1), Column B its
column B (debtor 2 or a non-filing spouse), and the six-month averages land
exactly as computed — the projection never re-derives arithmetic the
derivation already owns. The filing-date stand-in is the petition's
expected filing date when the attorney has entered one (issue #349 — the
means test is advice about a filing that has not happened, and the six-
month window moves with the planned date), else the case's creation date,
as 106C's registry reads use, so the projection stays pure over the case
file. `means_test_as_of` is that rule, exported so the means-test endpoint
and this form read the same window.

Readings this revision forces, each argued where it happens:

- **A column fills only when the derivation produced it.** The form's own
  instruction is to write $0 on empty lines — but only for columns being
  filled out at all, so a column with any computed line gets $0 on its
  silent lines while an absent column stays blank (progressive intake).
- **CMI problems are projection errors.** A pay period that cannot enter
  the calculation (no date, no gross, a dangling employment) is a present
  fact that cannot land; printing a total that silently omits it would
  understate CMI on a signed form. Gaps stay advisory — a missing month is
  an absence, and absence is intake's business.
- **The line 8 Social Security contention boxes** print the excluded
  `social_security_act_benefit` receipts' monthly average — recorded,
  shown, and never counted, § 101(10A)(B)(ii)'s rule made visible.
- **The determination (lines 12-14 and the page 1 box)** fills only when
  the median comparison can run: it needs the entered household
  composition (means_test_input) and debtor 1's state. Absent either, the
  boxes stay blank and the completeness gate owns the complaint. An
  entered Form 122A-1Supp exemption (issue #349) skips the comparison and
  checks the page 1 box the supplement directs — box 1 for non-consumer
  debts and the disabled veteran, box 3 for the reservist whose test "does
  not apply now".
- **Line 1 reads the entered marital status first** (issue #349's
  `means_test_input.marital_filing_status`), then the debtor records — the
  entered value exists for the "living separately" declaration the records
  cannot express, under which Column B is not filled in.

Known defects of the official PDF, verified by widget geometry (the spec's
notes): Debtor 2's line-4 box is misnamed `Debto2.Quest2.2`, and line 12a's
copy box carries no widget at all.
"""

from __future__ import annotations

from datetime import date
from decimal import Decimal
from typing import Final

from insolvia_core.means_test_inputs import MeansTestInputBody

from ..cmi import CmiColumn, CmiResult, current_monthly_income
from ..form_fill import Option, Text
from ..form_templates import FormRelease
from ..means_test import median_household_size, presumption_exemption
from ..ust_data import median_income_table
from .shared import (
    CaseFile,
    FieldValues,
    FormProjectionError,
    format_date,
    format_money,
    full_name,
    row_fill,
)

# CMI category -> the spec field its monthly average lands on.
_CATEGORY_FIELDS: Final = {
    "wages": "wages",
    "alimony_maintenance": "alimony",
    "household_contributions": "household_contributions",
    "interest_dividends_royalties": "interest_dividends",
    "unemployment": "unemployment",
    "pension_retirement": "pension",
    "other": "other_income_1",
}

# Every per-column money line the form instructs to fill with $0 when a
# column is being filled out at all.
_ZERO_FILLED: Final = (
    "wages",
    "alimony",
    "household_contributions",
    "business_gross",
    "business_expenses",
    "business_net",
    "rental_gross",
    "rental_expenses",
    "rental_net",
    "interest_dividends",
    "unemployment",
    "pension",
    "other_income_1",
    "other_income_2",
    "column_total",
)


def means_test_as_of(case_file: CaseFile) -> tuple[date, str]:
    """The registry-resolution and lookback date, and where it came from:
    the petition's expected filing date when entered
    (`petition.expected_filing_date`), else the case's creation date
    standing in for the filing date until `case.filed_at` exists (the same
    reading 106C's projection documents)."""
    petition = case_file.petition
    if petition is not None and petition.expected_filing_date is not None:
        return (
            date.fromisoformat(petition.expected_filing_date),
            "petition.expected_filing_date",
        )
    return date.fromisoformat(case_file.case.created_at[:10]), "case.created_at"


def _as_of(case_file: CaseFile) -> date:
    return means_test_as_of(case_file)[0]


def means_test_inputs(case_file: CaseFile) -> MeansTestInputBody:
    """The case's entered figures — the first record, or an empty body when
    none has been entered yet (the packet gate owns the cardinality)."""
    return (
        case_file.means_test_inputs[0]
        if case_file.means_test_inputs
        else MeansTestInputBody()
    )


def compute_cmi(case_file: CaseFile) -> CmiResult:
    """The § 101(10A) derivation for this case file — exported so packet
    assembly, B122A-2 and the means-test endpoint read the same figures
    this form prints."""
    return current_monthly_income(
        filing_date=_as_of(case_file),
        debtors=case_file.debtors,
        employments=case_file.employments,
        pay_periods=case_file.pay_period_records,
        other_income=case_file.other_income_records,
        overrides=means_test_inputs(case_file).income_overrides,
    )


def household_size(case_file: CaseFile) -> int | None:
    """Line 13's household size: the entered composition
    (means_test_input) — its own override, or the age bands summed — which
    is also what the engine compares against the median. One source."""
    size = median_household_size(means_test_inputs(case_file))
    return size[0] if size is not None else None


def marital_filing_status(case_file: CaseFile) -> tuple[str | None, str]:
    """Line 1's answer and where it came from: the entered
    `means_test_input.marital_filing_status` when present, else the debtor
    records. `None` (with the reason) when the records contradict each
    other — a debtor_2 AND a non-filing spouse."""
    entered = means_test_inputs(case_file).marital_filing_status
    if entered is not None:
        return entered, "entered (means_test_input.marital_filing_status)"
    debtor2 = case_file.debtor("debtor_2")
    spouse = case_file.debtor("non_filing_spouse")
    if debtor2 is not None and spouse is not None:
        return None, (
            "the case has both a debtor_2 and a non_filing_spouse record — "
            "line 1 cannot answer both 'filing with you' and 'not filing'"
        )
    if debtor2 is not None:
        return "married_filing_jointly", "the case's debtor_2 record"
    if spouse is not None:
        return "married_not_filing_same_household", (
            "the case's non_filing_spouse record"
        )
    return "not_married", "the case's debtor records (no spouse recorded)"


def _marital_status(
    case_file: CaseFile, values: FieldValues, problems: list[str]
) -> None:
    status, source = marital_filing_status(case_file)
    if status is None:
        problems.append(source)
        return
    if status == "married_filing_jointly":
        # The PDF's own misspelled export state.
        values["marital_filing_status"] = Option("Maried and filing")
    elif status == "married_not_filing_same_household":
        values["marital_filing_status"] = Option("Married but not filing")
        values["married_not_filing_household"] = Option("together")
    elif status == "married_not_filing_separated":
        values["marital_filing_status"] = Option("Married but not filing")
        values["married_not_filing_household"] = Option("separately")
    else:
        values["marital_filing_status"] = Option("Not married")


def _column(
    release: FormRelease,
    values: FieldValues,
    column: CmiColumn,
    index: int,
    problems: list[str],
) -> None:
    filled: set[str] = set()

    def put(field_id: str, amount: str) -> None:
        filled.add(field_id)
        row_fill(release, values, field_id, index, Text(format_money(amount)), problems)

    for line in column.lines:
        if line.category in ("business", "rental"):
            prefix = line.category
            assert line.gross_monthly_average is not None
            assert line.expenses_monthly_average is not None
            put(f"{prefix}_gross", line.gross_monthly_average)
            put(f"{prefix}_expenses", line.expenses_monthly_average)
            put(f"{prefix}_net", line.monthly_average)
        else:
            put(_CATEGORY_FIELDS[line.category], line.monthly_average)
            if line.category == "other":
                payers = sorted(
                    {
                        entry.description.split(" — ")[-1]
                        for entry in line.entries
                        if " — " in entry.description
                    }
                )
                source = "; ".join(payers) if payers else "Other income"
                row_fill(
                    release,
                    values,
                    "other_income_source_1",
                    0,
                    Text(source),
                    problems,
                )
    for line in column.excluded:
        if line.category == "social_security_act_benefit":
            put("ssa_contention", line.monthly_average)
    put("column_total", column.monthly_total)

    # The form's own instruction: write $0 on a filled-out column's silent
    # lines. ssa_contention stays blank — it is a contention, not an amount.
    for field_id in _ZERO_FILLED:
        if field_id not in filled and field_id != "other_income_2":
            row_fill(release, values, field_id, index, Text("0.00"), problems)


def _determination(
    release: FormRelease,
    values: FieldValues,
    case_file: CaseFile,
    cmi: CmiResult,
    problems: list[str],
) -> None:
    values["annualized_cmi"] = Text(format_money(cmi.annualized))

    exemption = presumption_exemption(means_test_inputs(case_file))
    if exemption is not None:
        # Form 122A-1Supp's instruction: the test ends, and the page 1 box
        # says so — box 3 for the reservist ("does not apply now"), box 1
        # for the other two. Lines 12-14 stay blank.
        values["caption.presumption_box"] = Option(
            "Does not apply"
            if exemption[0] == "reservist_national_guard"
            else "No Abuse"
        )
        return

    debtor1 = case_file.debtor("debtor_1")
    state = (
        debtor1.residence_address.state
        if debtor1 is not None and debtor1.residence_address.state
        else None
    )
    size = household_size(case_file)
    if state is not None:
        values["median_state"] = Text(state.upper())
    if size is not None:
        values["median_household_size"] = Text(str(size))
    if state is None or size is None:
        # An absent fact leaves the comparison blank; the completeness gate
        # owns "the means test has not been answered yet".
        return

    _, table = median_income_table(_as_of(case_file))
    try:
        median = table.annual_median(state, size)
    except (KeyError, ValueError) as error:
        problems.append(str(error))
        return
    values["median_income"] = Text(format_money(f"{median:f}"))
    if Decimal(cmi.combined_monthly_total) * 12 > median:
        values["median_comparison"] = Option("12b more than 13")
        values["caption.presumption_box"] = Option("Presumption of abuse applies")
    else:
        values["median_comparison"] = Option("12b less or equal to 13")
        values["caption.presumption_box"] = Option("No Abuse")


def _signatures(values: FieldValues, case_file: CaseFile) -> None:
    for role, field_id in (
        ("debtor_1", "debtor1_signature_date"),
        ("debtor_2", "debtor2_signature_date"),
    ):
        debtor = case_file.debtor(role)
        if debtor is not None and debtor.signed_at is not None:
            values[field_id] = Text(format_date(debtor.signed_at[:10]))


def _caption(values: FieldValues, case_file: CaseFile) -> None:
    debtor1 = case_file.debtor("debtor_1")
    debtor2 = case_file.debtor("debtor_2")
    if debtor1 is not None and (name := full_name(debtor1.name)):
        values["caption.debtor1_name"] = Text(name)
    if debtor2 is not None and (name := full_name(debtor2.name)):
        values["caption.debtor2_name"] = Text(name)
    if case_file.case.district:
        values["caption.district"] = Text(case_file.case.district)


def project_b122a1_1219(release: FormRelease, case_file: CaseFile) -> FieldValues:
    """The values for form/b122a1@2019-12-01, from one case's facts."""
    values: FieldValues = {}
    problems: list[str] = []

    _caption(values, case_file)
    _marital_status(case_file, values, problems)

    cmi = compute_cmi(case_file)
    problems.extend(cmi.problems)

    columns = {column.column: column for column in cmi.columns}
    for index, key in enumerate(("A", "B")):
        column = columns.get(key)
        if column is not None:
            _column(release, values, column, index, problems)
    if cmi.columns:
        values["total_cmi"] = Text(format_money(cmi.combined_monthly_total))
        _determination(release, values, case_file, cmi, problems)

    _signatures(values, case_file)

    if problems:
        raise FormProjectionError(problems)
    return values
