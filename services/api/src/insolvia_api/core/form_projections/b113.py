"""B113 @ 2017-12-01 (revision 12/17) — Official Form 113, the national
Chapter 13 plan (issue #367).

WHAT PRINTS FROM WHERE. The proposal is the case's one `plans` record
(insolvia_core/plans.py, #366); every figure the form prints beyond what was
typed — each run of payments, the total, each claim's monthly payment and
estimated total, the trustee's fee, the liquidation amount, the exhibit —
is the plan calculator's (core/chapter13_plan.py), run here over the same
file the other projections read. Nothing is recomputed: the calculator
already reuses the means test, Schedules I/J, liens.py and the exemption
workbench, and this module only lands its answers on the printed boxes.

THE FORM IS FLAT (no AcroForm), so every value lands on an overlay box
the engine draws; the table cells carry their own point size
(`OverlayBox.size`).

A PLAN THAT CANNOT BE PRINTED HONESTLY IS REFUSED. The form states
figures a court will hold the debtor to, so any of these is a projection
error rather than a blank:

- the calculator's own problems (a missing input the arithmetic needs);
- an infeasible plan — the payments do not cover what the plan promises,
  and the exhibit would total more than § 2.5 can fund;
- a plan that fails the § 1325(a)(4) liquidation test — § 5.1 itself
  promises "payments … in at least this amount", so printing a smaller
  plan payout under it would contradict the form;
- more rows than the form prints (two payment runs; two rows in each of
  §§ 3.1, 3.2, 3.5 and 6.1) — "insert additional lines" is an edit to
  the official form's text, which Rule 3015(c) makes a nonstandard
  provision, and Part 8 is not modelled;
- a priority class paid less than in full: § 1322(a)(4)'s treatment,
  which § 4.5 prints claim by claim and the plan record does not name.

WHAT IS FIXED BECAUSE THE RECORD DOES NOT MODEL IT. Part 1 lines 1.2 and
1.3 print "Not included", and §§ 3.3, 3.4, 4.5, 5.2, 5.3 and 8.1 print
"None": the plan record has no § 506-excluded claim, no lien avoidance, no
separately classified or long-term unsecured debt and no nonstandard
provision, so a "None" there is this plan's true answer, not a default.
How payments reach the trustee (§ 2.2), income tax refunds (§ 2.3) and
vesting (Part 7) are not modelled and stay blank for the preparer; the
signature lines stay wet; the case number is the court's.

WHICH DATE THE LIQUIDATION FIGURE RESOLVES AS OF. The exemption workbench
resolves its registry as of the petition's expected filing date where one
is typed, and otherwise floats on "today" — the plan screen's today. A
projection must be pure (the goldens pin its bytes), so here "today" is
the case's creation date, the stand-in B106C's projection already uses
for the same registry.

Chapter 7 cases never file this form (packet assembly's
`CHAPTER_13_FORM_SERIES`); a case with no plan projects its caption alone,
and the gate names the missing plan.
"""

from __future__ import annotations

from collections.abc import Sequence
from datetime import date
from decimal import Decimal
from typing import TYPE_CHECKING, Final

from insolvia_core.claims import ClaimBody
from insolvia_core.plans import PlanBody, SecuredTreatment

from ..form_fill import Check, Text
from ..form_templates import FormRelease
from .b2030 import attorney_of, district_parts
from .shared import (
    CaseFile,
    FieldValues,
    FormProjectionError,
    format_date,
    format_money,
    full_name,
    row_fill,
    text_or_none,
    wrap_width,
)

if TYPE_CHECKING:
    from ..chapter13_plan import PayoutRow, PlanCalculation
    from ..liens import LienFigures

_ZERO: Final = Decimal("0.00")

# The printed rows per table, and the printed lines per wrapped cell.
_RUNS: Final = 2
_ROWS: Final = 2


def _rate(value: str) -> str:
    """A stored percentage as the form prints it before its own `%`:
    `7` -> `7`, `6.250` -> `6.25`."""
    text = f"{Decimal(value).normalize():f}"
    return text


def _money_text(value: Decimal) -> Text:
    return Text(format_money(value))


