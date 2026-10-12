"""B122C-1 @ 2019-10-01 (revision 10/19) — the Chapter 13 CMI statement and
commitment-period calculation's mapping (issue #365).

Part 1 (lines 1-11) is B122A-1's Part 1 under different widget names: the
same § 101(10A) derivation (core/cmi.py), landed through the same
`fill_cmi_column` reading — a column fills only when the derivation
produced it, and a filled column writes $0 on its silent lines. Parts 2-3
(lines 12-21) are the means-test engine's Chapter 13 branch
(core/means_test.py): the marital adjustment is subtracted BEFORE the median
comparison here (line 13, then line 15b = line 14 x 12 against line 16c —
§ 1325(b)(3)), and the commitment period is a second comparison of its own
(lines 18-21 — § 1325(b)(4)), so every one of those boxes takes the
engine's line of the same number, exactly as B122A-2's do.

Readings this revision forces, each argued where it happens:

- **Line 1 has two answers, not three.** "Married" covers a spouse filing
  with you AND a non-filing spouse — both fill Column B — so the entered
  or derived marital status collapses to married/single here; line 13's
  three-way box is where "spouse not filing" is asked.
- **Lines 12-21 fill only when the engine can run them**, which needs the
  household composition and debtor 1's state. Absent either, the boxes
  stay blank and the completeness gate owns the complaint (B122A-1's own
  rule for its lines 12-14). Anything else the engine refuses — a marital
  adjustment with no Column B, a state the median table does not carry —
  is a present fact that cannot land, and a projection error.
- **The page 1 boxes follow lines 17 and 21**: box 1 or 2 from the
  § 1325(b)(3) comparison, box 3 or 4 from the § 1325(b)(4) one. They are
  independent, and the form prints both.
- **A Chapter 7 case never projects here.** The form exists for a Chapter
  13 filing; `packet_form_series` puts it in the set by chapter.

Known defects of the official PDF, verified by widget geometry (the spec's
notes): the net business/rental boxes, line 11's total, line 14, line 16c,
line 19b and line 13's total each fill two or three printed boxes from one
widget (used constructively — one value, always agreeing), and line 15b's
widget has an orphaned second instance below page 2's bottom edge.
"""

from __future__ import annotations

from typing import Final

from ..form_fill import Option, Text
from ..form_templates import FormRelease
from ..means_test import (
    COMMITMENT_PERIOD_BELOW_MEDIAN,
    FORM_122C1,
    MeansTestError,
    MeansTestResult,
    resolve_means_test_data,
    run_means_test,
)
from .b122a1 import (
    compute_cmi,
    fill_caption,
    fill_cmi_column,
    fill_signature_dates,
    household_size,
    marital_filing_status,
    means_test_as_of,
    means_test_inputs,
)
from .b122a2 import build_means_test_case
from .shared import CaseFile, FieldValues, FormProjectionError, format_money, row_fill

# Engine line (form 122C-1) -> the spec field it lands on. Lines the engine
# emits that are copies of a shared widget (12 and 18 = line 11's box, 15a
# = line 14's, 20a = line 19b's, 20c = line 16c's) are absent here; line
# 17 and line 21 are checkbox answers, landed by `_determination`.
_LINE_FIELDS: Final = {
    "13": "marital_adjustment_total",
    "14": "current_monthly_income",
    "15b": "annualized_cmi",
    "16c": "median_income",
    "19a": "commitment_marital_adjustment",
    "19b": "commitment_monthly_income",
    "20b": "commitment_annualized",
}


def _marital_status(
    case_file: CaseFile, values: FieldValues, problems: list[str]
) -> str | None:
    status, source = marital_filing_status(case_file)
    if status is None:
        problems.append(source)
        return None
    values["marital_filing_status"] = Option(
        "single" if status == "not_married" else "married"
    )
    return status


def _marital_adjustment_rows(
    release: FormRelease, values: FieldValues, case_file: CaseFile, problems: list[str]
) -> None:
    """Line 13's itemized rows, from the means_test_input record."""
    for index, item in enumerate(means_test_inputs(case_file).marital_adjustments):
        row_fill(
            release,
            values,
            "marital_adjustment_purpose",
            index,
            Text(item.description) if item.description else None,
            problems,
        )
        row_fill(
            release,
            values,
            "marital_adjustment_amount",
            index,
            Text(format_money(item.amount)) if item.amount else None,
            problems,
        )


def _determination(
    values: FieldValues,
    case_file: CaseFile,
    status: str | None,
    problems: list[str],
) -> None:
    """Lines 12-21 and the two page 1 boxes, from the engine's Chapter 13
    branch — blank until the household and state exist."""
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
    if status is not None:
        values["marital_adjustment_status"] = Option(
            {
                "not_married": "1",
                "married_filing_jointly": "2",
            }.get(status, "3")
        )
    if state is None or size is None:
        return

    try:
        data = resolve_means_test_data(means_test_as_of(case_file)[0])
        result: MeansTestResult = run_means_test(build_means_test_case(case_file), data)
    except (MeansTestError, LookupError) as error:
        problems.extend(
            list(error.problems) if isinstance(error, MeansTestError) else [str(error)]
        )
        return

    for line in result.lines:
        if line.form != FORM_122C1:
            continue
        field_id = _LINE_FIELDS.get(line.line)
        if field_id is not None:
            values[field_id] = Text(format_money(line.amount))

    assert result.comparison is not None  # the Chapter 13 branch always compares
    if result.comparison.above_median:
        values["median_comparison"] = Option("17b")
        values["caption.disposable_income_box"] = Option("2")
    else:
        values["median_comparison"] = Option("17a")
        values["caption.disposable_income_box"] = Option("1")
    assert result.commitment is not None  # ditto the commitment period
    if result.commitment.months == COMMITMENT_PERIOD_BELOW_MEDIAN:
        values["commitment_comparison"] = Option("yes")
        values["caption.commitment_period_box"] = Option("3")
    else:
        values["commitment_comparison"] = Option("no")
        values["caption.commitment_period_box"] = Option("4")


def project_b122c1_1019(release: FormRelease, case_file: CaseFile) -> FieldValues:
    """The values for form/b122c1@2019-10-01, from one case's facts."""
    if case_file.case.chapter != 13:
        raise FormProjectionError(
            [
                f"Form 122C-1 is the Chapter 13 statement of current monthly "
                f"income — this is a Chapter {case_file.case.chapter} case"
            ]
        )
    values: FieldValues = {}
    problems: list[str] = []

    fill_caption(values, case_file)
    status = _marital_status(case_file, values, problems)

    cmi = compute_cmi(case_file)
    problems.extend(cmi.problems)

    columns = {column.column: column for column in cmi.columns}
    for index, key in enumerate(("A", "B")):
        column = columns.get(key)
        if column is not None:
            fill_cmi_column(release, values, column, index, problems)
    if cmi.columns:
        values["total_cmi"] = Text(format_money(cmi.combined_monthly_total))
        _determination(values, case_file, status, problems)

    _marital_adjustment_rows(release, values, case_file, problems)
    fill_signature_dates(values, case_file)

    if problems:
        raise FormProjectionError(problems)
    return values
