"""The Chapter 13 plan calculator (issue 16.2 / #366): the payment
waterfall, feasibility, and the § 1325(a)(4) liquidation test — one pure
function over the case's records and one plan proposal.

WHAT IT REUSES, AND WHY IT RECOMPUTES NOTHING. Every figure a plan depends
on already has an owner in this service, and a second derivation would
agree with the first only until somebody edited a record:

- the applicable commitment period (B122C-1 line 21) and the monthly
  disposable income (B122C-2 line 45) are the means-test engine's
  (`means_test_trace.trace_means_test`, issue #365) — the plan's term
  defaults to the first and the `disposable_income` payment source IS the
  second;
- Schedule J's monthly net income (line 23c) is 106I line 12 less 106J line
  22c, through the same two helpers the Schedule J projection prints from;
- each secured claim's secured and unsecured portions are `core/liens.py`'s,
  the figures B106D prints;
- each asset's value, liens, exemptions and unexempt equity are the Schedule
  C workbench's (`core/exemption_analysis.analyse`, issue #346).

The route loads those once and hands them over as `PlanInputs`; this module
adds only the arithmetic that is the plan's own.

THE WATERFALL, month by month over the term. Each month's receipts are the
payment in force (the base, replaced from each step's start month) plus any
lump sum that month. From them, in this order:

    1. the trustee's percentage fee on the receipts (28 U.S.C. § 586(e));
    2. ongoing contract payments paid through the plan (a conduit), each
       cure-and-maintain claim's `maintenance_payment`;
    3. the secured claims' equal monthly payments (§ 1325(a)(5)(B)(iii)) —
       each arrearage and each cramdown balance, with interest accruing
       monthly on the unpaid balance at its rate; the payment is the typed
       one or, absent, the balance amortised over the term;
    4. the attorney's fees (an administrative expense, § 507(a)(2)), up to
       the monthly cap where one is typed;
    5. the priority claims (§ 1322(a)(2)), pro rata, to the plan's
       percentage of each with any interest;
    6. the general unsecured class, pro rata, to its target — everything
       (`pot`), a promised percentage, or a promised amount.

Whatever a tier cannot be paid in full in a month is shared pro rata within
the tier; whatever is left after the unsecured target is a surplus. That
order is the calculator's default, stated here rather than assumed: Official
Form 113 leaves the order of distribution to the plan (Part 7's nonstandard
provisions), and the default is the conventional one. A plan that needs
another order is a nonstandard provision, out of this issue's scope.

FEASIBILITY: the payments over the term cover the waterfall — every tier
above the unsecured class paid in full by the last month, and the unsecured
class's promise met where the plan makes one. Separately, a base payment
above Schedule J's net income is flagged: § 1325(a)(6) asks whether the
debtor can make the payments at all.

THE LIQUIDATION TEST (§ 1325(a)(4)), from Schedules A/B, C, D and E/F:

    property      = each asset's current value (the value 106C prints)
    - liens       = what the secured claims take of it (value - net equity)
    - exemptions  = what the claimed exemptions protect of the equity
    = unexempt    = what a Chapter 7 trustee could sell
    - the trustee's § 326(a) commission on it (25% of the first $5,000, 10%
      to $50,000, 5% to $1,000,000, 3% above — a statutory schedule § 104
      does not adjust, so it is written here with its citation rather than
      kept in the dollar-amounts registry)
    - other Chapter 7 costs (typed on the plan: a sale's costs)
    - priority claims (paid ahead of unsecured under § 726(a)(1))
    = available to general unsecured creditors in a liquidation,

as a percentage of the Chapter 7 unsecured pool (every nonpriority claim,
every priority claim's nonpriority part, and every secured claim's
unsecured portion). The plan must pay general unsecured creditors at least
that percentage — measured at present value where the plan types a
discount rate, nominally otherwise (and the result says which).

EVERY FIGURE CARRIES ITS SOURCE — a claim id, a plan field, or a rule — so
the screen can show where each number came from, and nothing is a guess:
an input the arithmetic needs and the case does not hold is a `problem`,
and the tests that depend on it answer `null` rather than pass or fail.

SCENARIOS are the same function over a different `PlanBody`. The route
parses an unsaved proposal the way it would parse a stored one and runs it
against the same case — comparing alternatives writes nothing, and the one
the preparer adopts reaches the case through the ordinary confirmed write.

Money is `Decimal` throughout, rounded to the cent at every monthly step,
and two-place strings on the wire.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from datetime import date
from decimal import ROUND_DOWN, ROUND_HALF_UP, ROUND_UP, Decimal
from typing import Final

from insolvia_core.claims import ClaimBody
from insolvia_core.plans import PlanBody, SecuredTreatment

from .exemption_analysis import AssetExemptions, ExemptionCase, analyse
from .form_projections.b106i import monthly_income_line_12
from .form_projections.b106j import monthly_expenses_line_22c
from .form_projections.shared import CaseFile
from .liens import LienFigures, derive_liens
from .means_test_trace import trace_means_test

_ZERO: Final = Decimal("0.00")
_CENT: Final = Decimal("0.01")
_HUNDRED: Final = Decimal("100")
_TWELVE: Final = Decimal("12")

# 11 U.S.C. § 326(a): the Chapter 7 trustee's maximum compensation, as
# marginal rates on the moneys disbursed. `None` is "and above".
SECTION_326A_TIERS: Final[tuple[tuple[Decimal | None, Decimal], ...]] = (
    (Decimal("5000"), Decimal("0.25")),
    (Decimal("50000"), Decimal("0.10")),
    (Decimal("1000000"), Decimal("0.05")),
    (None, Decimal("0.03")),
)
SECTION_326A_RULE: Final = (
    "11 U.S.C. § 326(a): 25% of the first $5,000, 10% of the next $45,000, "
    "5% of the next $950,000, 3% above $1,000,000"
)

# The waterfall's classes, in distribution order.
CLASS_TRUSTEE: Final = "trustee"
CLASS_CONDUIT: Final = "ongoing_payments"
CLASS_SECURED: Final = "secured"
CLASS_ATTORNEY: Final = "attorney_fees"
CLASS_PRIORITY: Final = "priority"
CLASS_UNSECURED: Final = "general_unsecured"

_CLASS_LABELS: Final = {
    CLASS_TRUSTEE: "Trustee's fee",
    CLASS_CONDUIT: "Ongoing payments through the plan",
    CLASS_SECURED: "Secured claims and arrears",
    CLASS_ATTORNEY: "Attorney's fees",
    CLASS_PRIORITY: "Priority claims",
    CLASS_UNSECURED: "General unsecured claims",
}


def _money(value: Decimal) -> Decimal:
    return value.quantize(_CENT, rounding=ROUND_HALF_UP)


def _dec(value: str | None) -> Decimal | None:
    return Decimal(value) if value is not None else None


def _monthly_rate(annual_percent: Decimal | None) -> Decimal:
    if annual_percent is None:
        return Decimal("0")
    return annual_percent / _HUNDRED / _TWELVE


def _pct(part: Decimal, whole: Decimal) -> Decimal | None:
    if whole <= 0:
        return None
    return (part / whole * _HUNDRED).quantize(_CENT, rounding=ROUND_HALF_UP)


# ── the inputs ─────────────────────────────────────────────────────────


@dataclass(frozen=True)
class Sourced:
    """A figure and where it came from."""

    amount: Decimal
    source: str


@dataclass(frozen=True)
class PlanInputs:
    """What the plan reads besides the proposal: the case file, and the
    figures other engines own, already computed (the route composes them;
    the tests build them by hand)."""

    case_file: CaseFile
    commitment_months: int | None = None
    commitment_source: str | None = None
    disposable_income: Sourced | None = None
    schedule_j_excess: Sourced | None = None
    assets: tuple[AssetExemptions, ...] = ()
    #: Why the figures above could not be had — the means test's and the
    #: exemption analysis's own problems, carried through.
    upstream_problems: tuple[str, ...] = ()


def build_plan_inputs(
    case_file: CaseFile, exemptions: ExemptionCase, *, today: date
) -> PlanInputs:
    """Compose the figures other engines own — the means test's commitment
    period and disposable income, Schedule J's net income, the exemption
    workbench's per-asset equity — by calling them, never by redoing them.
    `today` is the exemption registry's floating resolution date, an
    argument for the reason `exemption_analysis.analyse` gives."""
    trace = trace_means_test(case_file)
    result = trace.result
    commitment = result.commitment if result is not None else None
    disposable: Sourced | None = None
    if result is not None and result.disposable_income is not None:
        disposable = Sourced(
            Decimal(result.disposable_income),
            "the means test's monthly disposable income, B122C-2 line 45 "
            "(§ 1325(b)(2))",
        )
    income = monthly_income_line_12(case_file)
    expenses = monthly_expenses_line_22c(case_file)
    excess = Sourced(
        _money(income - expenses),
        f"Schedule J line 23c: 106I line 12 (${income:,.2f}) less 106J line "
        f"22c (${expenses:,.2f})",
    )
    analysis = analyse(exemptions, today=today)
    return PlanInputs(
        case_file=case_file,
        commitment_months=commitment.months if commitment is not None else None,
        commitment_source=commitment.source if commitment is not None else None,
        disposable_income=disposable,
        schedule_j_excess=excess,
        assets=analysis.assets,
        upstream_problems=tuple(
            f"Exemptions (for the liquidation test): {problem}"
            for problem in analysis.problems
        ),
    )


# ── the result ─────────────────────────────────────────────────────────


@dataclass(frozen=True)
class PayoutRow:
    """One payee's line of the waterfall: a claim, the trustee, the
    attorney. `allowed` is what the plan owes it (before interest);
    `principal` and `interest` what the term actually pays; months are
    1-based, `None` when nothing was paid."""

    key: str
    label: str
    claim_id: str | None
    treatment: str | None
    allowed: Decimal
    monthly_payment: Decimal | None
    rate: Decimal | None
    principal: Decimal
    interest: Decimal
    first_month: int | None
    last_month: int | None
    unpaid: Decimal
    source: str

    @property
    def payout(self) -> Decimal:
        return self.principal + self.interest

    @property
    def months_paid(self) -> int:
        if self.first_month is None or self.last_month is None:
            return 0
        return self.last_month - self.first_month + 1


@dataclass(frozen=True)
class ClassSummary:
    key: str
    label: str
    rows: tuple[PayoutRow, ...]

    @property
    def allowed(self) -> Decimal:
        return sum((r.allowed for r in self.rows), _ZERO)

    @property
    def principal(self) -> Decimal:
        return sum((r.principal for r in self.rows), _ZERO)

    @property
    def interest(self) -> Decimal:
        return sum((r.interest for r in self.rows), _ZERO)

    @property
    def unpaid(self) -> Decimal:
        return sum((r.unpaid for r in self.rows), _ZERO)

    @property
    def first_month(self) -> int | None:
        months = [r.first_month for r in self.rows if r.first_month is not None]
        return min(months) if months else None

    @property
    def last_month(self) -> int | None:
        months = [r.last_month for r in self.rows if r.last_month is not None]
        return max(months) if months else None


@dataclass(frozen=True)
class PoolClaim:
    """One claim's part of an unsecured pool, and why it is there."""

    claim_id: str
    label: str
    amount: Decimal
    source: str


