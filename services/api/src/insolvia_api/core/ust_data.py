"""The UST means-testing datasets: loader and resolution for the `ust/*` series.

The means test computes against three datasets the U.S. Trustee Program
republishes from the Census Bureau and the IRS (issue #99; the regulatory
source register rates this row high-risk — "everything downstream"):

    ust/census-median-family-income   § 707(b)(7)'s median table: annual median
                                      family income by state/territory and
                                      household size (B122A-1 line 13)
    ust/irs-national-standards        National Standards for food, clothing and
                                      other items plus the out-of-pocket health
                                      care allowance (B122A-2 lines 6-7)
    ust/irs-local-standards           Local Standards: housing/utilities by
                                      county and the transportation tables
                                      (B122A-2 lines 8-14)

Each release is a committed directory under `src/insolvia_api/regulatory/`
per the regulatory release registry model (docs/reference/effective-dating.md;
ADR 0014), written by `scripts/ingest-ust-data.py` from the UST's own XLSX
artifacts (sha256 pinned in the manifest) and reviewed as a pull request —
the diff is the review surface. `scripts/check-ust-data.py` is the scheduled
tripwire that alerts a human when the UST posts a period these series have
not ingested.

The local-standards payload is scoped to the launch states (ADR 0017), like
the exemptions registry: nothing downstream computes for any other state, and
a wider launch set arrives as a new release. The medians table keeps every
state and territory — it is one small table and the UST publishes it whole.

Resolution follows effective-dating.md exactly, as the other registry
consumers' does: `resolve_*` picks the release effective on the case's filing
date, `get_*` returns a pinned release forever, and resolution before a
series' earliest release refuses rather than guessing. The means-test engine
records the release ids it computed from, so every figure in its output
traces to a dated dataset.

Stdlib only; reading the registry shipped inside this package is
configuration access, not an external dependency.
"""

from __future__ import annotations

import json
import re
from collections.abc import Mapping
from dataclasses import dataclass
from datetime import date
from decimal import Decimal
from functools import cache
from importlib import resources
from importlib.resources.abc import Traversable
from typing import TypeVar

from .exemptions import Source, Verification

MEDIAN_INCOME_SERIES = "ust/census-median-family-income"
NATIONAL_STANDARDS_SERIES = "ust/irs-national-standards"
LOCAL_STANDARDS_SERIES = "ust/irs-local-standards"

_SERIES_PAYLOADS = {
    MEDIAN_INCOME_SERIES: "census-median-family-income",
    NATIONAL_STANDARDS_SERIES: "irs-national-standards",
    LOCAL_STANDARDS_SERIES: "irs-local-standards",
}

_MONEY_RE = re.compile(r"^[1-9]\d*\.\d{2}$")
_DIRNAME_RE = re.compile(r"^(\d{4}-\d{2}-\d{2})(?:\+(\d+))?$")
_STATE_RE = re.compile(r"^[A-Z]{2}$")
_FIPS_RE = re.compile(r"^\d{5}$")


@dataclass(frozen=True)
class MedianIncomeTable:
    """Annual median family income by state/territory for household sizes
    1-4; a household above 4 adds the excess-person figure per person (the
    UST table's own footnote — 12x the § 707(b)(7)(A)(iii) monthly amount
    that code/dollar-amounts carries)."""

    annual_medians: Mapping[str, tuple[str, str, str, str]]
    excess_person_annual_addition: str

    def annual_median(self, state: str, household_size: int) -> Decimal:
        """The applicable median — B122A-1 line 13's lookup. Raises KeyError
        for a jurisdiction the table does not carry and ValueError for a
        nonsensical household size; callers surface both, never guess."""
        if household_size < 1:
            raise ValueError(f"household size {household_size} is not positive")
        row = self.annual_medians.get(state.upper())
        if row is None:
            raise KeyError(f"the median income table has no row for {state!r}")
        if household_size <= 4:
            return Decimal(row[household_size - 1])
        excess = Decimal(self.excess_person_annual_addition)
        return Decimal(row[3]) + excess * (household_size - 4)