def _lines(
    release: FormRelease,
    values: FieldValues,
    field_id: str,
    row: int,
    per_row: int,
    value: str | None,
    problems: list[str],
) -> None:
    """Wrap `value` across one row's `per_row` printed lines of a repeated
    field whose boxes are laid out row-major."""
    if not value:
        return
    spec = release.field(field_id)
    first = spec.pdf_names[row * per_row]
    box = release.boxes[first]
    wrapped = wrap_width(
        value,
        points=box.w,
        lines=per_row,
        where=f"{field_id} row {row + 1}",
        problems=problems,
        size=box.size,
    )
    for index, line in enumerate(wrapped):
        row_fill(release, values, field_id, row * per_row + index, Text(line), problems)


def _as_of(case_file: CaseFile) -> date:
    """The "today" the exemption workbench floats on, for the liquidation
    figure — the case's creation date. The workbench itself prefers the
    petition's expected filing date where one is typed
    (`exemption_analysis.resolution_date`); see the module docstring."""
    return date.fromisoformat(case_file.case.created_at[:10])


def calculate(case_file: CaseFile, plan: PlanBody) -> PlanCalculation:
    """The plan calculator over this file — the one call the projection
    makes. Imported here, not at module level: the calculator imports the
    projection package (for Schedules I/J and the means test), and this
    module is part of it."""
    from ..chapter13_plan import build_plan_inputs, calculate_plan, exemption_case_of

    inputs = build_plan_inputs(
        case_file, exemption_case_of(case_file), today=_as_of(case_file)
    )
    return calculate_plan(inputs, plan)


def _payment_runs(schedule: Sequence[tuple[int, Decimal]]) -> list[tuple[Decimal, int]]:
    """The month-by-month schedule compressed into runs of equal payments."""
    runs: list[tuple[Decimal, int]] = []
    for _month, payment in schedule:
        if runs and runs[-1][0] == payment:
            runs[-1] = (payment, runs[-1][1] + 1)
        else:
            runs.append((payment, 1))
    return runs


def _row(calc: PlanCalculation, key: str) -> PayoutRow | None:
    for summary in calc.classes:
        for row in summary.rows:
            if row.key == key:
                return row
    return None


def _class_payout(calc: PlanCalculation, key: str) -> Decimal:
    return next((s.principal + s.interest for s in calc.classes if s.key == key), _ZERO)


def _treated(
    case_file: CaseFile, plan: PlanBody, kind: str
) -> list[tuple[str, ClaimBody, SecuredTreatment]]:
    """The case's secured claims the plan treats as `kind`, in claim order —
    the calculator's own order."""
    rows: list[tuple[str, ClaimBody, SecuredTreatment]] = []
    for claim_id, claim in case_file.claims:
        if claim.claim_class != "secured":
            continue
        treatment = next(
            (t for t in plan.secured_treatments if t.claim_id == claim_id), None
        )
        if treatment is not None and treatment.treatment == kind:
            rows.append((claim_id, claim, treatment))
    return rows


def _too_many(section: str, found: int, printed: int, problems: list[str]) -> None:
    problems.append(
        f"§ {section}: the plan lists {found}; Official Form 113 prints {printed}"
        " rows, and adding rows is a nonstandard change (Part 8), which is not"
        " prepared here"
    )


def _caption(
    release: FormRelease,
    values: FieldValues,
    case_file: CaseFile,
    problems: list[str],
) -> None:
    for index, part in enumerate(district_parts(case_file.case.district)):
        row_fill(
            release, values, "caption.district", index, text_or_none(part), problems
        )
    for role, field_id in (
        ("debtor_1", "caption.debtor1_name"),
        ("debtor_2", "caption.debtor2_name"),
    ):
        debtor = case_file.debtor(role)
        if debtor is not None and (name := full_name(debtor.name)):
            values[field_id] = Text(name)
    debtor_1 = case_file.debtor("debtor_1")
    if debtor_1 is not None and (name := full_name(debtor_1.name)):
        spec = release.field("header.debtor_name")
        values["header.debtor_name"] = {box: Text(name) for box in spec.pdf_names}
    for role, field_id in (
        ("debtor_1", "sign.debtor1_executed_on"),
        ("debtor_2", "sign.debtor2_executed_on"),
    ):
        debtor = case_file.debtor(role)
        if debtor is not None and debtor.signed_at:
            values[field_id] = Text(format_date(debtor.signed_at))
    attorney = attorney_of(case_file)
    if attorney is not None and attorney.signature_date:
        values["sign.attorney_date"] = Text(format_date(attorney.signature_date))


