"""`GET /v1/cases/<id>/means-test`'s body (issue #349): the whole § 707(b)
trace for one case, assembled from the SAME calls the B122A-1 and B122A-2
projections make, and nothing else.

NO ARITHMETIC HERE. `build_means_test_case` (form_projections/b122a2.py)
derives the engine's inputs from the case records exactly as the packet
does — the CMI over the dated income history, the debt totals from the
claims, the under-18 count from the dependents — `resolve_means_test_data`
pins the datasets as of the same date the forms use (`means_test_as_of`),
and `run_means_test` computes. This module only lays the result out for the
screen: every figure the endpoint returns is a figure one of those three
produced, with the source string the engine attached to it, so the screen
can never disagree with the form.

PROGRESSIVE, LIKE `/summary` AND `/standards`. A case mid-intake has an
incomplete means test the way it has an incomplete schedule: the engine's
refusals (no household entered yet, a county the Local Standards do not
carry, a figure past a statutory cap) become `problems` on a 200, next to
whatever DID compute — the CMI derivation and its window always do.

Unlike `/standards`, a registry `LookupError` is caught here too. There the
as-of date is the case's creation date and a registry that cannot describe
it is the server's problem; here it is the attorney's EXPECTED FILING DATE,
typed on the petition screen, and a planned date before the datasets begin
is an input the screen must explain next to the field, not a 500 on the
whole page. The problem carries the registry's own message, which names
the series and the date it begins.
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import date

from .cmi import CmiColumn, CmiEntry, CmiGap, CmiLine, CmiResult
from .form_projections import CaseFile
from .form_projections.b122a1 import (
    marital_filing_status,
    means_test_as_of,
    means_test_inputs,
)
from .form_projections.b122a2 import build_means_test_case
from .means_test import (
    PRESUMPTION_EXEMPTIONS,
    MeansTestCase,
    MeansTestError,
    MeansTestLine,
    MeansTestResult,
    MedianComparison,
    irs_family_size,
    irs_housing_family_size,
    median_household_size,
    presumption_exemption,
    resolve_means_test_data,
    run_means_test,
)


@dataclass(frozen=True)
class MeansTestTrace:
    """One run of the test for the screen: the inputs as the engine read
    them, the outcome when it could be reached, and every reason it could
    not."""

    as_of: date
    as_of_source: str
    release_ids: dict[str, str]
    case: MeansTestCase
    marital_filing_status: str | None
    marital_source: str
    result: MeansTestResult | None
    problems: tuple[str, ...]


def trace_means_test(case_file: CaseFile) -> MeansTestTrace:
    """The trace for one case file — pure, like the projections it calls,
    and never raising: every refusal lands in `problems`."""
    as_of, as_of_source = means_test_as_of(case_file)
    test = build_means_test_case(case_file)
    problems = list(test.cmi.problems)

    status, status_source = marital_filing_status(case_file)
    if status is None:
        problems.append(status_source)

    result: MeansTestResult | None = None
    release_ids: dict[str, str] = {}
    try:
        data = resolve_means_test_data(as_of)
    except LookupError as error:
        problems.append(str(error))
    else:
        release_ids = data.release_ids
        try:
            result = run_means_test(test, data)
        except MeansTestError as error:
            problems.extend(error.problems)

    return MeansTestTrace(
        as_of=as_of,
        as_of_source=as_of_source,
        release_ids=release_ids,
        case=test,
        marital_filing_status=status,
        marital_source=status_source,
        result=result,
        problems=tuple(problems),
    )


# ── the wire shape ──────────────────────────────────────────────


def _entry_json(entry: CmiEntry) -> dict[str, object]:
    return {
        "receivedOn": entry.received_on,
        "amount": entry.amount,
        "description": entry.description,
    }


def _cmi_line_json(line: CmiLine) -> dict[str, object]:
    body: dict[str, object] = {
        "category": line.category,
        "label": line.label,
        "totalReceived": line.total_received,
        "monthlyAverage": line.monthly_average,
        "citation": line.citation,
        "note": line.note,
        "entries": [_entry_json(entry) for entry in line.entries],
    }
    if line.gross_monthly_average is not None:
        body["grossMonthlyAverage"] = line.gross_monthly_average
    if line.expenses_monthly_average is not None:
        body["expensesMonthlyAverage"] = line.expenses_monthly_average
    return body


def _column_json(column: CmiColumn) -> dict[str, object]:
    return {
        "column": column.column,
        "lines": [_cmi_line_json(line) for line in column.lines],
        "excluded": [_cmi_line_json(line) for line in column.excluded],
        "monthlyTotal": column.monthly_total,
    }


def _gap_json(gap: CmiGap) -> dict[str, object]:
    return {"employer": gap.employer, "months": list(gap.months)}


def _cmi_json(cmi: CmiResult) -> dict[str, object]:
    return {
        "window": {
            "filingDate": cmi.window.filing_date.isoformat(),
            "start": cmi.window.start.isoformat(),
            "end": cmi.window.end.isoformat(),
            "months": list(cmi.window.months),
        },
        "columns": [_column_json(column) for column in cmi.columns],
        "combinedMonthlyTotal": cmi.combined_monthly_total,
        "annualized": cmi.annualized,
        "gaps": [_gap_json(gap) for gap in cmi.gaps],
        "problems": list(cmi.problems),
    }


def _comparison_json(comparison: MedianComparison) -> dict[str, object]:
    return {
        "state": comparison.state,
        "householdSize": comparison.household_size,
        "monthlyCmi": comparison.monthly_cmi,
        "annualizedCmi": comparison.annualized_cmi,
        "annualMedian": comparison.annual_median,
        "aboveMedian": comparison.above_median,
        "source": comparison.source,
    }


def _line_json(line: MeansTestLine) -> dict[str, object]:
    return {
        "line": line.line,
        "label": line.label,
        "amount": line.amount,
        "source": line.source,
    }


def _size_json(size: tuple[int, str] | None) -> dict[str, object]:
    """A household size and where it came from, `null` in both when the
    inputs cannot answer it yet — the client labels the box, not the
    server's silence."""
    if size is None:
        return {"value": None, "source": None}
    return {"value": size[0], "source": size[1]}


