"""The Chapter 13 plan — what the debtor proposes to pay, over how long, and
how each class of claim is treated (issue 16.2 / #366).

A plan is a payment waterfall: a monthly payment over a term funds the
trustee's commission, the attorney's fees, the secured claims and their
arrears, the priority claims, and then a share for general unsecured
creditors. This record holds only the PROPOSAL — the choices a preparer
makes and confirms. Every figure the plan produces (each class's payout,
the unsecured percentage, feasibility, the § 1325(a)(4) liquidation floor)
is arithmetic over this record and the case's other records, and lives in
the API's calculator (`services/api` `core/chapter13_plan.py`), never here:
the data model's "derived values are computed, never stored" rule.

ONE PER CASE, NOT KEY-ENFORCED. The means-test input's rule: progressive
intake means a record must be savable before it is complete, and the packet
gate (issue #367) owns the cardinality.

WHAT IT DOES NOT RESTATE. The commitment period and the disposable income
are the means-test engine's (B122C-1 / B122C-2, issue #365); the claims, the
collateral and the exemptions are their own records. So `term_months` is an
OVERRIDE of the commitment period, absent by default, and the two payment
sources that are not a typed figure name where the payment comes from
instead of copying it:

- `schedule_j_excess` — Schedule J line 23c, the monthly net income
  (106I line 12 less 106J line 22c);
- `disposable_income` — B122C-2 line 45, the § 1325(b)(2) figure the
  engine computes for an above-median debtor.

A copied figure would agree with its source only until somebody edited an
expense.

THE TREATMENTS are the three the issue names for a secured claim — cure and
maintain (§ 1322(b)(5)), cramdown to the collateral's value at a rate
(§ 1325(a)(5)(B), Till), and surrender (§ 1325(a)(5)(C)) — one row per
secured claim, addressed by a client-chosen row id so provenance can name
`secured_treatments[<id>].interest_rate` (the notice-party rule). Priority
claims are paid in full without interest unless a percentage or a rate says
otherwise (§ 1322(a)(2) and (a)(4)); general unsecured creditors take what
is left (`pot`), or are promised a percentage or an amount.

RATES ARE PERCENTAGES CARRIED AS STRINGS (`fields.percentage`) — per annum
for interest, of each receipt for the trustee — for money's reason.
"""

from __future__ import annotations

from collections.abc import Callable, Mapping, Sequence
from dataclasses import dataclass
from decimal import Decimal
from typing import Final, TypeVar

from insolvia_core.errors import FieldValidationError

from .case_entities import EntityKind
from .fields import choice, money, percentage, text, whole_number
from .provenance import ADDRESSABLE_ID_RE

# § 1322(d): no plan may provide for payments over a period longer than five
# years.
MAX_TERM_MONTHS: Final = 60

# 28 U.S.C. § 586(e)(1)(B): a standing trustee's percentage fee may not
# exceed ten per cent of the payments received under plans.
MAX_TRUSTEE_PERCENTAGE: Final = Decimal("10")

# Where the base monthly payment comes from.
PAYMENT_SOURCES: Final = ("fixed", "schedule_j_excess", "disposable_income")

# A secured claim's treatment.
SECURED_TREATMENTS: Final = ("cure_and_maintain", "cramdown", "surrender")

# What general unsecured creditors receive: whatever is left, a promised
# percentage of their claims, or a promised dollar total.
UNSECURED_TREATMENTS: Final = ("pot", "percentage", "amount")


@dataclass(frozen=True)
class StepPayment:
    """From `start_month` on (1-based, inclusive), the monthly payment is
    `monthly_payment` — until a later step replaces it."""

    id: str
    start_month: int | None = None
    monthly_payment: str | None = None


@dataclass(frozen=True)
class LumpSum:
    """A one-time payment into the plan in `month` — a tax refund, the sale
    of a surrendered-equity asset."""

    id: str
    month: int | None = None
    amount: str | None = None
    description: str | None = None