@dataclass(frozen=True)
class Funding:
    term_months: int | None
    term_source: str | None
    base_payment: Decimal | None
    base_source: str | None
    schedule: tuple[tuple[int, Decimal], ...]
    lump_sums: tuple[tuple[int, Decimal, str | None], ...]
    total: Decimal


@dataclass(frozen=True)
class Feasibility:
    #: None while an input the waterfall needs is missing.
    feasible: bool | None
    total_funding: Decimal
    total_distributed: Decimal
    surplus: Decimal
    shortfall: Decimal
    reasons: tuple[str, ...]
    schedule_j_excess: Sourced | None
    exceeds_schedule_j: bool | None


@dataclass(frozen=True)
class LiquidationAsset:
    asset_id: str
    description: str | None
    value: Decimal
    liens: Decimal
    exempt: Decimal
    unexempt: Decimal


@dataclass(frozen=True)
class Liquidation:
    assets: tuple[LiquidationAsset, ...]
    property_total: Decimal
    liens_total: Decimal
    exemptions_total: Decimal
    unexempt_total: Decimal
    trustee_commission: Decimal
    trustee_commission_rule: str
    other_costs: Decimal
    other_costs_source: str
    priority_total: Decimal
    available: Decimal
    pool: tuple[PoolClaim, ...]
    pool_total: Decimal
    #: The percentage the plan must meet or beat; None with an empty pool.
    percentage: Decimal | None