@dataclass(frozen=True)
class NationalStandardComponent:
    """One published component line (Food, Housekeeping supplies, ...) of the
    National Standards total, kept as the UST prints it."""

    item: str
    monthly: tuple[str, str, str, str]


@dataclass(frozen=True)
class NationalStandards:
    """B122A-2 lines 6-7's tables: the food-clothing-and-other-items total by
    household size, and the per-person out-of-pocket health care allowance."""

    monthly_allowances: tuple[str, str, str, str]
    each_additional_person: str
    components: tuple[NationalStandardComponent, ...]
    # The published food-and-clothing subtotal and its 5% cap — B122A-2 line
    # 30's optional additional food and clothing expense — each with the
    # UST's per-person figure for households above four.
    food_and_clothing: tuple[str, str, str, str]
    food_and_clothing_each_additional_person: str
    additional_food_clothing_cap: tuple[str, str, str, str]
    additional_food_clothing_cap_each_additional_person: str
    oop_healthcare_under_65: str
    oop_healthcare_65_and_older: str

    def allowance(self, household_size: int) -> Decimal:
        """The line 6 monthly allowance for a household of this size."""
        if household_size < 1:
            raise ValueError(f"household size {household_size} is not positive")
        if household_size <= 4:
            return Decimal(self.monthly_allowances[household_size - 1])
        extra = Decimal(self.each_additional_person)
        return Decimal(self.monthly_allowances[3]) + extra * (household_size - 4)

    def additional_food_clothing_cap_for(self, household_size: int) -> Decimal:
        """Line 30's cap for this household: the published table for sizes
        1-4, plus the UST's per-person addition above four."""
        if household_size < 1:
            raise ValueError(f"household size {household_size} is not positive")
        if household_size <= 4:
            return Decimal(self.additional_food_clothing_cap[household_size - 1])
        extra = Decimal(self.additional_food_clothing_cap_each_additional_person)
        return Decimal(self.additional_food_clothing_cap[3]) + extra * (
            household_size - 4
        )

    def oop_healthcare(self, *, under_65: int, over_65: int) -> Decimal:
        """The line 7 monthly allowance: per-person amounts by age band."""
        if under_65 < 0 or over_65 < 0:
            raise ValueError("household member counts cannot be negative")
        return (
            Decimal(self.oop_healthcare_under_65) * under_65
            + Decimal(self.oop_healthcare_65_and_older) * over_65
        )


@dataclass(frozen=True)
class CountyHousing:
    """One county's housing and utilities allowances, household sizes
    1,2,3,4,5+: `non_mortgage` is the insurance-and-operating half (B122A-2
    line 8), `mortgage_rent` the mortgage-or-rent half (line 9a)."""

    fips: str
    county: str
    non_mortgage: tuple[str, str, str, str, str]
    mortgage_rent: tuple[str, str, str, str, str]

    def _pick(self, values: tuple[str, ...], household_size: int) -> Decimal:
        if household_size < 1:
            raise ValueError(f"household size {household_size} is not positive")
        return Decimal(values[min(household_size, 5) - 1])

    def non_mortgage_for(self, household_size: int) -> Decimal:
        return self._pick(self.non_mortgage, household_size)

    def mortgage_rent_for(self, household_size: int) -> Decimal:
        return self._pick(self.mortgage_rent, household_size)


@dataclass(frozen=True)
class VehicleCosts:
    one_car: str
    two_cars: str

    def for_vehicles(self, vehicle_count: int) -> Decimal:
        """The published column for one or two vehicles — the forms allow at
        most two (B122A-2 lines 13a/13b are per-vehicle rows capped at two
        vehicles; the operating table has no third column)."""
        if vehicle_count == 1:
            return Decimal(self.one_car)
        if vehicle_count == 2:
            return Decimal(self.two_cars)
        raise ValueError(f"the table has no column for {vehicle_count} vehicles")


@dataclass(frozen=True)
class MsaOperatingCosts(VehicleCosts):
    state: str = ""
    counties: tuple[str, ...] = ()