def means_test_json(trace: MeansTestTrace) -> dict[str, object]:
    """Money as strings, absent optional figures as `null` (this is a
    read-only trace the screen labels box by box, the `/standards` rule),
    and `outcome` `undetermined` with the reasons in `problems` when the
    engine refused."""
    inputs = trace.case.inputs
    exemption = presumption_exemption(inputs)
    result = trace.result
    return {
        "asOf": trace.as_of.isoformat(),
        "asOfSource": trace.as_of_source,
        "releaseIds": dict(trace.release_ids),
        "jurisdiction": {
            "state": trace.case.state or None,
            "county": trace.case.county or None,
            "district": trace.case.district,
        },
        "maritalFilingStatus": {
            "value": trace.marital_filing_status,
            "source": trace.marital_source,
        },
        "household": {
            "peopleUnder65": inputs.people_under_65,
            "people65OrOlder": inputs.people_65_or_older,
            "medianHouseholdSize": _size_json(median_household_size(inputs)),
            "irsFamilySize": _size_json(irs_family_size(inputs)),
            "irsHousingFamilySize": _size_json(irs_housing_family_size(inputs)),
            "childrenUnder18": trace.case.children_under_18,
        },
        "exemptions": {
            "nonConsumerDebts": inputs.non_consumer_debts is True,
            "disabledVeteran": inputs.disabled_veteran is True,
            "reservistNationalGuard": inputs.reservist_national_guard is True,
            "applied": exemption[0] if exemption is not None else None,
            "rule": exemption[1] if exemption is not None else None,
            "available": [flag for flag, _ in PRESUMPTION_EXEMPTIONS],
        },
        "cmi": _cmi_json(trace.case.cmi),
        "debt": {
            "priorityTotal": trace.case.priority_debt_total,
            "nonpriorityUnsecuredTotal": trace.case.nonpriority_unsecured_total,
        },
        "comparison": (
            _comparison_json(result.comparison)
            if result is not None and result.comparison is not None
            else None
        ),
        "outcome": result.outcome if result is not None else "undetermined",
        "determinedBy": result.determined_by if result is not None else None,
        "lines": [_line_json(line) for line in result.lines] if result else [],
        "problems": list(trace.problems),
    }


__all__ = [
    "MeansTestTrace",
    "means_test_inputs",
    "means_test_json",
    "trace_means_test",
]