def project_b113_1217(release: FormRelease, case_file: CaseFile) -> FieldValues:
    values: FieldValues = {}
    problems: list[str] = []
    _caption(release, values, case_file, problems)
    if not case_file.plans:
        # The gate names the missing plan (packet_assembly); the caption
        # alone is what a preview of an unwritten plan honestly shows.
        if problems:
            raise FormProjectionError(problems)
        return values
    plan = case_file.plans[0]
    calc = calculate(case_file, plan)

    problems.extend(f"Plan: {problem}" for problem in calc.problems)
    feasibility = calc.feasibility
    if feasibility.feasible is False:
        problems.extend(
            f"The plan is not feasible — {reason}" for reason in feasibility.reasons
        )
    if calc.best_interests.passes is False:
        problems.append(
            "The plan fails the § 1325(a)(4) liquidation test: it pays general"
            f" unsecured creditors {calc.best_interests.plan_percentage}% against"
            f" the {calc.best_interests.liquidation_percentage}% a Chapter 7"
            " liquidation would — § 5.1 of the form promises at least the"
            " liquidation amount"
        )
    if plan.priority_percentage is not None and Decimal(plan.priority_percentage) < 100:
        problems.append(
            "The plan pays priority claims less than in full — § 1322(a)(4)'s"
            " treatment of an assigned domestic support obligation, which § 4.5"
            " lists claim by claim and is not prepared here"
        )
    if problems:
        raise FormProjectionError(problems)

    _part_2(release, values, calc, problems)
    liens = _liens(case_file)
    exhibit_a = _section_3_1(release, values, case_file, plan, calc, liens, problems)
    exhibit_b = _section_3_2(release, values, case_file, plan, calc, liens, problems)
    _section_3_5(release, values, case_file, plan, liens, problems)
    values["line_3_3_none"] = Check()
    values["line_3_4_none"] = Check()
    values[
        "line_1_1_included" if exhibit_b is not None else "line_1_1_not_included"
    ] = Check()
    values["line_1_2_not_included"] = Check()
    values["line_1_3_not_included"] = Check()

    exhibit_e = _part_4(values, plan, calc)
    exhibit_f = _part_5(values, plan, calc)
    _part_6(release, values, case_file, problems)
    values["line_8_1_none"] = Check()

    lines = {
        "a": exhibit_a or _ZERO,
        "b": exhibit_b or _ZERO,
        "e": exhibit_e,
        "f": exhibit_f,
    }
    total = _ZERO
    for letter in "abcdefghij":
        amount = lines.get(letter, _ZERO)
        total += amount
        values[f"exhibit.{letter}"] = _money_text(amount)
    values["exhibit.total"] = _money_text(total)

    if problems:
        raise FormProjectionError(problems)
    return values


def _liens(case_file: CaseFile) -> LienFigures:
    from ..liens import derive_liens

    return derive_liens(case_file)


def _part_2(
    release: FormRelease,
    values: FieldValues,
    calc: PlanCalculation,
    problems: list[str],
) -> None:
    runs = _payment_runs(calc.funding.schedule)
    if len(runs) > _RUNS:
        problems.append(
            f"§ 2.1: the plan's payments change {len(runs) - 1} times; Official"
            f" Form 113 prints {_RUNS} payment lines, and adding lines is a"
            " nonstandard change (Part 8), which is not prepared here"
        )
    for index, (payment, months) in enumerate(runs[:_RUNS]):
        row_fill(
            release, values, "line_2_1_amount", index, _money_text(payment), problems
        )
        row_fill(release, values, "line_2_1_per", index, Text("month"), problems)
        row_fill(release, values, "line_2_1_months", index, Text(str(months)), problems)

    lumps = calc.funding.lump_sums
    if not lumps:
        values["line_2_4_none"] = Check()
    else:
        values["line_2_4_additional"] = Check()
        described = "; ".join(
            f"{description or 'Lump sum'}: ${amount:,.2f} in month {month}"
            for month, amount, description in lumps
        )
        spec = release.field("line_2_4_description")
        box = release.boxes[spec.pdf_names[0]]
        wrapped = wrap_width(
            described,
            points=box.w,
            lines=len(spec.pdf_names),
            where="line_2_4_description",
            problems=problems,
            size=box.size,
        )
        for index, line in enumerate(wrapped):
            row_fill(
                release, values, "line_2_4_description", index, Text(line), problems
            )
    values["line_2_5_total"] = _money_text(calc.funding.total)


