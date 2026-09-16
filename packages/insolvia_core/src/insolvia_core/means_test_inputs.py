"""The means test's entered figures — B122A-2's questions the case model
does not otherwise store (issue #101).

The § 707(b)(2) calculation draws on three kinds of input: the effective-
dated datasets (the UST registry series), figures DERIVABLE from case
records (CMI from the dated income history, priority and nonpriority debt
from the claims), and figures only the debtor can supply — actual monthly
tax withholding, a term-life premium, the marital adjustment, the vehicles
claimed. This module owns the third kind, one record per case, ENTERED AND
CONFIRMED like the 106I income summary: every one of these lands on a
signed form, so none of them may be a guess this system made.

Field names follow B122A-2's own line subjects (revision 04/25), not line
numbers, so a renumbering does not shift meanings — the income summary's
rule. One-per-case is not key-enforced for the usual progressive-intake
reason; the packet gate owns the cardinality, exactly as it does for the
petition.

The three embedded lists (line 3's marital adjustments, line 33d's other
secured debts, the per-line income overrides) carry client-chosen row ids
so provenance can address `marital_adjustments[<id>].amount` — the
notice-party rule.

Issue #349 (the means-test screen) added the inputs the test needs that no
other record holds, each named for the question it answers:

- **The marital and filing status** (B122A-1 line 1). The debtor records
  answer it by default (a `debtor_2` is "married and filing", a
  `non_filing_spouse` is "married but not filing"); the entered value is
  the override for the one case the records cannot express — a spouse
  living separately, whose income is NOT included (the form's own
  declaration on line 1).
- **Three household sizes, each independently overridable.** The Census
  median table, the IRS National Standards and the IRS housing standard
  each ask "how many people" and each legitimately answers differently
  (the median counts the debtor's household; line 5's IRS family size
  follows the debtor's tax dependents; housing may count everyone under the
  roof). Absent, every one of them is `people_under_65 +
  people_65_or_older`, exactly as before.
- **The three presumption exemptions** (Form 122A-1Supp): debts that are
  not primarily consumer debts (§ 707(b)(1)), a disabled veteran whose
  debts were incurred on active duty or in homeland-defense activity
  (§ 707(b)(2)(D)(i)), and a reservist or National Guard member called to
  active duty (§ 707(b)(2)(D)(ii)). Any one short-circuits the test.
- **Per-line income overrides** (B122A-1 lines 2-10). The dated records
  derive every line; an override replaces one column's line with an
  entered monthly figure, and the derivation says so. The statutory
  exclusions are their own lines here — Social Security Act benefits, the
  victims' payments, and unemployment the debtor contends is a Social
  Security Act benefit (line 8's own "list it here" box).
- **Per-claim secured payments** (line 33d's rows, widened). A row may
  name the `claim_id` it belongs to, the IRS ownership bucket its payment
  offsets (the home for line 9b, vehicle 1 or 2 for lines 13b/13e, other
  for line 33d) and its past-due cure total (line 34). The engine sums the
  bucketed rows where the older single-figure totals are absent, so the
  panel beside a claim and the typed total cannot both count.
"""

from __future__ import annotations

from collections.abc import Mapping, Sequence
from dataclasses import dataclass
from typing import Final

from insolvia_core.errors import FieldValidationError

from .case_entities import EntityKind
from .fields import boolean, choice, money, text, whole_number
from .income import EXCLUDED_INCOME_CATEGORIES, OTHER_INCOME_CATEGORIES
from .provenance import ADDRESSABLE_ID_RE

# Line 11's answer set: the Local Standards publish columns for one and two
# vehicles only, so "2" means "2 or more", as the form itself prints.
MAX_CLAIMED_VEHICLES: Final = 2

# B122A-1 line 1's four answers, named for their meaning (the PDF's export
# values are the projection's business). The "separated" declaration is the
# one the debtor records cannot express, and the one under which Column B is
# NOT filled in.
MARITAL_FILING_STATUSES: Final = (
    "not_married",
    "married_filing_jointly",
    "married_not_filing_same_household",
    "married_not_filing_separated",
)

