"""Checks over the UST means-testing registry and its loader (issue #99).

The exemptions suite's two layers: the loader against malformed releases in
tmp_path, and the committed registry loaded for real and swept. The
known-answer layer pins a handful of figures verbatim from the UST's own
tables — the medians for the launch states, the National Standards totals,
a county housing row, and the MSA/region operating split — so a conversion
bug or a bad hand-edit is a red build, not a wrong means test.
"""

import json
from datetime import date
from decimal import ROUND_HALF_UP, Decimal
from pathlib import Path

import pytest
from insolvia_api.core.dollar_amounts import latest as latest_dollar_amounts
from insolvia_api.core.ust_data import (
    CH13_MULTIPLIERS_SERIES,
    LOCAL_STANDARDS_SERIES,
    MEDIAN_INCOME_SERIES,
    NATIONAL_STANDARDS_SERIES,
    Ch13AdminMultipliers,
    LocalStandards,
    MedianIncomeTable,
    NationalStandards,
    Release,
    Verification,
    ch13_admin_multipliers,
    get,
    latest,
    load_registry,
    local_standards,
    median_income_table,
    national_standards,
    pick_release,
    registry,
    releases,
    resolve,
    series_ids,
)

AS_OF = date(2026, 9, 4)


# --- the committed registry loads, whole -------------------------------------


def test_the_registry_holds_the_four_series() -> None:
    assert series_ids() == (
        MEDIAN_INCOME_SERIES,
        CH13_MULTIPLIERS_SERIES,
        LOCAL_STANDARDS_SERIES,
        NATIONAL_STANDARDS_SERIES,
    )


def test_every_committed_release_is_verified_and_sourced() -> None:
    for series_id in series_ids():
        for release in releases(series_id):
            assert release.verification is not Verification.UNVERIFIED
            assert release.sources
            assert release.source_sha256


# --- the median table (B122A-1 line 13) ---------------------------------------


def test_the_launch_state_medians_match_the_ust_table() -> None:
    # Verbatim from the UST's 2026-04-01 median-income table; a difference
    # means a conversion bug or an uningested update.
    _, table = median_income_table(AS_OF)
    assert table.annual_medians["FL"] == (
        "69876.00",
        "86523.00",
        "97540.00",
        "114761.00",
    )
    assert table.annual_medians["GA"] == (
        "68478.00",
        "84965.00",
        "101479.00",
        "123481.00",
    )
    assert table.annual_medians["TX"] == (
        "66837.00",
        "86714.00",
        "99273.00",
        "117962.00",
    )


def test_households_above_four_add_the_excess_person_figure() -> None:
    _, table = median_income_table(AS_OF)
    four = table.annual_median("FL", 4)
    six = table.annual_median("FL", 6)
    assert six == four + 2 * Decimal(table.excess_person_annual_addition)


def test_the_excess_addition_is_twelve_times_the_statutory_monthly_addon() -> None:
    # The UST table's footnote and § 707(b)(7)(A)(iii) describe one figure;
    # the two series must agree or one of them missed an adjustment.
    _, table = median_income_table(AS_OF)
    addon = latest_dollar_amounts().amount("median-addon-per-person-safe-harbor")
    assert Decimal(table.excess_person_annual_addition) == 12 * addon.value


def test_the_median_table_covers_every_state_and_territory() -> None:
    _, table = median_income_table(AS_OF)
    assert len(table.annual_medians) == 55  # 50 states + DC + GU, MP, PR, VI
    assert {"DC", "GU", "MP", "PR", "VI"} <= set(table.annual_medians)


def test_an_unknown_jurisdiction_and_a_bad_size_are_refused() -> None:
    _, table = median_income_table(AS_OF)
    with pytest.raises(KeyError, match="no row"):
        table.annual_median("ZZ", 2)
    with pytest.raises(ValueError, match="not positive"):
        table.annual_median("FL", 0)


# --- the National Standards (B122A-2 lines 6-7) -------------------------------


def test_the_national_allowances_match_the_ust_table() -> None:
    _, standards = national_standards(AS_OF)
    assert standards.monthly_allowances == (
        "867.00",
        "1558.00",
        "1857.00",
        "2176.00",
    )
    assert standards.each_additional_person == "397.00"
    assert standards.allowance(6) == Decimal("2176.00") + 2 * Decimal("397.00")