def _creditor_name(case_file: CaseFile, claim: ClaimBody) -> str | None:
    creditor = case_file.creditor(claim.creditor_id)
    return creditor.name if creditor is not None else None


def _section_3_1(
    release: FormRelease,
    values: FieldValues,
    case_file: CaseFile,
    plan: PlanBody,
    calc: PlanCalculation,
    liens: LienFigures,
    problems: list[str],
) -> Decimal | None:
    rows = _treated(case_file, plan, "cure_and_maintain")
    if not rows:
        values["line_3_1_none"] = Check()
        return None
    values["line_3_1_maintain"] = Check()
    if len(rows) > _ROWS:
        _too_many("3.1", len(rows), _ROWS, problems)
    total = _ZERO
    for index, (claim_id, claim, treatment) in enumerate(rows[:_ROWS]):
        lien = liens.claim(claim_id)
        _lines(
            release,
            values,
            "line_3_1_creditor",
            index,
            2,
            _creditor_name(case_file, claim),
            problems,
        )
        _lines(
            release,
            values,
            "line_3_1_collateral",
            index,
            2,
            lien.collateral_description if lien else None,
            problems,
        )
        if treatment.maintenance_payment is not None:
            row_fill(
                release,
                values,
                "line_3_1_installment",
                index,
                Text(format_money(treatment.maintenance_payment)),
                problems,
            )
            row_fill(
                release, values, "line_3_1_disbursed_trustee", index, Check(), problems
            )
        else:
            row_fill(
                release, values, "line_3_1_disbursed_debtor", index, Check(), problems
            )
        if treatment.arrearage is not None:
            row_fill(
                release,
                values,
                "line_3_1_arrearage",
                index,
                Text(format_money(treatment.arrearage)),
                problems,
            )
        if treatment.arrearage_interest_rate is not None:
            row_fill(
                release,
                values,
                "line_3_1_rate",
                index,
                Text(_rate(treatment.arrearage_interest_rate)),
                problems,
            )
        arrearage = _row(calc, f"secured:{claim_id}")
        conduit = _row(calc, f"conduit:{claim_id}")
        if arrearage is not None and arrearage.monthly_payment is not None:
            row_fill(
                release,
                values,
                "line_3_1_monthly",
                index,
                _money_text(arrearage.monthly_payment),
                problems,
            )
        paid = (arrearage.payout if arrearage else _ZERO) + (
            conduit.payout if conduit else _ZERO
        )
        row_fill(release, values, "line_3_1_total", index, _money_text(paid), problems)
        total += paid
    return total


def _section_3_2(
    release: FormRelease,
    values: FieldValues,
    case_file: CaseFile,
    plan: PlanBody,
    calc: PlanCalculation,
    liens: LienFigures,
    problems: list[str],
) -> Decimal | None:
    rows = _treated(case_file, plan, "cramdown")
    if not rows:
        values["line_3_2_none"] = Check()
        return None
    values["line_3_2_request"] = Check()
    if len(rows) > _ROWS:
        _too_many("3.2", len(rows), _ROWS, problems)
    total = _ZERO
    for index, (claim_id, claim, treatment) in enumerate(rows[:_ROWS]):
        lien = liens.claim(claim_id)
        _lines(
            release,
            values,
            "line_3_2_creditor",
            index,
            2,
            _creditor_name(case_file, claim),
            problems,
        )
        if claim.amount is not None:
            row_fill(
                release,
                values,
                "line_3_2_claim_amount",
                index,
                Text(format_money(claim.amount)),
                problems,
            )
        if lien is not None:
            _lines(
                release,
                values,
                "line_3_2_collateral",
                index,
                2,
                lien.collateral_description,
                problems,
            )
            if lien.collateral_value is not None:
                row_fill(
                    release,
                    values,
                    "line_3_2_collateral_value",
                    index,
                    _money_text(lien.collateral_value),
                    problems,
                )
            row_fill(
                release,
                values,
                "line_3_2_senior",
                index,
                _money_text(lien.senior_liens),
                problems,
            )
        if treatment.interest_rate is not None:
            row_fill(
                release,
                values,
                "line_3_2_rate",
                index,
                Text(_rate(treatment.interest_rate)),
                problems,
            )
        secured = _row(calc, f"secured:{claim_id}")
        if secured is None:
            continue
        row_fill(
            release,
            values,
            "line_3_2_secured",
            index,
            _money_text(secured.allowed),
            problems,
        )
        if secured.monthly_payment is not None:
            row_fill(
                release,
                values,
                "line_3_2_monthly",
                index,
                _money_text(secured.monthly_payment),
                problems,
            )
        row_fill(
            release,
            values,
            "line_3_2_total",
            index,
            _money_text(secured.payout),
            problems,
        )
        total += secured.payout
    return total