# Which IRS ownership allowance a secured payment offsets: the home (line
# 9b), the first or second claimed vehicle (lines 13b/13e), or none of them
# (line 33d, "other debts secured by your property").
SECURED_PAYMENT_BUCKETS: Final = ("home", "vehicle_1", "vehicle_2", "other")

# B122A-1's two columns.
INCOME_COLUMNS: Final = ("A", "B")

# The line a per-column income override may name: the nine counted lines
# (2-10), the § 101(10A)(B)(ii) exclusions, and line 8's contention that
# unemployment compensation is a Social Security Act benefit.
UNEMPLOYMENT_AS_SSA: Final = "unemployment_as_ssa"
INCOME_LINE_CATEGORIES: Final = (
    "wages",
    *OTHER_INCOME_CATEGORIES,
    *EXCLUDED_INCOME_CATEGORIES,
    UNEMPLOYMENT_AS_SSA,
)


@dataclass(frozen=True)
class MaritalAdjustmentItem:
    """One line-3 row: a part of the non-filing spouse's income not paid for
    the household, listed separately as the form requires."""

    id: str
    description: str | None = None
    amount: str | None = None


@dataclass(frozen=True)
class OtherSecuredPayment:
    """One line-33d row: a secured debt beyond the home and the two claimed
    vehicles, as its average monthly payment over the next 60 months."""

    id: str
    creditor_name: str | None = None
    property_description: str | None = None
    monthly_payment: str | None = None
    # Issue #349: the secured claim this row is entered beside, which IRS
    # ownership allowance its payment offsets, and the past-due amount the
    # debtor must pay to keep the property (line 34's cure, before ÷ 60).
    claim_id: str | None = None
    bucket: str | None = None
    cure_total: str | None = None


@dataclass(frozen=True)
class IncomeLineOverride:
    """One entered B122A-1 income line, replacing what the dated records
    derive for that column: `column` is A or B, `category` one of
    INCOME_LINE_CATEGORIES, `monthly_amount` the six-month average the
    form prints."""

    id: str
    column: str | None = None
    category: str | None = None
    monthly_amount: str | None = None


@dataclass(frozen=True)
class MeansTestInputBody:
    """B122A-2's entered figures, named for their subjects. Every money
    member is a monthly amount unless the name says otherwise."""

    # Line 5/7: the household composition the deductions use. Line 5 is the
    # sum; the split feeds line 7's per-person health care bands.
    people_under_65: int | None = None
    people_65_or_older: int | None = None
    # Issue #349: the three sizes, each absent by default (= the sum above).
    median_household_size: int | None = None
    irs_family_size: int | None = None
    irs_housing_family_size: int | None = None
    # B122A-1 line 1, overriding what the debtor records say.
    marital_filing_status: str | None = None
    # Form 122A-1Supp: any one exempts the debtor from the presumption.
    non_consumer_debts: bool | None = None
    disabled_veteran: bool | None = None
    reservist_national_guard: bool | None = None
    # B122A-1 lines 2-10, per column, entered in place of the records.
    income_overrides: tuple[IncomeLineOverride, ...] = ()
    # Line 3.
    marital_adjustments: tuple[MaritalAdjustmentItem, ...] = ()
    # Line 9b: average monthly payment for all debts secured by the home.
    home_secured_monthly_total: str | None = None
    # Line 10: the contention that the UST's housing split is wrong.
    housing_adjustment_amount: str | None = None
    housing_adjustment_explanation: str | None = None
    # Lines 11-13.
    vehicle_count: int | None = None
    vehicle_1_loan_monthly: str | None = None
    vehicle_2_loan_monthly: str | None = None
    # Line 15.
    additional_public_transportation: str | None = None
    # Lines 16-23: the Other Necessary Expenses.
    taxes: str | None = None
    involuntary_deductions: str | None = None
    term_life_insurance: str | None = None
    court_ordered_payments: str | None = None
    education_for_employment_or_disability: str | None = None
    childcare: str | None = None
    healthcare_above_allowance: str | None = None
    optional_telecom: str | None = None
    # Lines 25-31: the Additional Expense Deductions.
    health_insurance: str | None = None
    disability_insurance: str | None = None
    health_savings_account: str | None = None
    family_care_contributions: str | None = None
    family_violence_protection: str | None = None
    home_energy_excess: str | None = None
    education_under_18: str | None = None
    additional_food_clothing: str | None = None
    charitable_contributions: str | None = None
    # Line 33d.
    other_secured_payments: tuple[OtherSecuredPayment, ...] = ()
    # Line 34: the total amount past due on line-33 debts that are necessary
    # for support (primary residence, vehicle, other support property).
    priority_cure_total: str | None = None
    # Line 36.
    ch13_eligible: bool | None = None
    ch13_projected_plan_payment: str | None = None