@dataclass(frozen=True)
class Transportation:
    """The transportation tables (B122A-2 lines 11-14): a national public
    transportation figure, national per-vehicle ownership costs, and
    operating costs by Census region with MSA overrides. Which MSA a debtor
    falls in is a county question — the UST publishes the county lists only
    on its per-region HTML pages, curated into the release at ingestion."""

    public_transportation_national: str
    ownership_costs: VehicleCosts
    operating_costs_region: Mapping[str, str]
    operating_costs: Mapping[str, VehicleCosts]
    operating_costs_msa: tuple[MsaOperatingCosts, ...]

    def operating_costs_for(self, state: str, county: str) -> VehicleCosts:
        """The operating-costs row for a debtor's county: the MSA override
        when the county is on an MSA's list, the state's Census region
        otherwise. County matching ignores a trailing ' County' so stored
        county names land either way."""
        normalized = _normalize_county(county)
        for msa in self.operating_costs_msa:
            if msa.state == state.upper() and any(
                _normalize_county(name) == normalized for name in msa.counties
            ):
                return msa
        region = self.operating_costs_region.get(state.upper())
        if region is None:
            raise KeyError(f"no Census region is recorded for state {state!r}")
        return self.operating_costs[region]


def _normalize_county(name: str) -> str:
    return name.strip().removesuffix(" County").strip().lower()


@dataclass(frozen=True)
class LocalStandards:
    """The Local Standards payload, scoped to the launch states."""

    states: tuple[str, ...]
    housing: Mapping[str, tuple[CountyHousing, ...]]
    transportation: Transportation

    def housing_for(self, state: str, county: str) -> CountyHousing:
        """The county row B122A-2 lines 8-9 read. Raises KeyError with the
        state named when the county is not in the table — a typo'd county
        must surface, never fall back to some other county's allowance."""
        rows = self.housing.get(state.upper())
        if rows is None:
            raise KeyError(
                f"the local standards carry no housing table for {state!r} "
                f"(launch states: {', '.join(self.states)})"
            )
        normalized = _normalize_county(county)
        found = next(
            (row for row in rows if _normalize_county(row.county) == normalized),
            None,
        )
        if found is None:
            raise KeyError(f"{state}: no housing standard for county {county!r}")
        return found


PayloadT = TypeVar("PayloadT", MedianIncomeTable, NationalStandards, LocalStandards)


@dataclass(frozen=True)
class Release:
    """One immutable release of a UST series (effective-dating.md)."""

    series_id: str
    effective_date: date
    sequence: int
    source_url: str
    source_published: date | None
    source_sha256: str | None
    notes: str
    verification: Verification
    sources: tuple[Source, ...]
    payload: MedianIncomeTable | NationalStandards | LocalStandards

    @property
    def release_id(self) -> str:
        base = f"{self.series_id}@{self.effective_date.isoformat()}"
        return base if self.sequence == 1 else f"{base}+{self.sequence}"


# --- Loading and validation --------------------------------------------------


def _fail(where: str, problem: str) -> ValueError:
    return ValueError(f"malformed release {where}: {problem}")


def _str_field(data: Mapping[str, object], key: str, where: str) -> str:
    value = data.get(key)
    if not isinstance(value, str) or not value.strip():
        raise _fail(where, f"{key} missing or empty")
    return value


def _date_or_none(value: object, where: str, field_name: str) -> date | None:
    if value is None:
        return None
    if not isinstance(value, str):
        raise _fail(where, f"{field_name} {value!r} is not a date string")
    try:
        return date.fromisoformat(value)
    except ValueError as exc:
        raise _fail(where, f"{field_name} {value!r}: {exc}") from exc


def _sources(value: object, where: str) -> tuple[Source, ...]:
    if not isinstance(value, list) or not value:
        raise _fail(where, "sources missing or empty")
    parsed: list[Source] = []
    for raw in value:
        if not isinstance(raw, dict):
            raise _fail(where, "source is not an object")
        accessed = _date_or_none(raw.get("accessed"), where, "source.accessed")
        if accessed is None:
            raise _fail(where, "source.accessed missing")
        parsed.append(
            Source(
                title=_str_field(raw, "title", where),
                url=_str_field(raw, "url", where),
                accessed=accessed,
            )
        )
    return tuple(parsed)