def test_the_component_lines_sum_to_the_total() -> None:
    # The UST publishes both; internal consistency is checkable, so check it.
    _, standards = national_standards(AS_OF)
    for size in range(4):
        total = sum(Decimal(c.monthly[size]) for c in standards.components)
        assert total == Decimal(standards.monthly_allowances[size])


def test_the_additional_food_clothing_cap_is_five_percent_rounded() -> None:
    # The published "5% of Food & Clothing" row must be what its name says —
    # a broken conversion would quietly cap line 30 at the wrong figure.
    _, standards = national_standards(AS_OF)
    for size in range(4):
        five_percent = Decimal("0.05") * Decimal(standards.food_and_clothing[size])
        expected = five_percent.quantize(Decimal("1"), rounding=ROUND_HALF_UP)
        assert Decimal(standards.additional_food_clothing_cap[size]) == expected
    assert standards.additional_food_clothing_cap_for(6) == Decimal(
        standards.additional_food_clothing_cap[3]
    ) + 2 * Decimal(standards.additional_food_clothing_cap_each_additional_person)


def test_the_oop_healthcare_allowance_is_per_person_by_age_band() -> None:
    _, standards = national_standards(AS_OF)
    assert standards.oop_healthcare_under_65 == "90.00"
    assert standards.oop_healthcare_65_and_older == "163.00"
    assert standards.oop_healthcare(under_65=2, over_65=1) == Decimal("343.00")


# --- the Local Standards (B122A-2 lines 8-14) ---------------------------------


def test_the_housing_tables_cover_every_launch_state_county() -> None:
    _, standards = local_standards(AS_OF)
    assert standards.states == ("FL", "GA", "TX")
    assert len(standards.housing["FL"]) == 67
    assert len(standards.housing["GA"]) == 159
    assert len(standards.housing["TX"]) == 254


def test_a_county_row_matches_the_ust_spreadsheet() -> None:
    _, standards = local_standards(AS_OF)
    alachua = standards.housing_for("FL", "Alachua County")
    assert alachua.fips == "12001"
    assert alachua.non_mortgage_for(3) == Decimal("727.00")
    assert alachua.mortgage_rent_for(3) == Decimal("1517.00")
    # Sizes above five use the 5+ column.
    assert alachua.non_mortgage_for(9) == alachua.non_mortgage_for(5)


def test_county_lookup_tolerates_the_county_suffix() -> None:
    _, standards = local_standards(AS_OF)
    assert standards.housing_for("FL", "Alachua") == standards.housing_for(
        "fl", "Alachua County"
    )
    with pytest.raises(KeyError, match="no housing standard"):
        standards.housing_for("FL", "Nowhere")
    with pytest.raises(KeyError, match="launch states"):
        standards.housing_for("CA", "Alameda")


def test_operating_costs_use_the_msa_override_or_the_region() -> None:
    _, standards = local_standards(AS_OF)
    transportation = standards.transportation
    miami = transportation.operating_costs_for("FL", "Miami-Dade")
    region = transportation.operating_costs_for("FL", "Alachua County")
    assert miami.for_vehicles(1) == Decimal("423.00")
    assert region.for_vehicles(1) == Decimal("291.00")
    assert transportation.operating_costs_for("GA", "Fulton County").for_vehicles(
        2
    ) == Decimal("638.00")


def test_the_national_transportation_figures_match_the_ust_table() -> None:
    _, standards = local_standards(AS_OF)
    transportation = standards.transportation
    assert transportation.public_transportation_national == "220.00"
    assert transportation.ownership_costs.for_vehicles(1) == Decimal("703.00")
    assert transportation.ownership_costs.for_vehicles(2) == Decimal("1406.00")
    with pytest.raises(ValueError, match="no column"):
        transportation.ownership_costs.for_vehicles(3)


# --- the Chapter 13 multipliers (B122A-2 line 36) -----------------------------


def test_the_launch_district_multipliers_match_the_ust_table() -> None:
    _, table = ch13_admin_multipliers(AS_OF)
    assert table.multiplier_for("Middle Florida") == Decimal("0.1")
    assert table.multiplier_for("Northern Georgia") == Decimal("0.077")
    assert table.multiplier_for("Southern Texas") == Decimal("0.098")