def _parse_marital_adjustments(
    value: object, errors: dict[str, str]
) -> tuple[MaritalAdjustmentItem, ...]:
    if value is None:
        return ()
    if not isinstance(value, Sequence) or isinstance(value, (str, bytes)):
        errors["marital_adjustments"] = "Must be a list."
        return ()
    items: list[MaritalAdjustmentItem] = []
    seen: set[str] = set()
    for index, raw in enumerate(value):
        path = f"marital_adjustments[{index}]"
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
        items.append(
            MaritalAdjustmentItem(
                id=given_id,
                description=text(raw.get("description"), f"{path}.description", errors),
                amount=money(raw.get("amount"), f"{path}.amount", errors),
            )
        )
    return tuple(items)


def _parse_other_secured(
    value: object, errors: dict[str, str]
) -> tuple[OtherSecuredPayment, ...]:
    if value is None:
        return ()
    if not isinstance(value, Sequence) or isinstance(value, (str, bytes)):
        errors["other_secured_payments"] = "Must be a list."
        return ()
    items: list[OtherSecuredPayment] = []
    seen: set[str] = set()
    for index, raw in enumerate(value):
        path = f"other_secured_payments[{index}]"
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
        items.append(
            OtherSecuredPayment(
                id=given_id,
                creditor_name=text(
                    raw.get("creditor_name"), f"{path}.creditor_name", errors
                ),
                property_description=text(
                    raw.get("property_description"),
                    f"{path}.property_description",
                    errors,
                ),
                monthly_payment=money(
                    raw.get("monthly_payment"), f"{path}.monthly_payment", errors
                ),
                claim_id=text(
                    raw.get("claim_id"), f"{path}.claim_id", errors, limit=64
                ),
                bucket=choice(
                    raw.get("bucket"), SECURED_PAYMENT_BUCKETS, f"{path}.bucket", errors
                ),
                cure_total=money(raw.get("cure_total"), f"{path}.cure_total", errors),
            )
        )
    return tuple(items)


def _parse_income_overrides(
    value: object, errors: dict[str, str]
) -> tuple[IncomeLineOverride, ...]:
    if value is None:
        return ()
    if not isinstance(value, Sequence) or isinstance(value, (str, bytes)):
        errors["income_overrides"] = "Must be a list."
        return ()
    items: list[IncomeLineOverride] = []
    seen: set[str] = set()
    seen_lines: set[tuple[str, str]] = set()
    for index, raw in enumerate(value):
        path = f"income_overrides[{index}]"
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
        item = IncomeLineOverride(
            id=given_id,
            column=choice(raw.get("column"), INCOME_COLUMNS, f"{path}.column", errors),
            category=choice(
                raw.get("category"),
                INCOME_LINE_CATEGORIES,
                f"{path}.category",
                errors,
            ),
            monthly_amount=money(
                raw.get("monthly_amount"), f"{path}.monthly_amount", errors
            ),
        )
        # One override per line per column: two rows for Column A's wages
        # would be two answers to one box.
        if item.column is not None and item.category is not None:
            key = (item.column, item.category)
            if key in seen_lines:
                errors[f"{path}.category"] = (
                    "This column already has an override for that line."
                )
                continue
            seen_lines.add(key)
        items.append(item)
    return tuple(items)