def _money(value: object, where: str, field_name: str) -> str:
    if not isinstance(value, str) or not _MONEY_RE.match(value):
        raise _fail(where, f"{field_name} {value!r} is not a two-decimal money string")
    return value


def _money_row(
    value: object, where: str, field_name: str, count: int
) -> tuple[str, ...]:
    if not isinstance(value, list) or len(value) != count:
        raise _fail(where, f"{field_name} must be a list of {count} money strings")
    return tuple(_money(v, where, f"{field_name}[{i}]") for i, v in enumerate(value))


def _vehicle_costs(value: object, where: str, field_name: str) -> VehicleCosts:
    if not isinstance(value, dict):
        raise _fail(where, f"{field_name} must be an object")
    return VehicleCosts(
        one_car=_money(value.get("one_car"), where, f"{field_name}.one_car"),
        two_cars=_money(value.get("two_cars"), where, f"{field_name}.two_cars"),
    )


def _medians_payload(payload: Mapping[str, object], where: str) -> MedianIncomeTable:
    raw = payload.get("annual_medians")
    if not isinstance(raw, dict) or not raw:
        raise _fail(where, "annual_medians missing or empty")
    medians: dict[str, tuple[str, str, str, str]] = {}
    for state, row in raw.items():
        if not _STATE_RE.match(state):
            raise _fail(where, f"annual_medians key {state!r} is not a state code")
        four = _money_row(row, where, f"annual_medians.{state}", 4)
        medians[state] = (four[0], four[1], four[2], four[3])
    return MedianIncomeTable(
        annual_medians=medians,
        excess_person_annual_addition=_money(
            payload.get("excess_person_annual_addition"),
            where,
            "excess_person_annual_addition",
        ),
    )


def _national_payload(payload: Mapping[str, object], where: str) -> NationalStandards:
    raw_allowances = payload.get("monthly_allowances")
    if not isinstance(raw_allowances, dict) or set(raw_allowances) != {
        "1",
        "2",
        "3",
        "4",
    }:
        raise _fail(where, "monthly_allowances must carry sizes 1-4 exactly")
    allowances = tuple(
        _money(raw_allowances[size], where, f"monthly_allowances.{size}")
        for size in ("1", "2", "3", "4")
    )
    raw_components = payload.get("components")
    if not isinstance(raw_components, list) or not raw_components:
        raise _fail(where, "components missing or empty")
    components: list[NationalStandardComponent] = []
    for raw in raw_components:
        if not isinstance(raw, dict):
            raise _fail(where, "component is not an object")
        item = _str_field(raw, "item", where)
        row = _money_row(raw.get("monthly"), where, f"component {item}", 4)
        components.append(
            NationalStandardComponent(
                item=item, monthly=(row[0], row[1], row[2], row[3])
            )
        )
    oop = payload.get("oop_healthcare")
    if not isinstance(oop, dict):
        raise _fail(where, "oop_healthcare missing")
    food_and_clothing = _money_row(
        payload.get("food_and_clothing"), where, "food_and_clothing", 4
    )
    five_percent = _money_row(
        payload.get("additional_food_clothing_cap"),
        where,
        "additional_food_clothing_cap",
        4,
    )
    return NationalStandards(
        monthly_allowances=(
            allowances[0],
            allowances[1],
            allowances[2],
            allowances[3],
        ),
        each_additional_person=_money(
            payload.get("each_additional_person"), where, "each_additional_person"
        ),
        components=tuple(components),
        food_and_clothing=(
            food_and_clothing[0],
            food_and_clothing[1],
            food_and_clothing[2],
            food_and_clothing[3],
        ),
        food_and_clothing_each_additional_person=_money(
            payload.get("food_and_clothing_each_additional_person"),
            where,
            "food_and_clothing_each_additional_person",
        ),
        additional_food_clothing_cap=(
            five_percent[0],
            five_percent[1],
            five_percent[2],
            five_percent[3],
        ),
        additional_food_clothing_cap_each_additional_person=_money(
            payload.get("additional_food_clothing_cap_each_additional_person"),
            where,
            "additional_food_clothing_cap_each_additional_person",
        ),
        oop_healthcare_under_65=_money(
            oop.get("under_65"), where, "oop_healthcare.under_65"
        ),
        oop_healthcare_65_and_older=_money(
            oop.get("65_and_older"), where, "oop_healthcare.65_and_older"
        ),
    )