def test_district_lookup_accepts_the_courts_spelling() -> None:
    _, table = ch13_admin_multipliers(AS_OF)
    assert table.multiplier_for("Middle District of Florida") == table.multiplier_for(
        "Middle Florida"
    )
    # DC is a name, not a pattern — the normalisation must not strip it away.
    assert table.multiplier_for("District of Columbia") == Decimal("0.091")
    with pytest.raises(KeyError, match="no district"):
        table.multiplier_for("Outer District of Nowhere")


# --- resolution semantics -----------------------------------------------------


def test_resolution_before_a_series_begins_refuses() -> None:
    with pytest.raises(LookupError, match="refusing"):
        resolve(MEDIAN_INCOME_SERIES, date(2026, 3, 31))


def test_resolve_get_and_latest_agree() -> None:
    for series_id in series_ids():
        release = resolve(series_id, AS_OF)
        assert get(series_id, release.release_id) is release
        assert latest(series_id) is release


def test_an_unknown_series_is_a_key_error() -> None:
    with pytest.raises(KeyError, match="unknown series"):
        releases("ust/nowhere")


def test_the_typed_accessors_carry_their_payload_types() -> None:
    assert isinstance(median_income_table(AS_OF)[1], MedianIncomeTable)
    assert isinstance(national_standards(AS_OF)[1], NationalStandards)
    assert isinstance(local_standards(AS_OF)[1], LocalStandards)
    assert isinstance(ch13_admin_multipliers(AS_OF)[1], Ch13AdminMultipliers)


def _release_stub(effective: date, sequence: int) -> Release:
    return Release(
        series_id=MEDIAN_INCOME_SERIES,
        effective_date=effective,
        sequence=sequence,
        source_url="https://example.gov/",
        source_published=None,
        source_sha256=None,
        notes="stub",
        verification=Verification.PRIMARY,
        sources=(),
        payload=MedianIncomeTable(
            annual_medians={"FL": ("1.00", "2.00", "3.00", "4.00")},
            excess_person_annual_addition="5.00",
        ),
    )


def test_a_correction_wins_future_resolution_by_sequence() -> None:
    original = _release_stub(date(2026, 4, 1), 1)
    correction = _release_stub(date(2026, 4, 1), 2)
    picked = pick_release((original, correction), date(2026, 6, 1))
    assert picked is correction
    assert picked.release_id == "ust/census-median-family-income@2026-04-01+2"


# --- the loader rejects malformed releases ------------------------------------


def _medians_payload() -> dict[str, object]:
    return {
        "kind": "census-median-family-income",
        "verification": "primary",
        "sources": [
            {"title": "stub", "url": "https://example.gov/", "accessed": "2026-09-04"}
        ],
        "annual_medians": {"FL": ["100.00", "200.00", "300.00", "400.00"]},
        "excess_person_annual_addition": "50.00",
    }


def _national_payload() -> dict[str, object]:
    return {
        "kind": "irs-national-standards",
        "verification": "primary",
        "sources": [
            {"title": "stub", "url": "https://example.gov/", "accessed": "2026-09-04"}
        ],
        "monthly_allowances": {"1": "10.00", "2": "20.00", "3": "30.00", "4": "40.00"},
        "each_additional_person": "5.00",
        "components": [
            {"item": "Food", "monthly": ["10.00", "20.00", "30.00", "40.00"]}
        ],
        "food_and_clothing": ["8.00", "16.00", "24.00", "32.00"],
        "food_and_clothing_each_additional_person": "4.00",
        "additional_food_clothing_cap": ["1.00", "2.00", "3.00", "4.00"],
        "additional_food_clothing_cap_each_additional_person": "1.00",
        "oop_healthcare": {"under_65": "90.00", "65_and_older": "163.00"},
    }


def _local_payload() -> dict[str, object]:
    return {
        "kind": "irs-local-standards",
        "verification": "primary",
        "sources": [
            {"title": "stub", "url": "https://example.gov/", "accessed": "2026-09-04"}
        ],
        "states": ["FL"],
        "housing_utilities": {
            "FL": [
                {
                    "fips": "12001",
                    "county": "Alachua County",
                    "non_mortgage": ["1.00", "2.00", "3.00", "4.00", "5.00"],
                    "mortgage_rent": ["1.00", "2.00", "3.00", "4.00", "5.00"],
                }
            ]
        },
        "transportation": {
            "public_transportation_national": "220.00",
            "ownership_costs": {"one_car": "703.00", "two_cars": "1406.00"},
            "operating_costs_region": {"FL": "south"},
            "operating_costs": {"south": {"one_car": "291.00", "two_cars": "582.00"}},
            "operating_costs_msa": {},
        },
    }


