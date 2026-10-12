"""The Chapter 13 plan calculator (issue 16.2 / #366) — core/chapter13_plan.py,
as a pure function.

Each case builds a small CaseFile by hand and a `PlanInputs` whose upstream
figures (the commitment period, Schedule J's net income, each asset's
equity) are typed directly: those have their own engines and their own
tests, and this file pins only the plan's arithmetic. Amounts are chosen so
every figure can be checked by eye — the reference plan below is worked in
the comment above it.

Every identifier below is obviously fake; this repo is public.
"""

from __future__ import annotations

from dataclasses import replace
from decimal import Decimal

import pytest
from insolvia_api.core.chapter13_plan import (
    CLASS_ATTORNEY,
    CLASS_CONDUIT,
    CLASS_PRIORITY,
    CLASS_SECURED,
    CLASS_TRUSTEE,
    CLASS_UNSECURED,
    PlanInputs,
    Sourced,
    calculate_plan,
    plan_calculation_json,
    section_326a_commission,
)
from insolvia_api.core.exemption_analysis import AssetExemptions
from insolvia_api.core.form_projections import CaseFile
from insolvia_core.assets import AssetBody
from insolvia_core.cases import Case
from insolvia_core.claims import ClaimBody
from insolvia_core.creditors import CreditorBody
from insolvia_core.plans import parse_plan

CASE = Case(
    id="case-plan-0001",
    firm_id="firm-0001",
    created_by="subject-0001",
    chapter=13,
    district="Middle District of Florida",
    status="intake",
    created_at="2026-09-01T00:00:00Z",
    updated_at="2026-09-01T00:00:00Z",
)

ASSETS = (
    ("asset-house", AssetBody(description="12 Example Court", value_entire="250000")),
    ("asset-car", AssetBody(description="2019 sedan", value_entire="9000")),
    ("asset-savings", AssetBody(description="Savings", value_entire="10000")),
)
CREDITORS = (
    ("cr-mortgage", CreditorBody(name="Example Mortgage Co")),
    ("cr-auto", CreditorBody(name="Example Auto Finance")),
    ("cr-tax", CreditorBody(name="Example Tax Authority")),
    ("cr-card", CreditorBody(name="Example Card Bank")),
)
CLAIMS = (
    (
        "claim-mortgage",
        ClaimBody(
            creditor_id="cr-mortgage",
            claim_class="secured",
            amount="200000.00",
            asset_id="asset-house",
            lien_position=1,
        ),
    ),
    (
        "claim-auto",
        ClaimBody(
            creditor_id="cr-auto",
            claim_class="secured",
            amount="12000.00",
            asset_id="asset-car",
            lien_position=1,
        ),
    ),
    (
        "claim-tax",
        ClaimBody(
            creditor_id="cr-tax",
            claim_class="priority_unsecured",
            priority_amount="3000.00",
            nonpriority_amount="1000.00",
        ),
    ),
    (
        "claim-card",
        ClaimBody(
            creditor_id="cr-card",
            claim_class="nonpriority_unsecured",
            amount="20000.00",
        ),
    ),
)

CASE_FILE = CaseFile(case=CASE, assets=ASSETS, creditors=CREDITORS, claims=CLAIMS)


def equity(asset_id: str, value: str, liens: str, exempt: str) -> AssetExemptions:
    """One asset as the exemption workbench reports it."""
    net = max(Decimal(value) - Decimal(liens), Decimal("0"))
    return AssetExemptions(
        asset_id=asset_id,
        description=asset_id,
        category=None,
        current_value=Decimal(value),
        liens=Decimal(liens),
        net_equity=net,
        claimed=Decimal(exempt),
        unexempt=max(net - Decimal(exempt), Decimal("0")),
        homestead_cap=None,
        cap_applied=False,
        claims=(),
        suggestions=(),
    )


INPUTS = PlanInputs(
    case_file=CASE_FILE,
    commitment_months=60,
    commitment_source="line 20b is more than or equal to line 20c",
    schedule_j_excess=Sourced(Decimal("700.00"), "Schedule J line 23c"),
    assets=(
        equity("asset-house", "250000", "200000", "50000"),
        equity("asset-car", "9000", "12000", "0"),
        equity("asset-savings", "10000", "0", "2000"),
    ),
)