def _local_payload(payload: Mapping[str, object], where: str) -> LocalStandards:
    raw_states = payload.get("states")
    if (
        not isinstance(raw_states, list)
        or not raw_states
        or not all(isinstance(s, str) and _STATE_RE.match(s) for s in raw_states)
    ):
        raise _fail(where, "states must be a non-empty list of state codes")
    states = tuple(raw_states)

    raw_housing = payload.get("housing_utilities")
    if not isinstance(raw_housing, dict) or set(raw_housing) != set(states):
        raise _fail(where, "housing_utilities must carry exactly the listed states")
    housing: dict[str, tuple[CountyHousing, ...]] = {}
    for state, raw_rows in raw_housing.items():
        if not isinstance(raw_rows, list) or not raw_rows:
            raise _fail(where, f"housing_utilities.{state} missing or empty")
        rows: list[CountyHousing] = []
        for raw in raw_rows:
            if not isinstance(raw, dict):
                raise _fail(where, f"housing_utilities.{state} row is not an object")
            county = _str_field(raw, "county", where)
            fips = _str_field(raw, "fips", where)
            if not _FIPS_RE.match(fips):
                raise _fail(where, f"{state} {county}: fips {fips!r} is not 5 digits")
            non_mortgage = _money_row(
                raw.get("non_mortgage"), where, f"{state} {county} non_mortgage", 5
            )
            mortgage_rent = _money_row(
                raw.get("mortgage_rent"), where, f"{state} {county} mortgage_rent", 5
            )
            rows.append(
                CountyHousing(
                    fips=fips,
                    county=county,
                    non_mortgage=(
                        non_mortgage[0],
                        non_mortgage[1],
                        non_mortgage[2],
                        non_mortgage[3],
                        non_mortgage[4],
                    ),
                    mortgage_rent=(
                        mortgage_rent[0],
                        mortgage_rent[1],
                        mortgage_rent[2],
                        mortgage_rent[3],
                        mortgage_rent[4],
                    ),
                )
            )
        counties = [_normalize_county(row.county) for row in rows]
        if len(counties) != len(set(counties)):
            raise _fail(where, f"housing_utilities.{state} has duplicate counties")
        housing[state] = tuple(rows)

    raw_transport = payload.get("transportation")
    if not isinstance(raw_transport, dict):
        raise _fail(where, "transportation missing")
    raw_region_map = raw_transport.get("operating_costs_region")
    if not isinstance(raw_region_map, dict) or set(raw_region_map) != set(states):
        raise _fail(
            where, "operating_costs_region must carry exactly the listed states"
        )
    region_map = {
        state: _str_field(raw_region_map, state, where) for state in raw_region_map
    }
    raw_regions = raw_transport.get("operating_costs")
    if not isinstance(raw_regions, dict) or not (
        set(region_map.values()) <= set(raw_regions)
    ):
        raise _fail(where, "operating_costs missing a region the map names")
    regions = {
        name: _vehicle_costs(raw_costs, where, f"operating_costs.{name}")
        for name, raw_costs in raw_regions.items()
    }
    raw_msas = raw_transport.get("operating_costs_msa")
    if not isinstance(raw_msas, dict):
        raise _fail(where, "operating_costs_msa must be an object")
    msas: list[MsaOperatingCosts] = []
    for name, raw_msa in raw_msas.items():
        if not isinstance(raw_msa, dict):
            raise _fail(where, f"operating_costs_msa.{name} is not an object")
        state = _str_field(raw_msa, "state", where)
        if state not in states:
            raise _fail(where, f"MSA {name} names state {state!r}, not in states")
        raw_counties = raw_msa.get("counties")
        if not isinstance(raw_counties, list) or not all(
            isinstance(c, str) and c for c in raw_counties
        ):
            raise _fail(where, f"MSA {name}: counties must be a list of names")
        msas.append(
            MsaOperatingCosts(
                one_car=_money(raw_msa.get("one_car"), where, f"MSA {name} one_car"),
                two_cars=_money(raw_msa.get("two_cars"), where, f"MSA {name} two_cars"),
                state=state,
                counties=tuple(raw_counties),
            )
        )

    transportation = Transportation(
        public_transportation_national=_money(
            raw_transport.get("public_transportation_national"),
            where,
            "public_transportation_national",
        ),
        ownership_costs=_vehicle_costs(
            raw_transport.get("ownership_costs"), where, "ownership_costs"
        ),
        operating_costs_region=region_map,
        operating_costs=regions,
        operating_costs_msa=tuple(msas),
    )
    return LocalStandards(states=states, housing=housing, transportation=transportation)