def _ch13_payload() -> dict[str, object]:
    return {
        "kind": "ch13-admin-multipliers",
        "verification": "primary",
        "sources": [
            {"title": "stub", "url": "https://example.gov/", "accessed": "2026-09-04"}
        ],
        "multipliers": {"Middle Florida": "0.1"},
    }


_PAYLOADS = {
    "census-median-family-income": _medians_payload,
    "irs-national-standards": _national_payload,
    "irs-local-standards": _local_payload,
    "ch13-admin-multipliers": _ch13_payload,
}


def _write_registry(
    root: Path, *, overrides: dict[str, dict[str, object]] | None = None
) -> Path:
    for series_id in (
        MEDIAN_INCOME_SERIES,
        NATIONAL_STANDARDS_SERIES,
        LOCAL_STANDARDS_SERIES,
        CH13_MULTIPLIERS_SERIES,
    ):
        name = series_id.removeprefix("ust/")
        release_dir = root / "ust" / name / "2026-04-01"
        release_dir.mkdir(parents=True)
        manifest = {
            "series_id": series_id,
            "effective_date": "2026-04-01",
            "sequence": 1,
            "source": {
                "url": "https://example.gov/",
                "published": None,
                "sha256": "ab" * 32,
            },
            "notes": "a well-formed stub release",
        }
        payload = _PAYLOADS[name]()
        payload.update((overrides or {}).get(name, {}))
        (release_dir / "manifest.json").write_text(
            json.dumps(manifest), encoding="utf-8"
        )
        (release_dir / "dataset.json").write_text(json.dumps(payload), encoding="utf-8")
    return root


def test_the_loader_accepts_a_well_formed_registry(tmp_path: Path) -> None:
    loaded = load_registry(_write_registry(tmp_path))
    assert set(loaded) == set(series_ids())
    release = loaded[MEDIAN_INCOME_SERIES][0]
    assert isinstance(release.payload, MedianIncomeTable)


def test_a_missing_series_is_a_packaging_failure(tmp_path: Path) -> None:
    root = _write_registry(tmp_path)
    for path in sorted((root / "ust" / "irs-national-standards").rglob("*")):
        if path.is_file():
            path.unlink()
    (root / "ust" / "irs-national-standards" / "2026-04-01").rmdir()
    (root / "ust" / "irs-national-standards").rmdir()
    with pytest.raises(ValueError, match="missing series"):
        load_registry(root)


@pytest.mark.parametrize(
    ("series", "overrides", "problem"),
    [
        ("census-median-family-income", {"kind": "wrong"}, "dataset kind"),
        ("census-median-family-income", {"verification": "unverified"}, "unverified"),
        (
            "census-median-family-income",
            {"annual_medians": {"Florida": ["1.00", "2.00", "3.00", "4.00"]}},
            "not a state code",
        ),
        (
            "census-median-family-income",
            {"annual_medians": {"FL": ["1.00", "2.00", "3.00"]}},
            "list of 4",
        ),
        (
            "irs-national-standards",
            {"monthly_allowances": {"1": "10.00"}},
            "sizes 1-4",
        ),
        (
            "irs-local-standards",
            {"housing_utilities": {"GA": []}},
            "exactly the listed states",
        ),
        (
            "irs-local-standards",
            {
                "transportation": {
                    "public_transportation_national": "220.00",
                    "ownership_costs": {"one_car": "703.00", "two_cars": "1406.00"},
                    "operating_costs_region": {"FL": "west"},
                    "operating_costs": {
                        "south": {"one_car": "291.00", "two_cars": "582.00"}
                    },
                    "operating_costs_msa": {},
                }
            },
            "missing a region",
        ),
    ],
)
def test_the_loader_rejects_a_malformed_payload(
    tmp_path: Path, series: str, overrides: dict[str, object], problem: str
) -> None:
    root = _write_registry(tmp_path, overrides={series: overrides})
    with pytest.raises(ValueError, match=problem):
        load_registry(root)


def test_the_loaded_registry_is_what_the_module_serves() -> None:
    # The cached module-level registry and a fresh load agree — a cheap guard
    # against the cache serving anything but the committed files.
    assert set(registry()) == set(series_ids())