@dataclass(frozen=True)
class SecuredTreatment:
    """How the plan treats one secured claim (`claim_id`).

    cure_and_maintain: `arrearage` is cured through the plan, with interest
        at `arrearage_interest_rate` where the contract or law requires it;
        `maintenance_payment`, when present, is the ongoing contract payment
        paid THROUGH the plan (a conduit) every month of the term — absent,
        the debtor pays it directly.
    cramdown: the claim is paid to the collateral's value — `cramdown_value`
        where typed, otherwise the secured portion the case's claims and
        assets derive — with interest at `interest_rate`; the rest joins the
        general unsecured class.
    surrender: nothing is paid through the plan; any deficiency joins the
        general unsecured class.

    `monthly_payment` fixes the equal monthly payment (§ 1325(a)(5)(B)(iii))
    for the arrearage or the cramdown; absent, the calculator amortises the
    balance over the term.
    """

    id: str
    claim_id: str | None = None
    treatment: str | None = None
    arrearage: str | None = None
    arrearage_interest_rate: str | None = None
    maintenance_payment: str | None = None
    cramdown_value: str | None = None
    interest_rate: str | None = None
    monthly_payment: str | None = None


@dataclass(frozen=True)
class PlanBody:
    # The term, in months; absent = the means test's commitment period.
    term_months: int | None = None
    # The base monthly payment.
    payment_source: str | None = None
    monthly_payment: str | None = None
    step_payments: tuple[StepPayment, ...] = ()
    lump_sums: tuple[LumpSum, ...] = ()
    # The trustee's percentage fee on every receipt.
    trustee_percentage: str | None = None
    # The attorney's fees still owed, paid through the plan, and an optional
    # monthly cap on what they take.
    attorney_fees: str | None = None
    attorney_fee_monthly: str | None = None
    secured_treatments: tuple[SecuredTreatment, ...] = ()
    # Priority claims: the share of each paid (absent = 100) and any rate.
    priority_percentage: str | None = None
    priority_interest_rate: str | None = None
    # General unsecured claims.
    unsecured_treatment: str | None = None
    unsecured_percentage: str | None = None
    unsecured_amount: str | None = None
    unsecured_interest_rate: str | None = None
    # The § 1325(a)(4) comparison's entered side: the costs of a Chapter 7
    # liquidation beyond the trustee's § 326(a) commission (sale costs, a
    # realtor, an auctioneer), and the rate a plan's future payments are
    # discounted at to compare them with a liquidation paid now.
    chapter_7_other_costs: str | None = None
    chapter_7_other_costs_description: str | None = None
    present_value_rate: str | None = None


RowT = TypeVar("RowT")


def _rows(
    value: object,
    field_name: str,
    errors: dict[str, str],
    build: Callable[[str, Mapping[str, object], str], RowT],
) -> tuple[RowT, ...]:
    """A list of rows with client-chosen addressable ids, unique per list —
    the shape every embedded list on a case record shares."""
    if value is None:
        return ()
    if not isinstance(value, Sequence) or isinstance(value, (str, bytes)):
        errors[field_name] = "Must be a list."
        return ()
    rows: list[RowT] = []
    seen: set[str] = set()
    for index, raw in enumerate(value):
        path = f"{field_name}[{index}]"
        if not isinstance(raw, Mapping):
            errors[path] = "Must be an object."
            continue
        given_id = raw.get("id")
        if not isinstance(given_id, str) or not ADDRESSABLE_ID_RE.match(given_id):
            errors[f"{path}.id"] = (
                "Required, and must be letters, digits, hyphen or underscore — "
                "generate one per row so provenance can name it."
            )
            continue
        if given_id in seen:
            errors[f"{path}.id"] = "Duplicate id."
            continue
        seen.add(given_id)
        rows.append(build(given_id, raw, path))
    return tuple(rows)


def _month(value: object, path: str, errors: dict[str, str]) -> int | None:
    month = whole_number(value, path, errors, maximum=MAX_TERM_MONTHS)
    if month is not None and month < 1:
        errors[path] = f"Must be a month of the plan, 1 to {MAX_TERM_MONTHS}."
        return None
    return month


