"""B106A/B @ 2015-12-01 (revision 12/15) — Schedule A/B's mapping.

The schedule is the asset collection projected onto the 106A/B line set:
`asset.category` picks the line, creation order picks the row. Three row
shapes exist and the tables below drive them:

- **Part 1-2 rows** (real estate, vehicles) print the full column set —
  ownership, community property, both value boxes.
- **Named-row lines** (deposits, retirement accounts, insurance…) print one
  small row per asset; overflow past the printed rows is an error.
- **Single-box lines** (household goods, tax refunds…) print ONE
  description box and one amount however many assets the category holds, so
  the projection aggregates: descriptions joined, amounts summed. The
  category-specific `detail` is appended to the description text so a fact
  with no box of its own still lands on the page.

Two mappings the model cannot serve yet stay blank, deliberately: a
vehicle's make/model/year/mileage boxes (the spec maps all four to the one
free-text `detail`, which cannot be split back apart — the whole text lands
in the row's "Other information" box instead), and line 43's PII question
(nothing structured records it). Line 28/29's amount columns route by
keyword over `detail` ("federal", "alimony", …) — the spec's own note.
"""

from __future__ import annotations

from collections.abc import Sequence
from decimal import Decimal
from typing import Final

from ..assets import AssetBody
from ..form_fill import Check, Text
from ..form_templates import FormRelease
from .shared import (
    CaseFile,
    FieldValues,
    FormProjectionError,
    amount,
    canonical_option,
    format_money,
    full_name,
    row_fill,
    text_or_none,
    yes_no,
)

# Part 1's "check all that apply" — stored member -> spec field suffix.
_PROPERTY_TYPE_BOXES: Final = {
    "single_family_home": "type_single_family_home",
    "duplex_or_multi_unit": "type_duplex_or_multi_unit_building",
    "condominium_or_cooperative": "type_condominium_or_cooperative",
    "manufactured_or_mobile_home": "type_manufactured_or_mobile_home",
    "land": "type_land",
    "investment_property": "type_investment_property",
    "timeshare": "type_timeshare",
    "other": "type_other",
}

# The single-box lines: line number -> (category, has a description box).
# Line 16 (cash) prints an amount box only.
_SINGLE_BOX_LINES: Final[tuple[tuple[str, str, bool], ...]] = (
    ("6", "household_goods", True),
    ("7", "electronics", True),
    ("8", "collectibles", True),
    ("9", "sports_and_hobby_equipment", True),
    ("10", "firearms", True),
    ("11", "clothes", True),
    ("12", "jewelry", True),
    ("13", "non_farm_animals", True),
    ("14", "other_personal_or_household", True),
    ("16", "cash", False),
    ("25", "trusts_and_future_interests", True),
    ("26", "intellectual_property", True),
    ("27", "licenses_and_franchises", True),
    ("30", "other_amounts_owed", True),
    ("32", "property_due_from_a_death", True),
    ("33", "claims_against_third_parties", True),
    ("34", "other_contingent_and_unliquidated_claims", True),
    ("35", "other_financial_assets", True),
    ("38", "accounts_receivable", True),
    ("39", "office_equipment", True),
    ("40", "machinery_and_tools_of_trade", True),
    ("41", "inventory", True),
    ("43", "customer_lists_and_intangibles", True),
    ("47", "farm_animals", True),
    ("48", "crops", True),
    ("49", "farm_and_fishing_equipment", True),
    ("50", "farm_and_fishing_supplies", True),
    ("51", "other_farm_property", True),
)

# The named-row lines: line -> (category, (field id, asset attribute)...).
# Each asset takes the next printed row; the amount column is always
# value_portion_owned.
_ROW_LINES: Final[tuple[tuple[str, str, tuple[tuple[str, str], ...]], ...]] = (
    ("17", "deposits_of_money", (("line_17_institution", "detail"),)),
    ("18", "bonds_and_mutual_funds", (("line_18_institution", "detail"),)),
    (
        "19",
        "non_publicly_traded_stock_and_business_interests",
        (("line_19_entity", "description"), ("line_19_ownership_pct", "detail")),
    ),
    ("20", "government_and_corporate_bonds", (("line_20_issuer", "detail"),)),
    ("21", "retirement_accounts", (("line_21_institution", "detail"),)),
    ("22", "security_deposits_and_prepayments", (("line_22_institution", "detail"),)),
    ("23", "annuities", (("line_23_issuer", "description"),)),
    ("24", "education_accounts", (("line_24_institution", "description"),)),
    (
        "31",
        "insurance_policy_interests",
        (("line_31_company", "description"), ("line_31_beneficiary", "detail")),
    ),
    (
        "42",
        "partnership_and_joint_venture_interests",
        (("line_42_entity", "description"), ("line_42_ownership_pct", "detail")),
    ),
    ("44", "other_business_property", (("line_44_description", "description"),)),
)