@dataclass(frozen=True)
class BestInterests:
    plan_percentage: Decimal | None
    liquidation_percentage: Decimal | None
    #: What the plan pays general unsecured creditors, discounted to the
    #: effective date where a rate is typed, nominal otherwise.
    plan_unsecured_value: Decimal
    present_value_rate: Decimal | None
    passes: bool | None
    rule: str


@dataclass(frozen=True)
class PlanCalculation:
    plan_present: bool
    chapter: int
    commitment_months: int | None
    commitment_source: str | None
    funding: Funding
    trustee_percentage: Decimal | None
    classes: tuple[ClassSummary, ...]
    unsecured_pool: tuple[PoolClaim, ...]
    unsecured_pool_total: Decimal
    unsecured_target: Decimal | None
    unsecured_target_source: str | None
    unsecured_percentage: Decimal | None
    feasibility: Feasibility
    liquidation: Liquidation
    best_interests: BestInterests
    warnings: tuple[str, ...]
    problems: tuple[str, ...]


# ── the claims, read ───────────────────────────────────────────────────


def _claim_label(case_file: CaseFile, claim_id: str, claim: ClaimBody) -> str:
    creditor = case_file.creditor(claim.creditor_id)
    name = creditor.name if creditor is not None and creditor.name else None
    tail = f" (…{claim.account_last4})" if claim.account_last4 else ""
    return f"{name or 'Unnamed creditor'}{tail}"


def _claims(case_file: CaseFile, claim_class: str) -> list[tuple[str, ClaimBody]]:
    return [(i, c) for i, c in case_file.claims if c.claim_class == claim_class]


def _priority_portion(claim: ClaimBody) -> Decimal:
    """A priority claim's priority part: the typed priority amount, else the
    whole amount (a claim typed before the split was entered)."""
    if claim.priority_amount is not None:
        return Decimal(claim.priority_amount)
    return Decimal(claim.amount) if claim.amount is not None else _ZERO


def _chapter_7_pool(
    case_file: CaseFile, liens: LienFigures
) -> tuple[list[PoolClaim], list[str]]:
    """Every unsecured dollar a Chapter 7 distribution would share: the
    nonpriority claims, the nonpriority part of the priority claims, and the
    unsecured portion of EVERY secured claim (a deficiency in liquidation)."""
    pool: list[PoolClaim] = []
    warnings: list[str] = []
    for claim_id, claim in _claims(case_file, "nonpriority_unsecured"):
        if claim.amount is None:
            continue
        pool.append(
            PoolClaim(
                claim_id,
                _claim_label(case_file, claim_id, claim),
                _money(Decimal(claim.amount)),
                f"claim {claim_id}: nonpriority unsecured amount",
            )
        )
    for claim_id, claim in _claims(case_file, "priority_unsecured"):
        if claim.nonpriority_amount is None or Decimal(claim.nonpriority_amount) == 0:
            continue
        pool.append(
            PoolClaim(
                claim_id,
                _claim_label(case_file, claim_id, claim),
                _money(Decimal(claim.nonpriority_amount)),
                f"claim {claim_id}: nonpriority part of a priority claim",
            )
        )
    for lien in liens.claims:
        claim = next(c for i, c in case_file.claims if i == lien.claim_id)
        if lien.unsecured_amount is None:
            warnings.append(
                f"{_claim_label(case_file, lien.claim_id, claim)}: the unsecured "
                "portion cannot be derived yet (no amount or no collateral "
                "value), so it is left out of the unsecured pool."
            )
            continue
        if lien.unsecured_amount == 0:
            continue
        pool.append(
            PoolClaim(
                lien.claim_id,
                _claim_label(case_file, lien.claim_id, claim),
                lien.unsecured_amount,
                f"claim {lien.claim_id}: unsecured portion of a secured claim "
                f"({lien.unsecured_source})",
            )
        )
    return pool, warnings


# ── the funding ────────────────────────────────────────────────────────


def _term(
    plan: PlanBody | None, inputs: PlanInputs, problems: list[str]
) -> tuple[int | None, str | None]:
    if plan is not None and plan.term_months is not None:
        return plan.term_months, "plan.term_months"
    if inputs.commitment_months is not None:
        return (
            inputs.commitment_months,
            "the applicable commitment period, B122C-1 line 21"
            + (f" — {inputs.commitment_source}" if inputs.commitment_source else ""),
        )
    problems.append(
        "The plan has no term: type one, or complete the means test so the "
        "commitment period (B122C-1 line 21) can supply it."
    )
    return None, None


def _base_payment(
    plan: PlanBody | None, inputs: PlanInputs, problems: list[str]
) -> tuple[Decimal | None, str | None]:
    if plan is None:
        problems.append("There is no plan yet: choose a monthly payment.")
        return None, None
    source = plan.payment_source
    if source is None and plan.monthly_payment is not None:
        source = "fixed"
    if source == "fixed":
        if plan.monthly_payment is None:
            problems.append("Type the plan's monthly payment.")
            return None, None
        return _money(Decimal(plan.monthly_payment)), "plan.monthly_payment"
    if source == "schedule_j_excess":
        excess = inputs.schedule_j_excess
        if excess is None or excess.amount <= 0:
            problems.append(
                "Schedule J shows no monthly net income (line 23c) to fund the "
                "plan from — complete Schedules I and J, or type a payment."
            )
            return None, None
        return _money(excess.amount), excess.source
    if source == "disposable_income":
        income = inputs.disposable_income
        if income is None:
            problems.append(
                "The means test has no § 1325(b)(2) disposable income for this "
                "case (B122C-2 is filed only above the median, and the test "
                "must be complete) — fund the plan from Schedule J or type a "
                "payment."
            )
            return None, None
        if income.amount <= 0:
            problems.append(
                "The means test's disposable income (B122C-2 line 45) is "
                "zero or less, so it cannot fund the plan."
            )
            return None, None
        return _money(income.amount), income.source
    problems.append(
        "Choose where the plan payment comes from: a typed amount, Schedule "
        "J's net income, or the means test's disposable income."
    )
    return None, None


