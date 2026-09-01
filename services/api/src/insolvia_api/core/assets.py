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

`detail` is one free-text field for the category-specific extras — make,
model, year and mileage for a vehicle; institution and account type for a
deposit; percentage ownership for an entity interest. One field rather than a
per-category shape, because the form itself prints one description box and a
typed sub-schema per category would be a second enum to keep in lockstep with
the first for no reader that exists yet.
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
)

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