def parse_means_test_input(payload: Mapping[str, object]) -> MeansTestInputBody:
    errors: dict[str, str] = {}

    def amount(key: str) -> str | None:
        return money(payload.get(key), key, errors)

    vehicle_count = whole_number(payload.get("vehicle_count"), "vehicle_count", errors)
    if vehicle_count is not None and vehicle_count > MAX_CLAIMED_VEHICLES:
        errors["vehicle_count"] = (
            "The Local Standards publish figures for at most two vehicles — "
            'the form\'s own "2 or more".'
        )
    body = MeansTestInputBody(
        people_under_65=whole_number(
            payload.get("people_under_65"), "people_under_65", errors
        ),
        people_65_or_older=whole_number(
            payload.get("people_65_or_older"), "people_65_or_older", errors
        ),
        median_household_size=whole_number(
            payload.get("median_household_size"), "median_household_size", errors
        ),
        irs_family_size=whole_number(
            payload.get("irs_family_size"), "irs_family_size", errors
        ),
        irs_housing_family_size=whole_number(
            payload.get("irs_housing_family_size"), "irs_housing_family_size", errors
        ),
        marital_filing_status=choice(
            payload.get("marital_filing_status"),
            MARITAL_FILING_STATUSES,
            "marital_filing_status",
            errors,
        ),
        non_consumer_debts=boolean(
            payload.get("non_consumer_debts"), "non_consumer_debts", errors
        ),
        disabled_veteran=boolean(
            payload.get("disabled_veteran"), "disabled_veteran", errors
        ),
        reservist_national_guard=boolean(
            payload.get("reservist_national_guard"), "reservist_national_guard", errors
        ),
        income_overrides=_parse_income_overrides(
            payload.get("income_overrides"), errors
        ),
        marital_adjustments=_parse_marital_adjustments(
            payload.get("marital_adjustments"), errors
        ),
        home_secured_monthly_total=amount("home_secured_monthly_total"),
        housing_adjustment_amount=amount("housing_adjustment_amount"),
        housing_adjustment_explanation=text(
            payload.get("housing_adjustment_explanation"),
            "housing_adjustment_explanation",
            errors,
        ),
        vehicle_count=vehicle_count,
        vehicle_1_loan_monthly=amount("vehicle_1_loan_monthly"),
        vehicle_2_loan_monthly=amount("vehicle_2_loan_monthly"),
        additional_public_transportation=amount("additional_public_transportation"),
        taxes=amount("taxes"),
        involuntary_deductions=amount("involuntary_deductions"),
        term_life_insurance=amount("term_life_insurance"),
        court_ordered_payments=amount("court_ordered_payments"),
        education_for_employment_or_disability=amount(
            "education_for_employment_or_disability"
        ),
        childcare=amount("childcare"),
        healthcare_above_allowance=amount("healthcare_above_allowance"),
        optional_telecom=amount("optional_telecom"),
        health_insurance=amount("health_insurance"),
        disability_insurance=amount("disability_insurance"),
        health_savings_account=amount("health_savings_account"),
        family_care_contributions=amount("family_care_contributions"),
        family_violence_protection=amount("family_violence_protection"),
        home_energy_excess=amount("home_energy_excess"),
        education_under_18=amount("education_under_18"),
        additional_food_clothing=amount("additional_food_clothing"),
        charitable_contributions=amount("charitable_contributions"),
        other_secured_payments=_parse_other_secured(
            payload.get("other_secured_payments"), errors
        ),
        priority_cure_total=amount("priority_cure_total"),
        ch13_eligible=boolean(payload.get("ch13_eligible"), "ch13_eligible", errors),
        ch13_projected_plan_payment=amount("ch13_projected_plan_payment"),
    )
    for field_name in (
        "median_household_size",
        "irs_family_size",
        "irs_housing_family_size",
    ):
        size = getattr(body, field_name)
        if size is not None and size < 1:
            errors[field_name] = "A household has at least one person."
    if errors:
        raise FieldValidationError(errors)
    return body


MEANS_TEST_INPUT: EntityKind[MeansTestInputBody] = EntityKind(
    name="means_test_input",
    collection="means_test_inputs",
    sk_prefix="MEANS_TEST_INPUT",
    parse_body=parse_means_test_input,
)