_ROW_AMOUNT_FIELDS: Final = {
    "17": "line_17_amount",
    "18": "line_18_amount",
    "19": "line_19_amount",
    "20": "line_20_amount",
    "21": "line_21_amount",
    "22": "line_22_amount",
    "23": "line_23_amount",
    "24": "line_24_amount",
    "31": "line_31_value",
    "42": "line_42_amount",
    "44": "line_44_amount",
}

# Line 28/29's amount columns, routed by keyword over `detail` (the spec's
# "detail records federal"). Ordered so the longest match wins.
_LINE_28_COLUMNS: Final = (
    ("federal", "line_28_amount_federal"),
    ("state", "line_28_amount_state"),
    ("local", "line_28_amount_local"),
)
_LINE_29_COLUMNS: Final = (
    ("property settlement", "line_29_amount_property_settlement"),
    ("divorce", "line_29_amount_divorce_settlement"),
    ("alimony", "line_29_amount_alimony"),
    ("maintenance", "line_29_amount_maintenance"),
    ("support", "line_29_amount_support"),
)

# The seven parts' category sets, for the part gates and subtotals.
_PART_2: Final = ("vehicle", "watercraft_aircraft_or_recreational_vehicle")
_PART_3: Final = tuple(c for _, c, _d in _SINGLE_BOX_LINES[:9])
_PART_4: Final = (
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
)
_PART_5: Final = (
    "accounts_receivable",
    "office_equipment",
    "machinery_and_tools_of_trade",
    "inventory",
    "partnership_and_joint_venture_interests",
    "customer_lists_and_intangibles",
    "other_business_property",
)
_PART_6: Final = (
    "farm_animals",
    "crops",
    "farm_and_fishing_equipment",
    "farm_and_fishing_supplies",
    "other_farm_property",
)


def _in(case_file: CaseFile, *categories: str) -> list[AssetBody]:
    wanted = set(categories)
    return [body for _, body in case_file.assets if body.category in wanted]


def _portion_total(assets: Sequence[AssetBody]) -> Decimal:
    return sum((amount(a.value_portion_owned) for a in assets), Decimal("0"))


def _entry_text(asset: AssetBody) -> str | None:
    """Description plus the category-specific detail, for the lines that
    print one box — nothing stored goes missing from the page."""
    parts = [p for p in (asset.description, asset.detail) if p]
    return "; ".join(parts) or None


def real_estate_total(case_file: CaseFile) -> Decimal:
    """Line 55 — the Part 1 total. Shared with 106Sum line 1a."""
    return _portion_total(_in(case_file, "real_property"))


def personal_property_total(case_file: CaseFile) -> Decimal:
    """Line 62 — the sum of the Part 2-7 totals. Shared with 106Sum line 1b."""
    return _portion_total(
        [body for _, body in case_file.assets if body.category != "real_property"]
    )


def _part_1(
    release: FormRelease,
    case_file: CaseFile,
    values: FieldValues,
    problems: list[str],
) -> None:
    estates = _in(case_file, "real_property")
    values["line_1_any_real_estate"] = yes_no(
        release, "line_1_any_real_estate", bool(estates)
    )
    for index, asset in enumerate(estates):
        # The street box's own label allows "or other description".
        row_fill(
            release,
            values,
            "real_estate.street",
            index,
            text_or_none(asset.description),
            problems,
        )
        row_fill(
            release,
            values,
            "real_estate.county",
            index,
            text_or_none(asset.county),
            problems,
        )
        for member in asset.property_types:
            row_fill(
                release,
                values,
                f"real_estate.{_PROPERTY_TYPE_BOXES[member]}",
                index,
                Check(),
                problems,
            )
        _shared_columns(release, values, "real_estate", index, asset, problems)
        row_fill(
            release,
            values,
            "real_estate.ownership_nature",
            index,
            text_or_none(asset.ownership_interest_description),
            problems,
        )
        row_fill(
            release,
            values,
            "real_estate.other_information",
            index,
            text_or_none(asset.detail),
            problems,
        )
    values["line_2_part1_total"] = Text(format_money(_portion_total(estates)))