def _funding(
    plan: PlanBody | None, inputs: PlanInputs, problems: list[str], warnings: list[str]
) -> Funding:
    term, term_source = _term(plan, inputs, problems)
    base, base_source = _base_payment(plan, inputs, problems)
    schedule: list[tuple[int, Decimal]] = []
    lumps: list[tuple[int, Decimal, str | None]] = []
    total = _ZERO
    if term is not None and base is not None and plan is not None:
        steps = sorted(
            (s for s in plan.step_payments if s.start_month is not None),
            key=lambda s: s.start_month or 0,
        )
        for step in steps:
            assert step.start_month is not None
            if step.start_month > term:
                warnings.append(
                    f"A step payment starts in month {step.start_month}, after "
                    f"the plan's {term}-month term, and is ignored."
                )
            if step.monthly_payment is None:
                problems.append(
                    f"The step starting in month {step.start_month} has no "
                    "monthly payment."
                )
        for month in range(1, term + 1):
            payment = base
            for step in steps:
                if (
                    step.start_month is not None
                    and step.start_month <= month
                    and step.monthly_payment is not None
                ):
                    payment = _money(Decimal(step.monthly_payment))
            schedule.append((month, payment))
            total += payment
        for lump in plan.lump_sums:
            if lump.month is None or lump.amount is None:
                continue
            if lump.month > term:
                warnings.append(
                    f"A lump sum in month {lump.month} falls after the plan's "
                    f"{term}-month term and is ignored."
                )
                continue
            value = _money(Decimal(lump.amount))
            lumps.append((lump.month, value, lump.description))
            total += value
    return Funding(
        term_months=term,
        term_source=term_source,
        base_payment=base,
        base_source=base_source,
        schedule=tuple(schedule),
        lump_sums=tuple(sorted(lumps, key=lambda lump: lump[0])),
        total=_money(total),
    )


# ── the waterfall ──────────────────────────────────────────────────────


@dataclass
class _Payee:
    """A payee's running state during the simulation."""

    key: str
    label: str
    claim_id: str | None
    treatment: str | None
    allowed: Decimal
    balance: Decimal
    monthly_rate: Decimal
    rate: Decimal | None
    #: The fixed monthly amount due (conduit, EMP, capped fee), or None for
    #: "whatever is available up to the balance".
    monthly_payment: Decimal | None
    source: str
    #: A conduit payment is owed every month, not against a balance.
    recurring: bool = False
    principal: Decimal = _ZERO
    interest: Decimal = _ZERO
    first_month: int | None = None
    last_month: int | None = None
    paid_by_month: dict[int, Decimal] = field(default_factory=dict)
    #: Interest accrued and not yet paid — a payment retires it first.
    accrued_unpaid: Decimal = _ZERO

    def accrue(self) -> None:
        if self.recurring or self.balance <= 0 or self.monthly_rate == 0:
            return
        accrued = _money(self.balance * self.monthly_rate)
        self.balance += accrued
        self.accrued_unpaid += accrued

    def due(self) -> Decimal:
        if self.recurring:
            return self.monthly_payment or _ZERO
        if self.balance <= 0:
            return _ZERO
        if self.monthly_payment is None:
            return self.balance
        return min(self.monthly_payment, self.balance)

    def pay(self, month: int, amount: Decimal) -> None:
        if amount <= 0:
            return
        if not self.recurring:
            self.balance -= amount
            to_interest = min(amount, self.accrued_unpaid)
            self.accrued_unpaid -= to_interest
            self.interest += to_interest
            self.principal += amount - to_interest
        else:
            self.principal += amount
        if self.first_month is None:
            self.first_month = month
        self.last_month = month
        self.paid_by_month[month] = self.paid_by_month.get(month, _ZERO) + amount

    def row(self, term: int) -> PayoutRow:
        if self.recurring:
            owed = (self.monthly_payment or _ZERO) * term
            unpaid = _money(max(owed - self.principal, _ZERO))
        else:
            unpaid = _money(max(self.balance, _ZERO))
        return PayoutRow(
            key=self.key,
            label=self.label,
            claim_id=self.claim_id,
            treatment=self.treatment,
            allowed=_money(self.allowed),
            monthly_payment=self.monthly_payment,
            rate=self.rate,
            principal=_money(self.principal),
            interest=_money(self.interest),
            first_month=self.first_month,
            last_month=self.last_month,
            unpaid=unpaid,
            source=self.source,
        )


def _amortised(principal: Decimal, monthly_rate: Decimal, months: int) -> Decimal:
    """The level monthly payment that retires `principal` over `months` at
    `monthly_rate`, rounded UP to the cent so the last payment clears it."""
    if months <= 0:
        return principal
    if monthly_rate == 0:
        return (principal / months).quantize(_CENT, rounding=ROUND_UP)
    factor = (1 + monthly_rate) ** months
    payment = principal * monthly_rate * factor / (factor - 1)
    return payment.quantize(_CENT, rounding=ROUND_UP)


def _distribute(month: int, available: Decimal, payees: list[_Payee]) -> Decimal:
    """Pay one tier from `available`: in full where it covers every payee's
    due, otherwise pro rata by due, cents rounded down and the remainder
    handed out in listing order. Returns what is left."""
    dues = [(payee, payee.due()) for payee in payees]
    total_due = sum((due for _, due in dues), _ZERO)
    if total_due <= 0:
        return available
    if available >= total_due:
        for payee, due in dues:
            payee.pay(month, due)
        return available - total_due
    shares: list[tuple[_Payee, Decimal, Decimal]] = []
    handed = _ZERO
    for payee, due in dues:
        share = (available * due / total_due).quantize(_CENT, rounding=ROUND_DOWN)
        shares.append((payee, share, due))
        handed += share
    remainder = available - handed
    for index, (payee, share, due) in enumerate(shares):
        extra = min(remainder, due - share)
        if extra > 0:
            shares[index] = (payee, share + extra, due)
            remainder -= extra
    for payee, share, _due in shares:
        payee.pay(month, share)
    return remainder


def _treatment_for(plan: PlanBody | None, claim_id: str) -> SecuredTreatment | None:
    if plan is None:
        return None
    return next((t for t in plan.secured_treatments if t.claim_id == claim_id), None)