def _load_json(node: Traversable, where: str) -> dict[str, object]:
    try:
        parsed = json.loads(node.read_text(encoding="utf-8"))
    except (FileNotFoundError, ValueError) as exc:
        raise _fail(where, f"{node.name}: {exc}") from exc
    if not isinstance(parsed, dict):
        raise _fail(where, f"{node.name} is not a JSON object")
    return parsed


def _load_release(release_dir: Traversable, series_id: str) -> Release:
    where = f"{series_id}/{release_dir.name}"
    match = _DIRNAME_RE.match(release_dir.name)
    if match is None:
        raise _fail(where, "directory name is not <effective_date>[+<sequence>]")
    effective = date.fromisoformat(match.group(1))
    sequence = int(match.group(2)) if match.group(2) else 1
    if sequence < 1:
        raise _fail(where, "sequence must be >= 1")

    manifest = _load_json(release_dir.joinpath("manifest.json"), where)
    if manifest.get("series_id") != series_id:
        raise _fail(where, f"manifest series_id {manifest.get('series_id')!r}")
    if manifest.get("effective_date") != effective.isoformat():
        raise _fail(where, "manifest effective_date disagrees with the path")
    if manifest.get("sequence") != sequence:
        raise _fail(where, "manifest sequence disagrees with the path")
    source = manifest.get("source")
    if not isinstance(source, dict):
        raise _fail(where, "manifest source missing")
    sha256 = source.get("sha256")
    if sha256 is not None and not isinstance(sha256, str):
        raise _fail(where, "source.sha256 must be a string or null")
    manifest_notes = manifest.get("notes")
    if not isinstance(manifest_notes, str) or not manifest_notes.strip():
        raise _fail(where, "manifest notes missing — say what this release is")

    payload_raw = _load_json(release_dir.joinpath("dataset.json"), where)
    if payload_raw.get("kind") != _SERIES_PAYLOADS[series_id]:
        raise _fail(where, f"dataset kind {payload_raw.get('kind')!r}")
    try:
        verification = Verification(payload_raw.get("verification"))
    except ValueError as exc:
        raise _fail(where, str(exc)) from exc
    if verification is Verification.UNVERIFIED:
        raise _fail(where, "a means-testing dataset may never be unverified")

    payload: MedianIncomeTable | NationalStandards | LocalStandards
    if series_id == MEDIAN_INCOME_SERIES:
        payload = _medians_payload(payload_raw, where)
    elif series_id == NATIONAL_STANDARDS_SERIES:
        payload = _national_payload(payload_raw, where)
    else:
        payload = _local_payload(payload_raw, where)

    return Release(
        series_id=series_id,
        effective_date=effective,
        sequence=sequence,
        source_url=_str_field(source, "url", where),
        source_published=_date_or_none(
            source.get("published"), where, "source.published"
        ),
        source_sha256=sha256,
        notes=manifest_notes,
        verification=verification,
        sources=_sources(payload_raw.get("sources"), where),
        payload=payload,
    )