# The reference plan, by hand. 60 months at $600: $36,000 in.
#   trustee 10% of each receipt ........................ $3,600
#   auto cramdown to $9,000 at 0%, $150 a month ....... $9,000
#   mortgage arrearage $6,000 at 0%, amortised: $100 ... $6,000
#   attorney's fees .................................... $3,000
#   priority (the tax claim's $3,000) .................. $3,000
#   general unsecured, what remains .................... $11,400
# The unsecured pool: the card's $20,000, the tax claim's $1,000 nonpriority
# part and the auto claim's $3,000 over the cramdown value — $24,000, so the
# plan pays 47.50%.
#
# The liquidation: $8,000 unexempt (the savings), less the § 326(a)
# commission ($1,250 + $300), less the $3,000 priority claim = $3,450 over
# the Chapter 7 pool (the same $24,000 here — the house has no deficiency) =
# 14.38%. The plan clears it.
REFERENCE_PLAN = {
    "term_months": 60,
    "payment_source": "fixed",
    "monthly_payment": "600",
    "trustee_percentage": "10",
    "attorney_fees": "3000",
    "secured_treatments": [
        {
            "id": "t-auto",
            "claim_id": "claim-auto",
            "treatment": "cramdown",
            "cramdown_value": "9000",
            "interest_rate": "0",
        },
        {
            "id": "t-home",
            "claim_id": "claim-mortgage",
            "treatment": "cure_and_maintain",
            "arrearage": "6000",
        },
    ],
    "unsecured_treatment": "pot",
}


def run(overrides: dict[str, object] | None = None, inputs: PlanInputs = INPUTS):
    return calculate_plan(inputs, parse_plan({**REFERENCE_PLAN, **(overrides or {})}))


def by_class(calc, key):
    return next(summary for summary in calc.classes if summary.key == key)


# ── the reference plan ───────────────────────────────────────────


def test_the_reference_plan_funds_every_class_in_order():
    calc = run()

    assert calc.funding.total == Decimal("36000.00")
    assert by_class(calc, CLASS_TRUSTEE).principal == Decimal("3600.00")
    assert by_class(calc, CLASS_SECURED).principal == Decimal("15000.00")
    assert by_class(calc, CLASS_ATTORNEY).principal == Decimal("3000.00")
    assert by_class(calc, CLASS_PRIORITY).principal == Decimal("3000.00")
    assert by_class(calc, CLASS_UNSECURED).principal == Decimal("11400.00")
    assert calc.feasibility.surplus == Decimal("0.00")


def test_the_reference_plan_is_feasible_and_clears_the_liquidation_floor():
    calc = run()

    assert calc.feasibility.feasible is True
    assert calc.unsecured_pool_total == Decimal("24000.00")
    assert calc.unsecured_percentage == Decimal("47.50")
    assert calc.liquidation.unexempt_total == Decimal("8000.00")
    assert calc.liquidation.trustee_commission == Decimal("1550.00")
    assert calc.liquidation.available == Decimal("3450.00")
    assert calc.liquidation.percentage == Decimal("14.38")
    assert calc.best_interests.passes is True


def test_every_row_names_its_claim_and_its_source():
    calc = run()

    rows = {row.claim_id: row for row in by_class(calc, CLASS_SECURED).rows}
    auto = rows["claim-auto"]
    assert "plan.secured_treatments[t-auto].cramdown_value" in auto.source
    assert auto.label.startswith("Example Auto Finance")
    pool_sources = {claim.claim_id: claim.source for claim in calc.unsecured_pool}
    assert "cramdown_value" in pool_sources["claim-auto"]
    assert "nonpriority part" in pool_sources["claim-tax"]


def test_the_attorney_is_paid_before_priority_claims():
    calc = run()

    attorney = by_class(calc, CLASS_ATTORNEY)
    priority = by_class(calc, CLASS_PRIORITY)
    # $290 a month is left after the trustee and the secured payments: the
    # fee takes months 1-11 and priority starts in month 11 with the rest.
    assert (attorney.first_month, attorney.last_month) == (1, 11)
    assert priority.first_month == 11


def test_the_json_carries_money_as_strings():
    body = plan_calculation_json(run())

    assert body["unsecured"]["percentage"] == "47.50"
    assert body["feasibility"]["feasible"] is True
    assert body["bestInterests"]["passes"] is True
    assert body["liquidation"]["trusteeCommission"] == "1550.00"
    assert [c["key"] for c in body["classes"]] == [
        CLASS_TRUSTEE,
        CLASS_SECURED,
        CLASS_ATTORNEY,
        CLASS_PRIORITY,
        CLASS_UNSECURED,
    ]


# ── feasibility ──────────────────────────────────────────────────