def parse_plan(payload: Mapping[str, object]) -> PlanBody:
    errors: dict[str, str] = {}

    def amount(key: str) -> str | None:
        return money(payload.get(key), key, errors)

    def rate(key: str) -> str | None:
        return percentage(payload.get(key), key, errors)

    def step(row_id: str, raw: Mapping[str, object], path: str) -> StepPayment:
        return StepPayment(
            id=row_id,
            start_month=_month(raw.get("start_month"), f"{path}.start_month", errors),
            monthly_payment=money(
                raw.get("monthly_payment"), f"{path}.monthly_payment", errors
            ),
        )

    def lump(row_id: str, raw: Mapping[str, object], path: str) -> LumpSum:
        return LumpSum(
            id=row_id,
            month=_month(raw.get("month"), f"{path}.month", errors),
            amount=money(raw.get("amount"), f"{path}.amount", errors),
            description=text(raw.get("description"), f"{path}.description", errors),
        )

    def secured(row_id: str, raw: Mapping[str, object], path: str) -> SecuredTreatment:
        return SecuredTreatment(
            id=row_id,
            claim_id=text(raw.get("claim_id"), f"{path}.claim_id", errors, limit=64),
            treatment=choice(
                raw.get("treatment"), SECURED_TREATMENTS, f"{path}.treatment", errors
            ),
            arrearage=money(raw.get("arrearage"), f"{path}.arrearage", errors),
            arrearage_interest_rate=percentage(
                raw.get("arrearage_interest_rate"),
                f"{path}.arrearage_interest_rate",
                errors,
            ),
            maintenance_payment=money(
                raw.get("maintenance_payment"), f"{path}.maintenance_payment", errors
            ),
            cramdown_value=money(
                raw.get("cramdown_value"), f"{path}.cramdown_value", errors
            ),
            interest_rate=percentage(
                raw.get("interest_rate"), f"{path}.interest_rate", errors
            ),
            monthly_payment=money(
                raw.get("monthly_payment"), f"{path}.monthly_payment", errors
            ),
        )

    term = whole_number(payload.get("term_months"), "term_months", errors, maximum=1000)
    if term is not None and not 1 <= term <= MAX_TERM_MONTHS:
        errors["term_months"] = (
            f"A plan runs 1 to {MAX_TERM_MONTHS} months — § 1322(d) caps it at "
            "five years."
        )
        term = None

    body = PlanBody(
        term_months=term,
        payment_source=choice(
            payload.get("payment_source"), PAYMENT_SOURCES, "payment_source", errors
        ),
        monthly_payment=amount("monthly_payment"),
        step_payments=_rows(
            payload.get("step_payments"), "step_payments", errors, step
        ),
        lump_sums=_rows(payload.get("lump_sums"), "lump_sums", errors, lump),
        trustee_percentage=percentage(
            payload.get("trustee_percentage"),
            "trustee_percentage",
            errors,
            maximum=MAX_TRUSTEE_PERCENTAGE,
        ),
        attorney_fees=amount("attorney_fees"),
        attorney_fee_monthly=amount("attorney_fee_monthly"),
        secured_treatments=_rows(
            payload.get("secured_treatments"), "secured_treatments", errors, secured
        ),
        priority_percentage=rate("priority_percentage"),
        priority_interest_rate=rate("priority_interest_rate"),
        unsecured_treatment=choice(
            payload.get("unsecured_treatment"),
            UNSECURED_TREATMENTS,
            "unsecured_treatment",
            errors,
        ),
        unsecured_percentage=rate("unsecured_percentage"),
        unsecured_amount=amount("unsecured_amount"),
        unsecured_interest_rate=rate("unsecured_interest_rate"),
        chapter_7_other_costs=amount("chapter_7_other_costs"),
        chapter_7_other_costs_description=text(
            payload.get("chapter_7_other_costs_description"),
            "chapter_7_other_costs_description",
            errors,
        ),
        present_value_rate=rate("present_value_rate"),
    )

    # Two steps starting in one month would be two answers to one payment.
    starts: set[int] = set()
    for index, row in enumerate(body.step_payments):
        if row.start_month is None:
            continue
        if row.start_month in starts:
            errors[f"step_payments[{index}].start_month"] = (
                "Another step already starts in this month."
            )
        starts.add(row.start_month)

    # One treatment per claim: two would be two plans for one creditor.
    treated: set[str] = set()
    for index, treatment in enumerate(body.secured_treatments):
        if treatment.claim_id is None:
            continue
        if treatment.claim_id in treated:
            errors[f"secured_treatments[{index}].claim_id"] = (
                "This claim already has a treatment."
            )
        treated.add(treatment.claim_id)

    if errors:
        raise FieldValidationError(errors)
    return body


PLAN: EntityKind[PlanBody] = EntityKind(
    name="plan",
    collection="plans",
    sk_prefix="PLAN",
    parse_body=parse_plan,
)