def load_registry(root: Traversable) -> dict[str, tuple[Release, ...]]:
    """Load and validate every UST release under a registry root.

    Raises ValueError on any malformed release — the loader-in-CI rule the
    registry model demands, run by tests/test_ust_data.py so a bad release
    fails the pull request, not a filing. Every series must exist: the means
    test needs all three, so a missing one is a packaging failure to catch
    here rather than at resolution time.
    """
    ust_dir = root.joinpath("ust")
    series: dict[str, tuple[Release, ...]] = {}
    for series_dir in sorted(ust_dir.iterdir(), key=lambda n: n.name):
        if not series_dir.is_dir():
            continue
        series_id = f"ust/{series_dir.name}"
        if series_id not in _SERIES_PAYLOADS:
            raise _fail(series_id, "unknown UST series")
        loaded = [
            _load_release(release_dir, series_id)
            for release_dir in sorted(series_dir.iterdir(), key=lambda n: n.name)
            if release_dir.is_dir()
        ]
        if not loaded:
            raise _fail(series_id, "series has no releases")
        loaded.sort(key=lambda r: (r.effective_date, r.sequence))
        ids = [r.release_id for r in loaded]
        if len(ids) != len(set(ids)):
            raise _fail(series_id, "duplicate release ids")
        series[series_id] = tuple(loaded)
    missing = sorted(set(_SERIES_PAYLOADS) - set(series))
    if missing:
        raise ValueError(f"the UST registry is missing series: {missing}")
    return series


@cache
def registry() -> dict[str, tuple[Release, ...]]:
    """The committed registry, shipped inside this package (ADR 0014)."""
    return load_registry(resources.files("insolvia_api").joinpath("regulatory"))


# --- Resolution (effective-dating.md) ----------------------------------------


def series_ids() -> tuple[str, ...]:
    return tuple(sorted(registry()))


def releases(series_id: str) -> tuple[Release, ...]:
    found = registry().get(series_id)
    if found is None:
        raise KeyError(f"unknown series {series_id!r}")
    return found


def pick_release(candidates: tuple[Release, ...], as_of: date) -> Release:
    """The release with the greatest effective_date <= as_of, ties broken by
    highest sequence. Pure over its inputs, so correction tie-breaks are
    testable without fixture directories."""
    applicable = [r for r in candidates if r.effective_date <= as_of]
    if not applicable:
        earliest = min(r.effective_date for r in candidates)
        raise LookupError(
            f"no release of {candidates[0].series_id} is effective on or "
            f"before {as_of.isoformat()} (series begins {earliest.isoformat()}); "
            "refusing to compute on data that does not describe that date"
        )
    return max(applicable, key=lambda r: (r.effective_date, r.sequence))


def resolve(series_id: str, as_of: date) -> Release:
    """The release in force on `as_of` — the case's filing date while a case
    floats, its pinned assembly date afterwards."""
    return pick_release(releases(series_id), as_of)


def get(series_id: str, release_id: str) -> Release:
    """That exact release — must succeed forever for any id ever pinned."""
    for release in releases(series_id):
        if release.release_id == release_id:
            return release
    raise KeyError(f"series {series_id!r} has no release {release_id!r}")


def latest(series_id: str) -> Release:
    """Newest by (effective_date, sequence), even if still in the future."""
    return releases(series_id)[-1]


def _payload(release: Release, expected: type[PayloadT]) -> tuple[Release, PayloadT]:
    if not isinstance(release.payload, expected):  # pragma: no cover - loader rule
        raise TypeError(f"{release.release_id} does not carry {expected.__name__}")
    return release, release.payload


def median_income_table(as_of: date) -> tuple[Release, MedianIncomeTable]:
    """The median table in force on `as_of`, with its release for the pin."""
    return _payload(resolve(MEDIAN_INCOME_SERIES, as_of), MedianIncomeTable)


def national_standards(as_of: date) -> tuple[Release, NationalStandards]:
    """The National Standards in force on `as_of`, with their release."""
    return _payload(resolve(NATIONAL_STANDARDS_SERIES, as_of), NationalStandards)


def local_standards(as_of: date) -> tuple[Release, LocalStandards]:
    """The Local Standards in force on `as_of`, with their release."""
    return _payload(resolve(LOCAL_STANDARDS_SERIES, as_of), LocalStandards)
