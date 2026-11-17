"""The asset record — 106A/B, one row per item of property.

One entity across all seven parts of the schedule, discriminated by
`category` — the 106A/B line set, named rather than numbered so a form
revision that renumbers the lines does not silently change stored meanings
(the same argument as the counseling statuses in core/debtors.py).

`value_entire` and `value_portion_owned` are BOTH stored and neither is
derivable from the other: a half-owned house has no fixed relationship between
them once liens and tenancy are involved (docs/reference/case-data-model.md).
The seven part subtotals and the Part 8 rollup are arithmetic and never
stored.

`detail` is one free-text field for the category-specific extras that stay
free text — institution and account type for a deposit, percentage ownership
for an entity interest, and a vehicle's "Other information" box once its own
boxes are filled. One field rather than a per-category shape, because the
form itself mostly prints one description box and a typed sub-schema per
category would be a second enum to keep in lockstep with the first for no
reader that exists yet.

`year`/`make`/`model`/`mileage` are the one exception (issue 13.3 / #344):
106A/B Part 2 prints a vehicle's year, make, model and mileage as their own
boxes (`vehicle.year` etc. in forms/specs/b106ab.json), not one free-text
run, and `detail` cannot be split back apart into four fields once it has
been typed as one. They apply to both vehicle categories — a car's mileage
box and a boat's do not both exist on the form, so `mileage` simply goes
unprinted for a watercraft/aircraft/RV row (`form_projections/b106ab.py`
skips the box for that prefix, it is not that the field refuses the value).

`includes_personal_information` answers line 43's own sub-question — whether
a customer/mailing list carries personally identifiable information as
§ 101(41A) defines it — for `customer_lists_and_intangibles` assets. It is a
separate boolean rather than folded into `detail` for the same reason: the
form prints it as its own Yes/No box (`line_43_pii_gate`), not text.
"""

from __future__ import annotations

from collections.abc import Mapping
from dataclasses import dataclass
from typing import Final

from insolvia_core.errors import FieldValidationError

from .case_entities import EntityKind
from .fields import (
    DEBTOR_ATTRIBUTION,
    boolean,
    choice,
    choice_list,
    money,
    narrative,
    text,
    whole_number,
)

# A vehicle's year is a small bounded integer, like a count — no model year
# on a filing is before the automobile or more than a couple of years past
# the current one, and `whole_number`'s shape-only contract (no such thing as
# "too far in the future" here) matches the storage-validation rule the rest
# of this module follows: shape and type only, completeness is the forms
# engine's job.
_MAX_VEHICLE_YEAR: Final = 2100
# Odometers roll well past six digits on an old car; this is a sanity cap,
# not a real-world limit.
_MAX_MILEAGE: Final = 999_999

# The 106A/B line set, part by part. Named for what the line asks about.
ASSET_CATEGORIES: Final = (
    # Part 1: real property
    "real_property",
    # Part 2: vehicles
    "vehicle",
    "watercraft_aircraft_or_recreational_vehicle",
    # Part 3: personal and household items
    "household_goods",
    "electronics",
    "collectibles",
    "sports_and_hobby_equipment",
    "firearms",
    "clothes",
    "jewelry",
    "non_farm_animals",
    "other_personal_or_household",
    # Part 4: financial assets
    "cash",
    "deposits_of_money",
    "bonds_and_mutual_funds",
    "non_publicly_traded_stock_and_business_interests",
    "government_and_corporate_bonds",
    "retirement_accounts",
    "security_deposits_and_prepayments",
    "annuities",
    "education_accounts",
    "trusts_and_future_interests",
    "intellectual_property",
    "licenses_and_franchises",
    "money_owed_to_you",
    "family_support_owed",
    "other_amounts_owed",
    "insurance_policy_interests",
    "property_due_from_a_death",
    "claims_against_third_parties",
    "other_contingent_and_unliquidated_claims",
    "other_financial_assets",
    # Part 5: business-related property
    "accounts_receivable",
    "office_equipment",
    "machinery_and_tools_of_trade",
    "inventory",
    "partnership_and_joint_venture_interests",
    "customer_lists_and_intangibles",
    "other_business_property",
    # Part 6: farm- and fishing-related property
    "farm_animals",
    "crops",
    "farm_and_fishing_equipment",
    "farm_and_fishing_supplies",
    "other_farm_property",
    # Part 7
    "other_property_not_listed",
)

# Part 1's "check all that apply" for real property.
PROPERTY_TYPES: Final = (
    "single_family_home",
    "duplex_or_multi_unit",
    "condominium_or_cooperative",
    "manufactured_or_mobile_home",
    "land",
    "investment_property",
    "timeshare",
    "other",
)


@dataclass(frozen=True)
class AssetBody:
    category: str | None = None
    property_types: tuple[str, ...] = ()
    description: str | None = None
    county: str | None = None
    value_entire: str | None = None
    value_portion_owned: str | None = None
    ownership_interest: str | None = None
    ownership_interest_description: str | None = None
    community_property: bool | None = None
    detail: str | None = None
    # Vehicle-only (both vehicle categories) — see the module docstring.
    year: int | None = None
    make: str | None = None
    model: str | None = None
    mileage: int | None = None
    # `customer_lists_and_intangibles` only — line 43's § 101(41A) question.
    includes_personal_information: bool | None = None


def parse_asset(payload: Mapping[str, object]) -> AssetBody:
    errors: dict[str, str] = {}
    body = AssetBody(
        category=choice(payload.get("category"), ASSET_CATEGORIES, "category", errors),
        property_types=choice_list(
            payload.get("property_types"), PROPERTY_TYPES, "property_types", errors
        ),
        description=narrative(payload.get("description"), "description", errors),
        county=text(payload.get("county"), "county", errors),
        value_entire=money(payload.get("value_entire"), "value_entire", errors),
        value_portion_owned=money(
            payload.get("value_portion_owned"), "value_portion_owned", errors
        ),
        ownership_interest=choice(
            payload.get("ownership_interest"),
            DEBTOR_ATTRIBUTION,
            "ownership_interest",
            errors,
        ),
        ownership_interest_description=text(
            payload.get("ownership_interest_description"),
            "ownership_interest_description",
            errors,
            limit=500,
        ),
        community_property=boolean(
            payload.get("community_property"), "community_property", errors
        ),
        detail=narrative(payload.get("detail"), "detail", errors),
        year=whole_number(
            payload.get("year"), "year", errors, maximum=_MAX_VEHICLE_YEAR
        ),
        make=text(payload.get("make"), "make", errors, limit=60),
        model=text(payload.get("model"), "model", errors, limit=60),
        mileage=whole_number(
            payload.get("mileage"), "mileage", errors, maximum=_MAX_MILEAGE
        ),
        includes_personal_information=boolean(
            payload.get("includes_personal_information"),
            "includes_personal_information",
            errors,
        ),
    )
    if errors:
        raise FieldValidationError(errors)
    return body


ASSET: EntityKind[AssetBody] = EntityKind(
    name="asset",
    collection="assets",
    sk_prefix="ASSET",
    parse_body=parse_asset,
)