def _secured_payees(
    plan: PlanBody | None,
    inputs: PlanInputs,
    liens: LienFigures,
    term: int,
    warnings: list[str],
    problems: list[str],
) -> tuple[list[_Payee], list[_Payee], list[PoolClaim]]:
    """The conduit payees, the equal-monthly-payment payees, and the
    deficiencies the plan sends to the unsecured class."""
    case_file = inputs.case_file
    conduit: list[_Payee] = []
    emp: list[_Payee] = []
    deficiencies: list[PoolClaim] = []
    secured_ids = {i for i, _ in _claims(case_file, "secured")}

    if plan is not None:
        for index, row in enumerate(plan.secured_treatments):
            if row.claim_id is not None and row.claim_id not in secured_ids:
                problems.append(
                    f"Treatment {index + 1} names a claim that is not a secured "
                    "claim of this case — fix the reference or remove the row."
                )

    for claim_id, claim in _claims(case_file, "secured"):
        label = _claim_label(case_file, claim_id, claim)
        lien = liens.claim(claim_id)
        treatment = _treatment_for(plan, claim_id)
        path = (
            f"plan.secured_treatments[{treatment.id}]" if treatment is not None else ""
        )
        if treatment is None or treatment.treatment is None:
            if plan is None:
                # No proposal yet: the missing plan is the one problem to
                # report, not each claim it would have treated.
                continue
            warnings.append(
                f"{label}: no treatment chosen, so the plan pays this secured "
                "claim nothing — § 1325(a)(5) requires one unless the holder "
                "accepts the plan."
            )
            continue
        kind = treatment.treatment
        if kind == "surrender":
            if lien is not None and lien.unsecured_amount:
                deficiencies.append(
                    PoolClaim(
                        claim_id,
                        label,
                        lien.unsecured_amount,
                        f"claim {claim_id}: deficiency after surrender "
                        "(§ 1325(a)(5)(C)), the claim less the collateral's value",
                    )
                )
            continue
        rate = _dec(
            treatment.arrearage_interest_rate
            if kind == "cure_and_maintain"
            else treatment.interest_rate
        )
        fixed = _dec(treatment.monthly_payment)
        principal: Decimal | None
        source: str
        if kind == "cure_and_maintain":
            if treatment.maintenance_payment is not None:
                conduit.append(
                    _Payee(
                        key=f"conduit:{claim_id}",
                        label=f"{label} — ongoing payment",
                        claim_id=claim_id,
                        treatment=kind,
                        allowed=_money(Decimal(treatment.maintenance_payment) * term),
                        balance=_ZERO,
                        monthly_rate=Decimal("0"),
                        rate=None,
                        monthly_payment=_money(Decimal(treatment.maintenance_payment)),
                        source=f"{path}.maintenance_payment, every month of the term "
                        "(§ 1322(b)(5))",
                        recurring=True,
                    )
                )
            principal = _dec(treatment.arrearage)
            if principal is None or principal == 0:
                continue
            source = f"{path}.arrearage, cured under § 1322(b)(5)"
            payee_label = f"{label} — arrearage"
        else:  # cramdown
            claim_amount = _dec(claim.amount)
            typed = _dec(treatment.cramdown_value)
            if typed is not None:
                principal = (
                    min(typed, claim_amount) if claim_amount is not None else typed
                )
                source = f"{path}.cramdown_value (§ 506(a), § 1325(a)(5)(B))"
                if claim_amount is not None and claim_amount > principal:
                    deficiencies.append(
                        PoolClaim(
                            claim_id,
                            label,
                            _money(claim_amount - principal),
                            f"claim {claim_id}: the claim less {path}.cramdown_value",
                        )
                    )
            elif lien is not None and lien.secured_amount is not None:
                principal = lien.secured_amount
                source = (
                    f"claim {claim_id}: secured portion, the collateral's value "
                    "less senior liens (§ 506(a), § 1325(a)(5)(B))"
                )
                if lien.unsecured_amount:
                    deficiencies.append(
                        PoolClaim(
                            claim_id,
                            label,
                            lien.unsecured_amount,
                            f"claim {claim_id}: unsecured portion, bifurcated "
                            "under § 506(a)",
                        )
                    )
            else:
                problems.append(
                    f"{label}: a cramdown needs a value — type one, or give the "
                    "claim an amount and its collateral a value."
                )
                continue
            if rate is None:
                problems.append(
                    f"{label}: a cramdown needs an interest rate "
                    "(§ 1325(a)(5)(B)(ii), Till v. SCS Credit Corp.)."
                )
            payee_label = f"{label} — secured value"
        monthly_rate = _monthly_rate(rate)
        payment = (
            fixed if fixed is not None else _amortised(principal, monthly_rate, term)
        )
        if rate is not None:
            source += f", {rate:f}% a year"
        source += (
            f"; {path}.monthly_payment"
            if fixed is not None
            else f"; amortised over {term} months"
        )
        emp.append(
            _Payee(
                key=f"secured:{claim_id}",
                label=payee_label,
                claim_id=claim_id,
                treatment=kind,
                allowed=_money(principal),
                balance=_money(principal),
                monthly_rate=monthly_rate,
                rate=rate,
                monthly_payment=payment,
                source=source,
            )
        )
    return conduit, emp, deficiencies


def _priority_payees(plan: PlanBody | None, case_file: CaseFile) -> list[_Payee]:
    share = _dec(plan.priority_percentage) if plan is not None else None
    rate = _dec(plan.priority_interest_rate) if plan is not None else None
    payees: list[_Payee] = []
    for claim_id, claim in _claims(case_file, "priority_unsecured"):
        portion = _priority_portion(claim)
        if portion <= 0:
            continue
        owed = _money(portion * (share if share is not None else _HUNDRED) / _HUNDRED)
        source = f"claim {claim_id}: priority amount"
        source += (
            f" at plan.priority_percentage {share:f}%"
            if share is not None
            else ", paid in full (§ 1322(a)(2))"
        )
        if rate is not None:
            source += f", plan.priority_interest_rate {rate:f}% a year"
        payees.append(
            _Payee(
                key=f"priority:{claim_id}",
                label=_claim_label(case_file, claim_id, claim),
                claim_id=claim_id,
                treatment=None,
                allowed=owed,
                balance=owed,
                monthly_rate=_monthly_rate(rate),
                rate=rate,
                monthly_payment=None,
                source=source,
            )
        )
    return payees