def _section_3_5(
    release: FormRelease,
    values: FieldValues,
    case_file: CaseFile,
    plan: PlanBody,
    liens: LienFigures,
    problems: list[str],
) -> None:
    rows = _treated(case_file, plan, "surrender")
    if not rows:
        values["line_3_5_none"] = Check()
        return
    values["line_3_5_surrender"] = Check()
    if len(rows) > _ROWS:
        _too_many("3.5", len(rows), _ROWS, problems)
    for index, (claim_id, claim, _treatment) in enumerate(rows[:_ROWS]):
        lien = liens.claim(claim_id)
        row_fill(
            release,
            values,
            "line_3_5_creditor",
            index,
            text_or_none(_creditor_name(case_file, claim)),
            problems,
        )
        row_fill(
            release,
            values,
            "line_3_5_collateral",
            index,
            text_or_none(lien.collateral_description if lien else None),
            problems,
        )


def _part_4(
    values: FieldValues,
    plan: PlanBody,
    calc: PlanCalculation,
) -> Decimal:
    """§§ 4.2 to 4.5; returns the exhibit's line e."""
    if plan.trustee_percentage is not None:
        values["line_4_2_percentage"] = Text(_rate(plan.trustee_percentage))
    trustee = _class_payout(calc, "trustee")
    values["line_4_2_total"] = _money_text(trustee)
    if plan.attorney_fees is not None:
        values["line_4_3_attorney_fees"] = Text(format_money(plan.attorney_fees))
    priority = next((s for s in calc.classes if s.key == "priority"), None)
    if priority is None or not priority.rows:
        values["line_4_4_none"] = Check()
    else:
        values["line_4_4_estimate"] = Check()
        values["line_4_4_total"] = _money_text(priority.allowed)
    values["line_4_5_none"] = Check()
    return (
        trustee + _class_payout(calc, "attorney_fees") + _class_payout(calc, "priority")
    )


def _part_5(values: FieldValues, plan: PlanBody, calc: PlanCalculation) -> Decimal:
    """§§ 5.1 to 5.3; returns the exhibit's line f."""
    treatment = plan.unsecured_treatment or "pot"
    if treatment == "amount":
        values["line_5_1_sum"] = Check()
        if plan.unsecured_amount is not None:
            values["line_5_1_sum_amount"] = Text(format_money(plan.unsecured_amount))
    elif treatment == "percentage":
        values["line_5_1_percentage"] = Check()
        if plan.unsecured_percentage is not None:
            values["line_5_1_percentage_value"] = Text(_rate(plan.unsecured_percentage))
        if calc.unsecured_target is not None:
            values["line_5_1_percentage_estimate"] = _money_text(calc.unsecured_target)
    else:
        values["line_5_1_remaining"] = Check()
    values["line_5_1_liquidation"] = _money_text(calc.liquidation.available)
    values["line_5_2_none"] = Check()
    values["line_5_3_none"] = Check()
    return _class_payout(calc, "general_unsecured")


def _part_6(
    release: FormRelease,
    values: FieldValues,
    case_file: CaseFile,
    problems: list[str],
) -> None:
    assumed = [
        body for _id, body in case_file.contract_leases if body.intention == "assume"
    ]
    if not assumed:
        values["line_6_1_none"] = Check()
        return
    values["line_6_1_assumed"] = Check()
    if len(assumed) > _ROWS:
        _too_many("6.1", len(assumed), _ROWS, problems)
    for index, lease in enumerate(assumed[:_ROWS]):
        _lines(
            release,
            values,
            "line_6_1_creditor",
            index,
            2,
            lease.counterparty_name,
            problems,
        )
        _lines(
            release,
            values,
            "line_6_1_description",
            index,
            4,
            lease.description,
            problems,
        )
        row_fill(release, values, "line_6_1_disbursed_debtor", index, Check(), problems)