def _shared_columns(
    release: FormRelease,
    values: FieldValues,
    prefix: str,
    index: int,
    asset: AssetBody,
    problems: list[str],
) -> None:
    """The columns Parts 1 and 2 share: ownership, community property, and
    the two value boxes."""
    if asset.ownership_interest is not None:
        field_id = f"{prefix}.who_has_interest"
        spec = release.field(field_id)
        if index >= len(spec.pdf_names):
            problems.append(
                f"{field_id}: row {index + 1} does not exist — the form "
                f"prints {len(spec.pdf_names)} rows"
            )
        else:
            row_fill(
                release,
                values,
                field_id,
                index,
                canonical_option(
                    release, field_id, spec.pdf_names[index], asset.ownership_interest
                ),
                problems,
            )
    if asset.community_property:
        row_fill(
            release, values, f"{prefix}.community_property", index, Check(), problems
        )
    row_fill(
        release,
        values,
        f"{prefix}.value_entire",
        index,
        text_or_none(asset.value_entire and format_money(asset.value_entire)),
        problems,
    )
    row_fill(
        release,
        values,
        f"{prefix}.value_portion",
        index,
        text_or_none(
            asset.value_portion_owned and format_money(asset.value_portion_owned)
        ),
        problems,
    )


def _part_2(
    release: FormRelease,
    case_file: CaseFile,
    values: FieldValues,
    problems: list[str],
) -> None:
    for gate, prefix, category in (
        ("line_3_any_vehicles", "vehicle", "vehicle"),
        (
            "line_4_any_other_vehicles",
            "other_vehicle",
            "watercraft_aircraft_or_recreational_vehicle",
        ),
    ):
        assets = _in(case_file, category)
        values[gate] = yes_no(release, gate, bool(assets))
        for index, asset in enumerate(assets):
            # Make/model/year/mileage stay blank — see the module docstring;
            # the whole free-text lands in the Other information box.
            row_fill(
                release,
                values,
                f"{prefix}.other_information",
                index,
                text_or_none(_entry_text(asset)),
                problems,
            )
            _shared_columns(release, values, prefix, index, asset, problems)
    values["line_5_part2_total"] = Text(
        format_money(_portion_total(_in(case_file, *_PART_2)))
    )


def _single_box_lines(
    release: FormRelease,
    case_file: CaseFile,
    values: FieldValues,
) -> None:
    for line, category, has_description in _SINGLE_BOX_LINES:
        assets = _in(case_file, category)
        values[f"line_{line}_gate"] = yes_no(release, f"line_{line}_gate", bool(assets))
        if not assets:
            continue
        if has_description:
            texts = [text for a in assets if (text := _entry_text(a))]
            if texts:
                values[f"line_{line}_description"] = Text("; ".join(texts))
        values[f"line_{line}_amount"] = Text(format_money(_portion_total(assets)))


def _keyword_lines(
    release: FormRelease,
    case_file: CaseFile,
    values: FieldValues,
    problems: list[str],
) -> None:
    for line, category, columns in (
        ("28", "money_owed_to_you", _LINE_28_COLUMNS),
        ("29", "family_support_owed", _LINE_29_COLUMNS),
    ):
        assets = _in(case_file, category)
        values[f"line_{line}_gate"] = yes_no(release, f"line_{line}_gate", bool(assets))
        if not assets:
            continue
        texts = [text for a in assets if (text := _entry_text(a))]
        if texts:
            values[f"line_{line}_description"] = Text("; ".join(texts))
        totals: dict[str, Decimal] = {}
        for asset in assets:
            if asset.value_portion_owned is None:
                continue
            detail = (asset.detail or "").lower()
            box = next((field for keyword, field in columns if keyword in detail), None)
            if box is None:
                problems.append(
                    f"line_{line}: the amount columns route by keyword over "
                    f"the asset's detail ({', '.join(k for k, _ in columns)}); "
                    f"{asset.detail!r} names none of them"
                )
                continue
            totals[box] = totals.get(box, Decimal("0")) + amount(
                asset.value_portion_owned
            )
        for box, total in totals.items():
            values[box] = Text(format_money(total))