def _plan_pool(case_file: CaseFile, deficiencies: list[PoolClaim]) -> list[PoolClaim]:
    """The plan's general unsecured class: the nonpriority claims, the
    priority claims' nonpriority parts, and the deficiencies the plan's own
    treatments create — a cured claim stays whole and adds nothing."""
    pool: list[PoolClaim] = []
    for claim_id, claim in _claims(case_file, "nonpriority_unsecured"):
        if claim.amount is None:
            continue
        pool.append(
            PoolClaim(
                claim_id,
                _claim_label(case_file, claim_id, claim),
                _money(Decimal(claim.amount)),
                f"claim {claim_id}: nonpriority unsecured amount",
            )
        )
    for claim_id, claim in _claims(case_file, "priority_unsecured"):
        if claim.nonpriority_amount is None or Decimal(claim.nonpriority_amount) == 0:
            continue
        pool.append(
            PoolClaim(
                claim_id,
                _claim_label(case_file, claim_id, claim),
                _money(Decimal(claim.nonpriority_amount)),
                f"claim {claim_id}: nonpriority part of a priority claim",
            )
        )
    return pool + deficiencies


def _unsecured_target(
    plan: PlanBody | None, pool_total: Decimal, problems: list[str]
) -> tuple[Decimal | None, str | None, str]:
    """What the unsecured class is owed, where it comes from, and the
    treatment actually applied."""
    treatment = (plan.unsecured_treatment if plan is not None else None) or "pot"
    if treatment == "percentage":
        share = _dec(plan.unsecured_percentage) if plan is not None else None
        if share is None:
            problems.append(
                "Type the percentage the plan promises unsecured creditors."
            )
            return None, None, treatment
        return (
            _money(pool_total * share / _HUNDRED),
            f"plan.unsecured_percentage {share:f}% of the unsecured pool",
            treatment,
        )
    if treatment == "amount":
        promised = _dec(plan.unsecured_amount) if plan is not None else None
        if promised is None:
            problems.append("Type the amount the plan promises unsecured creditors.")
            return None, None, treatment
        return _money(promised), "plan.unsecured_amount", treatment
    return (
        pool_total,
        "whatever remains, up to the full unsecured pool (a pot plan)",
        treatment,
    )


# ── the liquidation test ───────────────────────────────────────────────


def section_326a_commission(disbursed: Decimal) -> Decimal:
    """The Chapter 7 trustee's maximum commission on `disbursed`."""
    remaining = max(disbursed, _ZERO)
    floor = Decimal("0")
    total = Decimal("0")
    for ceiling, rate in SECTION_326A_TIERS:
        band = remaining if ceiling is None else min(remaining, ceiling - floor)
        if band <= 0:
            break
        total += band * rate
        remaining -= band
        if ceiling is not None:
            floor = ceiling
    return _money(total)


def _liquidation(
    plan: PlanBody | None,
    inputs: PlanInputs,
    liens: LienFigures,
    warnings: list[str],
) -> Liquidation:
    case_file = inputs.case_file
    rows: list[LiquidationAsset] = []
    for asset in inputs.assets:
        if asset.current_value is None:
            warnings.append(
                f"{asset.description or 'An asset'} has no value on Schedule A/B, "
                "so the liquidation test counts it at $0."
            )
        value = asset.current_value or _ZERO
        equity = asset.net_equity if asset.net_equity is not None else _ZERO
        unexempt = asset.unexempt if asset.unexempt is not None else _ZERO
        rows.append(
            LiquidationAsset(
                asset_id=asset.asset_id,
                description=asset.description,
                value=_money(value),
                liens=_money(value - equity),
                exempt=_money(equity - unexempt),
                unexempt=_money(unexempt),
            )
        )
    unexempt_total = sum((r.unexempt for r in rows), _ZERO)
    commission = section_326a_commission(unexempt_total)
    other = _money(_dec(plan.chapter_7_other_costs) or _ZERO) if plan else _ZERO
    priority_total = _money(
        sum(
            (_priority_portion(c) for _, c in _claims(case_file, "priority_unsecured")),
            _ZERO,
        )
    )
    available = _money(max(unexempt_total - commission - other - priority_total, _ZERO))
    pool, pool_warnings = _chapter_7_pool(case_file, liens)
    warnings.extend(pool_warnings)
    pool_total = _money(sum((p.amount for p in pool), _ZERO))
    percentage = _pct(available, pool_total)
    if percentage is not None and percentage > _HUNDRED:
        percentage = _HUNDRED
    other_source = (
        "plan.chapter_7_other_costs"
        + (
            f" ({plan.chapter_7_other_costs_description})"
            if plan is not None and plan.chapter_7_other_costs_description
            else ""
        )
        if plan is not None and plan.chapter_7_other_costs is not None
        else "none typed"
    )
    return Liquidation(
        assets=tuple(rows),
        property_total=_money(sum((r.value for r in rows), _ZERO)),
        liens_total=_money(sum((r.liens for r in rows), _ZERO)),
        exemptions_total=_money(sum((r.exempt for r in rows), _ZERO)),
        unexempt_total=_money(unexempt_total),
        trustee_commission=commission,
        trustee_commission_rule=SECTION_326A_RULE,
        other_costs=other,
        other_costs_source=other_source,
        priority_total=priority_total,
        available=available,
        pool=tuple(pool),
        pool_total=pool_total,
        percentage=percentage,
    )


# ── the whole ──────────────────────────────────────────────────────────