def test_a_payment_too_small_for_the_waterfall_is_not_feasible():
    calc = run({"monthly_payment": "280"})

    assert calc.feasibility.feasible is False
    assert calc.feasibility.shortfall > 0
    assert any("unpaid after month 60" in r for r in calc.feasibility.reasons)


def test_a_promised_percentage_the_payment_cannot_meet_is_not_feasible():
    calc = run({"unsecured_treatment": "percentage", "unsecured_percentage": "60"})

    assert calc.unsecured_target == Decimal("14400.00")
    assert calc.feasibility.feasible is False


def test_a_promised_amount_the_payment_covers_is_feasible_with_a_surplus():
    calc = run({"unsecured_treatment": "amount", "unsecured_amount": "10000"})

    assert calc.feasibility.feasible is True
    assert calc.feasibility.surplus == Decimal("1400.00")


def test_a_payment_above_schedule_j_is_flagged():
    calc = run({"monthly_payment": "800"})

    assert calc.feasibility.exceeds_schedule_j is True
    assert any("§ 1325(a)(6)" in w for w in calc.warnings)


def test_a_missing_trustee_percentage_leaves_feasibility_undetermined():
    plan = {**REFERENCE_PLAN}
    del plan["trustee_percentage"]

    calc = calculate_plan(INPUTS, parse_plan(plan))

    assert calc.feasibility.feasible is None
    assert calc.best_interests.passes is None
    assert any("trustee's percentage" in p for p in calc.problems)


# ── funding ──────────────────────────────────────────────────────


def test_the_term_defaults_to_the_commitment_period():
    plan = {**REFERENCE_PLAN}
    del plan["term_months"]

    calc = calculate_plan(replace(INPUTS, commitment_months=36), parse_plan(plan))

    assert calc.funding.term_months == 36
    assert "B122C-1 line 21" in (calc.funding.term_source or "")


def test_without_a_term_or_a_means_test_there_is_no_waterfall():
    plan = {**REFERENCE_PLAN}
    del plan["term_months"]

    calc = calculate_plan(replace(INPUTS, commitment_months=None), parse_plan(plan))

    assert calc.funding.schedule == ()
    assert calc.feasibility.feasible is None
    assert any("no term" in p for p in calc.problems)


def test_steps_and_lump_sums_change_the_funding():
    calc = run(
        {
            "step_payments": [
                {"id": "s1", "start_month": 31, "monthly_payment": "700"}
            ],
            "lump_sums": [{"id": "l1", "month": 12, "amount": "1500"}],
        }
    )

    schedule = dict(calc.funding.schedule)
    assert (schedule[30], schedule[31], schedule[60]) == (
        Decimal("600.00"),
        Decimal("700.00"),
        Decimal("700.00"),
    )
    assert calc.funding.total == Decimal("600") * 30 + Decimal("700") * 30 + 1500


def test_the_schedule_j_source_uses_line_23c():
    calc = run({"payment_source": "schedule_j_excess", "monthly_payment": None})

    assert calc.funding.base_payment == Decimal("700.00")
    assert calc.funding.base_source == "Schedule J line 23c"


def test_the_disposable_income_source_needs_the_means_test():
    calc = run({"payment_source": "disposable_income", "monthly_payment": None})

    assert calc.funding.base_payment is None
    assert any("B122C-2" in p for p in calc.problems)


def test_the_disposable_income_source_is_the_engines_figure():
    inputs = replace(
        INPUTS, disposable_income=Sourced(Decimal("612.40"), "B122C-2 line 45")
    )

    calc = run({"payment_source": "disposable_income", "monthly_payment": None}, inputs)

    assert calc.funding.base_payment == Decimal("612.40")
    assert calc.funding.base_source == "B122C-2 line 45"


# ── treatments ───────────────────────────────────────────────────


def test_a_cramdown_at_a_rate_pays_the_value_plus_interest():
    calc = run(
        {
            "monthly_payment": "900",
            "secured_treatments": [
                {
                    "id": "t-auto",
                    "claim_id": "claim-auto",
                    "treatment": "cramdown",
                    "cramdown_value": "9000",
                    "interest_rate": "6",
                }
            ],
        }
    )

    auto = by_class(calc, CLASS_SECURED).rows[0]
    # $9,000 at 0.5% a month over 60 months amortises to $173.99…, rounded
    # up so the last payment clears it.
    assert auto.monthly_payment == Decimal("174.00")
    assert auto.principal == Decimal("9000.00")
    assert Decimal("1430") < auto.interest < Decimal("1440")
    assert auto.unpaid == Decimal("0.00")


