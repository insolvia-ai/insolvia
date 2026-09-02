"""Households, expenses and dependents — the 106J family.

106J's roughly thirty expense lines are ROWS, not columns, keyed by a
`category` enum with an optional `specify_text`
(docs/reference/case-data-model.md): 106J-2 repeats the entire line set for a
second household, and a column model would make that a schema change instead
of a second `household` row. So a `household` is the axis both `expense` and
`dependent` hang off, via `household_id` — unchecked here, the usual
progressive-intake rule.

`which_household` names which of the form's two schedules a household row IS:
`main` for 106J, `debtor_2_separate` for 106J-2. Two rows with the same value
are a fact the completeness gate flags, not a write this layer refuses — the
same reasoning as income_summary's one-per-debtor.

`dependent` records relationship, age and residence only. The form does not
ask for dependents' names, SO WE DO NOT STORE THEM — do not add a name field;
it would be collecting a child's PII no form prints.

Total expenses, the 106J-2 carry-forward and net monthly income are arithmetic
and never stored.
"""

from __future__ import annotations

from collections.abc import Mapping
from dataclasses import dataclass
from typing import Final

from insolvia_core.errors import FieldValidationError

from .case_entities import EntityKind
from .fields import boolean, choice, money, narrative, text, whole_number

WHICH_HOUSEHOLDS: Final = ("main", "debtor_2_separate")

# The 106J line set, named for what the line asks about rather than numbered —
# the annual form cycle renumbers, and a stored number would silently change
# meaning. `other` and the insurance/installment "specify" lines carry their
# text in `specify_text`.
EXPENSE_CATEGORIES: Final = (
    "rent_or_home_ownership",
    "real_estate_taxes",
    "property_insurance",
    "home_maintenance",
    "homeowners_association_dues",
    "additional_mortgage_payments",
    "electricity_heat_gas",
    "water_sewer_garbage",
    "telephone_and_internet",
    "other_utilities",
    "food_and_housekeeping",
    "childcare_and_education",
    "clothing_and_laundry",
    "personal_care",
    "medical_and_dental",
    "transportation",
    "entertainment_and_recreation",
    "charitable_contributions",
    "life_insurance",
    "health_insurance",
    "vehicle_insurance",
    "other_insurance",
    "taxes",
    "vehicle_installment_payments",
    "other_installment_payments",
    "alimony_and_support_payments",
    "support_of_others",
    "other_property_mortgages",
    "other_property_taxes",
    "other_property_insurance",
    "other_property_maintenance",
    "other_property_association_dues",
    "other",
)


@dataclass(frozen=True)
class HouseholdBody:
    which_household: str | None = None
    # 106J line 1: does debtor 2 live in a separate household?
    separate_household: bool | None = None
    # Line 24: increase or decrease expected within the year, and why.
    change_expected: bool | None = None
    change_explanation: str | None = None


def parse_household(payload: Mapping[str, object]) -> HouseholdBody:
    errors: dict[str, str] = {}
    body = HouseholdBody(
        which_household=choice(
            payload.get("which_household"), WHICH_HOUSEHOLDS, "which_household", errors
        ),
        separate_household=boolean(
            payload.get("separate_household"), "separate_household", errors
        ),
        change_expected=boolean(
            payload.get("change_expected"), "change_expected", errors
        ),
        change_explanation=narrative(
            payload.get("change_explanation"), "change_explanation", errors
        ),
    )
    if errors:
        raise FieldValidationError(errors)
    return body


HOUSEHOLD: EntityKind[HouseholdBody] = EntityKind(
    name="household",
    collection="households",
    sk_prefix="HOUSEHOLD",
    parse_body=parse_household,
)


@dataclass(frozen=True)
class ExpenseBody:
    household_id: str | None = None
    category: str | None = None
    specify_text: str | None = None
    amount: str | None = None


def parse_expense(payload: Mapping[str, object]) -> ExpenseBody:
    errors: dict[str, str] = {}
    body = ExpenseBody(
        household_id=text(
            payload.get("household_id"), "household_id", errors, limit=64
        ),
        category=choice(
            payload.get("category"), EXPENSE_CATEGORIES, "category", errors
        ),
        specify_text=text(payload.get("specify_text"), "specify_text", errors),
        amount=money(payload.get("amount"), "amount", errors),
    )
    if errors:
        raise FieldValidationError(errors)
    return body


EXPENSE: EntityKind[ExpenseBody] = EntityKind(
    name="expense",
    collection="expenses",
    sk_prefix="EXPENSE",
    parse_body=parse_expense,
)


@dataclass(frozen=True)
class DependentBody:
    household_id: str | None = None
    relationship: str | None = None
    age: int | None = None
    lives_with_debtor: bool | None = None


def parse_dependent(payload: Mapping[str, object]) -> DependentBody:
    errors: dict[str, str] = {}
    if "name" in payload and payload["name"] is not None:
        # Refused rather than ignored, like the debtor's tax_id: silently
        # dropping it would leave the client believing a name was stored.
        errors["name"] = (
            "Dependents' names are not stored — the form does not ask for them."
        )
    body = DependentBody(
        household_id=text(
            payload.get("household_id"), "household_id", errors, limit=64
        ),
        relationship=text(payload.get("relationship"), "relationship", errors),
        age=whole_number(payload.get("age"), "age", errors, maximum=150),
        lives_with_debtor=boolean(
            payload.get("lives_with_debtor"), "lives_with_debtor", errors
        ),
    )
    if errors:
        raise FieldValidationError(errors)
    return body


DEPENDENT: EntityKind[DependentBody] = EntityKind(
    name="dependent",
    collection="dependents",
    sk_prefix="DEPENDENT",
    parse_body=parse_dependent,
)