def calculate_plan(inputs: PlanInputs, plan: PlanBody | None) -> PlanCalculation:
    """The plan's figures, pure: every refusal lands in `problems`, every
    doubt in `warnings`, and nothing raises on an incomplete case."""
    problems: list[str] = list(inputs.upstream_problems)
    warnings: list[str] = []
    case_file = inputs.case_file
    if case_file.case.chapter != 13:
        warnings.append(
            f"This is a Chapter {case_file.case.chapter} case; a plan is "
            "proposed only under Chapter 13."
        )
    liens = derive_liens(case_file)

    funding = _funding(plan, inputs, problems, warnings)
    term = funding.term_months or 0
    trustee_pct = _dec(plan.trustee_percentage) if plan is not None else None
    if plan is not None and trustee_pct is None:
        problems.append(
            "Type the trustee's percentage fee — the district's standing "
            "trustee sets it, up to 10% (28 U.S.C. § 586(e))."
        )

    conduit, emp, deficiencies = _secured_payees(
        plan, inputs, liens, max(term, 1), warnings, problems
    )
    attorney: list[_Payee] = []
    fees = _dec(plan.attorney_fees) if plan is not None else None
    if fees is not None and fees > 0:
        cap = _dec(plan.attorney_fee_monthly) if plan is not None else None
        attorney.append(
            _Payee(
                key="attorney",
                label="Attorney's fees",
                claim_id=None,
                treatment=None,
                allowed=_money(fees),
                balance=_money(fees),
                monthly_rate=Decimal("0"),
                rate=None,
                monthly_payment=_money(cap) if cap is not None else None,
                source="plan.attorney_fees"
                + (", up to plan.attorney_fee_monthly a month" if cap else "")
                + " (§ 503(b), § 507(a)(2))",
            )
        )
    priority = _priority_payees(plan, case_file)
    pool = _plan_pool(case_file, deficiencies)
    pool_total = _money(sum((p.amount for p in pool), _ZERO))
    target, target_source, unsecured_kind = _unsecured_target(
        plan, pool_total, problems
    )
    unsecured_rate = _dec(plan.unsecured_interest_rate) if plan is not None else None
    unsecured = _Payee(
        key="unsecured",
        label="General unsecured claims, pro rata",
        claim_id=None,
        treatment=unsecured_kind,
        allowed=target or _ZERO,
        balance=target or _ZERO,
        monthly_rate=_monthly_rate(unsecured_rate),
        rate=unsecured_rate,
        monthly_payment=None,
        source=(target_source or "")
        + (
            f", plan.unsecured_interest_rate {unsecured_rate:f}% a year"
            if unsecured_rate is not None
            else ""
        ),
    )
    trustee = _Payee(
        key="trustee",
        label="Trustee's fee",
        claim_id=None,
        treatment=None,
        allowed=_ZERO,
        balance=_ZERO,
        monthly_rate=Decimal("0"),
        rate=trustee_pct,
        monthly_payment=None,
        source=(
            f"plan.trustee_percentage {trustee_pct:f}% of every receipt "
            "(28 U.S.C. § 586(e))"
            if trustee_pct is not None
            else "no trustee percentage typed"
        ),
        recurring=True,
    )

    surplus = _ZERO
    receipts_by_month = dict(funding.schedule)
    for month, value, _desc in funding.lump_sums:
        receipts_by_month[month] = receipts_by_month.get(month, _ZERO) + value
    ran = bool(funding.schedule)
    for month in range(1, len(funding.schedule) + 1):
        receipts = receipts_by_month.get(month, _ZERO)
        fee = _money(receipts * (trustee_pct or Decimal("0")) / _HUNDRED)
        trustee.pay(month, fee)
        trustee.allowed += fee
        available = receipts - fee
        for payee in (*emp, *priority, unsecured):
            payee.accrue()
        for tier in (conduit, emp, attorney, priority, [unsecured]):
            available = _distribute(month, available, tier)
        surplus += available

    tiers = (
        (CLASS_TRUSTEE, [trustee] if ran else []),
        (CLASS_CONDUIT, conduit),
        (CLASS_SECURED, emp),
        (CLASS_ATTORNEY, attorney),
        (CLASS_PRIORITY, priority),
        (CLASS_UNSECURED, [unsecured]),
    )
    classes = tuple(
        ClassSummary(
            key=key,
            label=_CLASS_LABELS[key],
            rows=tuple(payee.row(max(term, 1)) for payee in payees),
        )
        for key, payees in tiers
        if payees
    )
    unsecured_row = classes[-1].rows[0]
    unsecured_paid = unsecured_row.payout

    # Feasibility.
    blocking = funding.term_months is None or funding.base_payment is None
    blocking = blocking or (plan is not None and trustee_pct is None)
    blocking = blocking or target is None
    reasons: list[str] = []
    shortfall = _ZERO
    for summary in classes:
        if summary.key == CLASS_UNSECURED and unsecured_kind == "pot":
            continue
        if summary.unpaid > 0:
            shortfall += summary.unpaid
            reasons.append(
                f"{summary.label}: ${summary.unpaid:,.2f} is still unpaid after "
                f"month {term}."
            )
    distributed = _money(sum((s.principal + s.interest for s in classes), _ZERO))
    excess = inputs.schedule_j_excess
    peak_payment = max((p for _, p in funding.schedule), default=None)
    exceeds: bool | None = None
    if excess is not None and peak_payment is not None:
        exceeds = peak_payment > _money(excess.amount)
        if exceeds:
            warnings.append(
                f"The plan payment (up to ${peak_payment:,.2f} a month) is more "
                f"than Schedule J's monthly net income (${excess.amount:,.2f}, "
                "line 23c) — § 1325(a)(6) asks whether the debtor can make it."
            )
    if unsecured_kind == "pot" and surplus > 0:
        warnings.append(
            f"${surplus:,.2f} is left after every class is paid in full — the "
            "payment or the term could come down."
        )
    feasibility = Feasibility(
        feasible=None if blocking else not reasons,
        total_funding=funding.total,
        total_distributed=distributed,
        surplus=_money(surplus),
        shortfall=_money(shortfall),
        reasons=tuple(reasons),
        schedule_j_excess=excess,
        exceeds_schedule_j=exceeds,
    )

    liquidation = _liquidation(plan, inputs, liens, warnings)

    # The unsecured class's percentage, nominal and (where a rate is typed)
    # at present value.
    pv_rate = _dec(plan.present_value_rate) if plan is not None else None
    if pv_rate is not None:
        monthly = _monthly_rate(pv_rate)
        value = _money(
            sum(
                (
                    amount / ((1 + monthly) ** month)
                    for month, amount in unsecured.paid_by_month.items()
                ),
                _ZERO,
            )
        )
    else:
        value = unsecured_paid
    plan_percentage = _pct(unsecured_paid, pool_total)
    tested_percentage = _pct(value, pool_total)
    passes: bool | None
    if blocking:
        passes = None
    elif liquidation.available <= 0:
        passes = True
    elif tested_percentage is None or liquidation.percentage is None:
        passes = None
    else:
        passes = tested_percentage >= liquidation.percentage
    best_interests = BestInterests(
        plan_percentage=tested_percentage,
        liquidation_percentage=liquidation.percentage,
        plan_unsecured_value=value,
        present_value_rate=pv_rate,
        passes=passes,
        rule=(
            "11 U.S.C. § 1325(a)(4): unsecured creditors receive at least what "
            "a Chapter 7 liquidation would pay them"
            + (
                f", valued at plan.present_value_rate {pv_rate:f}% a year"
                if pv_rate is not None
                else ", compared nominally — type a present-value rate to "
                "discount the plan's future payments"
            )
        ),
    )

    if (
        funding.term_months is not None
        and inputs.commitment_months is not None
        and funding.term_months < inputs.commitment_months
        and plan_percentage is not None
        and plan_percentage < _HUNDRED
    ):
        warnings.append(
            f"The term ({funding.term_months} months) is shorter than the "
            f"{inputs.commitment_months}-month commitment period, which "
            "§ 1325(b)(4)(B) allows only when unsecured claims are paid in full."
        )

    return PlanCalculation(
        plan_present=plan is not None,
        chapter=case_file.case.chapter,
        commitment_months=inputs.commitment_months,
        commitment_source=inputs.commitment_source,
        funding=funding,
        trustee_percentage=trustee_pct,
        classes=classes,
        unsecured_pool=tuple(pool),
        unsecured_pool_total=pool_total,
        unsecured_target=target,
        unsecured_target_source=target_source,
        unsecured_percentage=plan_percentage,
        feasibility=feasibility,
        liquidation=liquidation,
        best_interests=best_interests,
        warnings=tuple(w for w in warnings if w),
        problems=tuple(problems),
    )