def _row_lines(
    release: FormRelease,
    case_file: CaseFile,
    values: FieldValues,
    problems: list[str],
) -> None:
    for line, category, columns in _ROW_LINES:
        assets = _in(case_file, category)
        values[f"line_{line}_gate"] = yes_no(release, f"line_{line}_gate", bool(assets))
        for index, asset in enumerate(assets):
            for field_id, attr in columns:
                stored = getattr(asset, attr)
                row_fill(
                    release,
                    values,
                    field_id,
                    index,
                    text_or_none(stored),
                    problems,
                )
            row_fill(
                release,
                values,
                _ROW_AMOUNT_FIELDS[line],
                index,
                text_or_none(
                    asset.value_portion_owned
                    and format_money(asset.value_portion_owned)
                ),
                problems,
            )
    # Line 43's PII question has no structured fact to answer it — blank.

    # Line 53: one description box, three amount rows.
    others = _in(case_file, "other_property_not_listed")
    values["line_53_gate"] = yes_no(release, "line_53_gate", bool(others))
    if others:
        texts = [text for a in others if (text := _entry_text(a))]
        if texts:
            values["line_53_description"] = Text("; ".join(texts))
        for index, asset in enumerate(others):
            row_fill(
                release,
                values,
                "line_53_amount",
                index,
                text_or_none(
                    asset.value_portion_owned
                    and format_money(asset.value_portion_owned)
                ),
                problems,
            )


def _totals(release: FormRelease, case_file: CaseFile, values: FieldValues) -> None:
    part_totals = {
        "line_15_part3_total": _portion_total(_in(case_file, *_PART_3)),
        "line_36_part4_total": _portion_total(_in(case_file, *_PART_4)),
        "line_45_part5_total": _portion_total(_in(case_file, *_PART_5)),
        "line_52_part6_total": _portion_total(_in(case_file, *_PART_6)),
        "line_54_part7_total": _portion_total(
            _in(case_file, "other_property_not_listed")
        ),
    }
    real_estate = real_estate_total(case_file)
    part_2 = _portion_total(_in(case_file, *_PART_2))
    for field_id, total in part_totals.items():
        values[field_id] = Text(format_money(total))
    values["line_55_total"] = Text(format_money(real_estate))
    values["line_56_total"] = Text(format_money(part_2))
    values["line_57_total"] = Text(format_money(part_totals["line_15_part3_total"]))
    values["line_58_total"] = Text(format_money(part_totals["line_36_part4_total"]))
    values["line_59_total"] = Text(format_money(part_totals["line_45_part5_total"]))
    values["line_60_total"] = Text(format_money(part_totals["line_52_part6_total"]))
    values["line_61_total"] = Text(format_money(part_totals["line_54_part7_total"]))
    personal = personal_property_total(case_file)
    values["line_62_total_personal_property"] = Text(format_money(personal))
    values["line_63_total_all_property"] = Text(format_money(real_estate + personal))


def project_b106ab_1215(release: FormRelease, case_file: CaseFile) -> FieldValues:
    problems: list[str] = []
    values: FieldValues = {}

    values["caption.district"] = Text(case_file.case.district)
    for role, field_id in (
        ("debtor_1", "caption.debtor1_name"),
        ("debtor_2", "caption.debtor2_name"),
    ):
        debtor = case_file.debtor(role)
        if debtor is not None and (name := full_name(debtor.name)):
            values[field_id] = Text(name)

    _part_1(release, case_file, values, problems)
    _part_2(release, case_file, values, problems)
    _single_box_lines(release, case_file, values)
    _keyword_lines(release, case_file, values, problems)
    _row_lines(release, case_file, values, problems)

    # The part gates that ask about a whole part at once.
    values["line_37_gate"] = yes_no(
        release, "line_37_gate", bool(_in(case_file, *_PART_5))
    )
    values["line_46_gate"] = yes_no(
        release, "line_46_gate", bool(_in(case_file, *_PART_6))
    )

    _totals(release, case_file, values)

    if problems:
        raise FormProjectionError(sorted(problems))
    return values