def test_a_cramdown_without_a_value_uses_the_derived_secured_portion():
    calc = run(
        {
            "secured_treatments": [
                {
                    "id": "t-auto",
                    "claim_id": "claim-auto",
                    "treatment": "cramdown",
                    "interest_rate": "0",
                }
            ]
        }
    )

    auto = by_class(calc, CLASS_SECURED).rows[0]
    assert auto.allowed == Decimal("9000.00")
    assert "bifurcated" in next(
        c.source for c in calc.unsecured_pool if c.claim_id == "claim-auto"
    )


def test_a_surrendered_claims_deficiency_joins_the_unsecured_pool():
    calc = run(
        {
            "secured_treatments": [
                {"id": "t-auto", "claim_id": "claim-auto", "treatment": "surrender"}
            ]
        }
    )

    assert all(row.claim_id != "claim-auto" for s in calc.classes for row in s.rows)
    assert any(
        c.claim_id == "claim-auto" and c.amount == Decimal("3000.00")
        for c in calc.unsecured_pool
    )


def test_a_conduit_payment_is_paid_every_month_of_the_term():
    calc = run(
        {
            "monthly_payment": "2200",
            "secured_treatments": [
                {
                    "id": "t-home",
                    "claim_id": "claim-mortgage",
                    "treatment": "cure_and_maintain",
                    "arrearage": "6000",
                    "maintenance_payment": "1500",
                }
            ],
        }
    )

    conduit = by_class(calc, CLASS_CONDUIT).rows[0]
    assert conduit.principal == Decimal("90000.00")
    assert (conduit.first_month, conduit.last_month) == (1, 60)


def test_an_untreated_secured_claim_is_warned_about_not_paid():
    calc = run({"secured_treatments": []})

    assert any(
        "Example Auto Finance" in w and "§ 1325(a)(5)" in w for w in calc.warnings
    )
    assert CLASS_SECURED not in {s.key for s in calc.classes}


def test_a_treatment_naming_a_non_secured_claim_is_a_problem():
    calc = run(
        {
            "secured_treatments": [
                {"id": "t-x", "claim_id": "claim-card", "treatment": "surrender"}
            ]
        }
    )

    assert any("not a secured claim" in p for p in calc.problems)


def test_a_reduced_priority_percentage_pays_less():
    calc = run({"priority_percentage": "50"})

    assert by_class(calc, CLASS_PRIORITY).principal == Decimal("1500.00")


# ── the liquidation test ─────────────────────────────────────────


@pytest.mark.parametrize(
    ("disbursed", "commission"),
    [
        ("0", "0.00"),
        ("4000", "1000.00"),
        ("5000", "1250.00"),
        ("50000", "5750.00"),
        ("1000000", "53250.00"),
        ("1100000", "56250.00"),
    ],
)
def test_the_section_326a_commission_follows_the_statutes_bands(disbursed, commission):
    assert section_326a_commission(Decimal(disbursed)) == Decimal(commission)


def test_other_chapter_7_costs_lower_the_floor():
    calc = run({"chapter_7_other_costs": "1000"})

    assert calc.liquidation.available == Decimal("2450.00")


def test_a_plan_below_the_floor_fails_the_best_interests_test():
    inputs = replace(
        INPUTS,
        assets=(
            *INPUTS.assets[:2],
            equity("asset-savings", "40000", "0", "2000"),
        ),
    )

    calc = run(None, inputs)

    # $38,000 unexempt, less $1,250 + $3,300 commission, less $3,000
    # priority = $30,450 of $24,000 — the pool would be paid in full.
    assert calc.liquidation.percentage == Decimal("100")
    assert calc.best_interests.passes is False


def test_a_present_value_rate_discounts_the_plans_unsecured_payments():
    nominal = run()
    discounted = run({"present_value_rate": "5"})

    assert (
        discounted.best_interests.plan_percentage
        < nominal.best_interests.plan_percentage
    )
    assert "present_value_rate" in discounted.best_interests.rule


def test_with_no_plan_the_liquidation_floor_is_still_reported():
    calc = calculate_plan(INPUTS, None)

    assert calc.plan_present is False
    assert calc.liquidation.percentage == Decimal("14.38")
    assert calc.feasibility.feasible is None
    assert any("no plan yet" in p for p in calc.problems)


def test_a_chapter_7_case_is_warned():
    inputs = replace(
        INPUTS, case_file=replace(CASE_FILE, case=replace(CASE, chapter=7))
    )

    calc = run(None, inputs)

    assert any("Chapter 7 case" in w for w in calc.warnings)