# ── the wire shape ─────────────────────────────────────────────────────


def _m(value: Decimal | None) -> str | None:
    return None if value is None else f"{_money(value):f}"


def _rate(value: Decimal | None) -> str | None:
    return None if value is None else f"{value:f}"


def _row_json(row: PayoutRow) -> dict[str, object]:
    return {
        "key": row.key,
        "label": row.label,
        "claimId": row.claim_id,
        "treatment": row.treatment,
        "allowed": _m(row.allowed),
        "monthlyPayment": _m(row.monthly_payment),
        "rate": _rate(row.rate),
        "principal": _m(row.principal),
        "interest": _m(row.interest),
        "payout": _m(row.payout),
        "firstMonth": row.first_month,
        "lastMonth": row.last_month,
        "monthsPaid": row.months_paid,
        "unpaid": _m(row.unpaid),
        "source": row.source,
    }


def _class_json(summary: ClassSummary) -> dict[str, object]:
    return {
        "key": summary.key,
        "label": summary.label,
        "allowed": _m(summary.allowed),
        "principal": _m(summary.principal),
        "interest": _m(summary.interest),
        "payout": _m(summary.principal + summary.interest),
        "unpaid": _m(summary.unpaid),
        "firstMonth": summary.first_month,
        "lastMonth": summary.last_month,
        "rows": [_row_json(row) for row in summary.rows],
    }


def _pool_json(claim: PoolClaim) -> dict[str, object]:
    return {
        "claimId": claim.claim_id,
        "label": claim.label,
        "amount": _m(claim.amount),
        "source": claim.source,
    }


def _sourced_json(value: Sourced | None) -> dict[str, object] | None:
    if value is None:
        return None
    return {"amount": _m(value.amount), "source": value.source}


def plan_calculation_json(calc: PlanCalculation) -> dict[str, object]:
    funding = calc.funding
    feasibility = calc.feasibility
    liquidation = calc.liquidation
    best = calc.best_interests
    return {
        "planPresent": calc.plan_present,
        "chapter": calc.chapter,
        "commitmentPeriod": {
            "months": calc.commitment_months,
            "source": calc.commitment_source,
        },
        "funding": {
            "termMonths": funding.term_months,
            "termSource": funding.term_source,
            "basePayment": _m(funding.base_payment),
            "baseSource": funding.base_source,
            "schedule": [
                {"month": month, "payment": _m(payment)}
                for month, payment in funding.schedule
            ],
            "lumpSums": [
                {"month": month, "amount": _m(value), "description": description}
                for month, value, description in funding.lump_sums
            ],
            "total": _m(funding.total),
        },
        "trusteePercentage": _rate(calc.trustee_percentage),
        "classes": [_class_json(summary) for summary in calc.classes],
        "unsecured": {
            "pool": [_pool_json(claim) for claim in calc.unsecured_pool],
            "poolTotal": _m(calc.unsecured_pool_total),
            "target": _m(calc.unsecured_target),
            "targetSource": calc.unsecured_target_source,
            "percentage": _m(calc.unsecured_percentage),
        },
        "feasibility": {
            "feasible": feasibility.feasible,
            "totalFunding": _m(feasibility.total_funding),
            "totalDistributed": _m(feasibility.total_distributed),
            "surplus": _m(feasibility.surplus),
            "shortfall": _m(feasibility.shortfall),
            "reasons": list(feasibility.reasons),
            "scheduleJExcess": _sourced_json(feasibility.schedule_j_excess),
            "exceedsScheduleJ": feasibility.exceeds_schedule_j,
        },
        "liquidation": {
            "assets": [
                {
                    "assetId": row.asset_id,
                    "description": row.description,
                    "value": _m(row.value),
                    "liens": _m(row.liens),
                    "exempt": _m(row.exempt),
                    "unexempt": _m(row.unexempt),
                }
                for row in liquidation.assets
            ],
            "propertyTotal": _m(liquidation.property_total),
            "liensTotal": _m(liquidation.liens_total),
            "exemptionsTotal": _m(liquidation.exemptions_total),
            "unexemptTotal": _m(liquidation.unexempt_total),
            "trusteeCommission": _m(liquidation.trustee_commission),
            "trusteeCommissionRule": liquidation.trustee_commission_rule,
            "otherCosts": _m(liquidation.other_costs),
            "otherCostsSource": liquidation.other_costs_source,
            "priorityTotal": _m(liquidation.priority_total),
            "available": _m(liquidation.available),
            "pool": [_pool_json(claim) for claim in liquidation.pool],
            "poolTotal": _m(liquidation.pool_total),
            "percentage": _m(liquidation.percentage),
        },
        "bestInterests": {
            "planPercentage": _m(best.plan_percentage),
            "liquidationPercentage": _m(best.liquidation_percentage),
            "planUnsecuredValue": _m(best.plan_unsecured_value),
            "presentValueRate": _rate(best.present_value_rate),
            "passes": best.passes,
            "rule": best.rule,
        },
        "warnings": list(calc.warnings),
        "problems": list(calc.problems),
    }
