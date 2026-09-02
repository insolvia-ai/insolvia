"""Checks over the exemptions registry and its loader (issue #95).

Two layers, per effective-dating.md's demand that the registry's first
consumer ship a loader that validates it in CI:

- the loader itself is exercised against malformed releases built in
  tmp_path, so a bad manifest or figure is a loud ValueError;
- the committed registry is loaded for real and swept with consistency
  checks — malformed money, a wildcard pointing at a missing homestead, a
  dollar amount nobody verified. These cannot prove a statute says what the
  dataset says (that is the per-entry sources' job), but they make every
  internal inconsistency a red build.
"""

import json
from datetime import date
from decimal import Decimal
from pathlib import Path

import pytest
from insolvia_api.core.exemptions import (
    FEDERAL_SERIES,
    LAUNCH_STATES,
    NEXT_FEDERAL_ADJUSTMENT,
    Category,
    ExemptionEntry,
    ExemptionScheme,
    Release,
    Verification,
    federal_limits,
    get,
    latest,
    load_registry,
    pick_release,
    registry,
    releases,
    resolve,
    schemes_for_state,
    series_ids,
)

ALL_RELEASES: list[Release] = [
    release for series in registry().values() for release in series
]
ALL_ENTRIES: list[tuple[Release, ExemptionEntry]] = [
    (release, entry) for release in ALL_RELEASES for entry in release.scheme.entries
]


def _entry_params() -> list[object]:
    return [
        pytest.param(release, entry, id=f"{release.release_id}:{entry.entry_id}")
        for release, entry in ALL_ENTRIES
    ]


def _money_fields(entry: ExemptionEntry) -> list[str]:
    return [
        m
        for m in (
            entry.amount,
            entry.per_item_amount,
            entry.joint_amount,
            entry.wildcard_carryover_cap,
        )
        if m is not None
    ]


# --- the committed registry loads, whole -------------------------------------


def test_the_launch_registry_holds_the_four_series() -> None:
    assert series_ids() == (
        "exemptions/federal",
        "exemptions/fl",
        "exemptions/ga",
        "exemptions/tx",
    )


def test_entry_ids_are_unique_within_each_release() -> None:
    for release in ALL_RELEASES:
        ids = [e.entry_id for e in release.scheme.entries]
        assert len(ids) == len(set(ids)), release.release_id


def test_release_ids_resolve_by_get_forever() -> None:
    for release in ALL_RELEASES:
        assert get(release.series_id, release.release_id) is release


# --- money -------------------------------------------------------------------


@pytest.mark.parametrize(("release", "entry"), _entry_params())
def test_money_is_two_decimal_positive_strings(
    release: Release, entry: ExemptionEntry
) -> None:
    for money in _money_fields(entry):
        integral, _, fraction = money.partition(".")
        assert integral.isdigit(), money
        assert len(fraction) == 2, money
        assert fraction.isdigit(), money
        assert Decimal(money) > 0


@pytest.mark.parametrize(("release", "entry"), _entry_params())
def test_unlimited_and_amount_are_mutually_exclusive(
    release: Release, entry: ExemptionEntry
) -> None:
    if entry.unlimited:
        assert entry.amount is None
        assert entry.per_item_amount is None


@pytest.mark.parametrize(("release", "entry"), _entry_params())
def test_a_missing_cap_is_always_explained(
    release: Release, entry: ExemptionEntry
) -> None:
    # amount None means "no flat dollar cap" — either genuinely unlimited or
    # conditional, and a conditional cap must say its condition in notes.
    if entry.amount is None and not entry.unlimited:
        assert entry.notes, entry.entry_id


# --- verification and sources -------------------------------------------------


@pytest.mark.parametrize(("release", "entry"), _entry_params())
def test_every_entry_carries_a_citation_and_sources(
    release: Release, entry: ExemptionEntry
) -> None:
    assert entry.citation.strip()
    assert entry.description.strip()
    assert entry.sources
    for source in entry.sources:
        assert source.url.startswith("https://"), source.url
        assert source.accessed <= date(2026, 9, 1)


@pytest.mark.parametrize(("release", "entry"), _entry_params())
def test_no_dollar_figure_is_unverified(
    release: Release, entry: ExemptionEntry
) -> None:
    # A wrong dollar amount lands on a signed federal filing. An UNVERIFIED
    # entry may exist only for uncapped exemptions, and must explain itself.
    if entry.verification is Verification.UNVERIFIED:
        assert not _money_fields(entry), entry.entry_id
        assert "UNVERIFIED" in entry.notes, entry.entry_id


@pytest.mark.parametrize(("release", "entry"), _entry_params())
def test_a_figure_since_never_postdates_its_release(
    release: Release, entry: ExemptionEntry
) -> None:
    if entry.figure_since is not None:
        assert entry.figure_since <= release.effective_date, entry.entry_id


# --- wildcard carryover links -------------------------------------------------


@pytest.mark.parametrize(("release", "entry"), _entry_params())
def test_carryover_link_and_cap_travel_together(
    release: Release, entry: ExemptionEntry
) -> None:
    assert (entry.wildcard_carryover_from is None) == (
        entry.wildcard_carryover_cap is None
    )


@pytest.mark.parametrize(("release", "entry"), _entry_params())
def test_carryover_points_at_a_homestead_in_the_same_scheme(
    release: Release, entry: ExemptionEntry
) -> None:
    if entry.wildcard_carryover_from is None:
        return
    by_id = {e.entry_id: e for e in release.scheme.entries}
    target = by_id.get(entry.wildcard_carryover_from)
    assert target is not None, entry.wildcard_carryover_from
    assert target.category is Category.HOMESTEAD


# --- the federal series -------------------------------------------------------


def test_federal_dollar_entries_carry_the_2025_adjustment_date() -> None:
    release = resolve(FEDERAL_SERIES, date(2026, 9, 1))
    assert release.effective_date == date(2025, 4, 1)
    for entry in release.scheme.entries:
        if entry.amount is not None or entry.per_item_amount is not None:
            assert entry.figure_since == date(2025, 4, 1), entry.entry_id


def test_the_next_federal_adjustment_is_scheduled() -> None:
    assert date(2028, 4, 1) == NEXT_FEDERAL_ADJUSTMENT
    for limit in federal_limits(date(2026, 9, 1)):
        assert limit.next_adjustment == NEXT_FEDERAL_ADJUSTMENT
        assert limit.verification is not Verification.UNVERIFIED


def test_only_the_federal_series_carries_the_522_caps() -> None:
    for release in ALL_RELEASES:
        if release.series_id == FEDERAL_SERIES:
            assert {li.limit_id for li in release.limits} == {
                "us-lien-avoidance-tools",
                "us-lien-avoidance-household",
                "us-ira-cap",
                "us-homestead-1215-day-cap",
                "us-homestead-misconduct-cap",
            }
        else:
            assert release.limits == ()


def test_the_federal_scheme_has_no_opt_out_axis() -> None:
    scheme = resolve(FEDERAL_SERIES, date(2026, 9, 1)).scheme
    assert scheme.opted_out_of_federal is None
    assert scheme.opt_out_citation is None


# --- scheme election (106C line 1) --------------------------------------------


@pytest.mark.parametrize("state", ["FL", "GA"])
def test_opt_out_states_offer_only_their_own_scheme(state: str) -> None:
    schemes = schemes_for_state(state, date(2026, 9, 1))
    assert len(schemes) == 1
    assert schemes[0].jurisdiction == state
    assert schemes[0].opt_out_citation


def test_texas_offers_the_federal_election() -> None:
    schemes = schemes_for_state("TX", date(2026, 9, 1))
    assert [s.jurisdiction for s in schemes] == ["TX", "US"]


def test_state_lookup_is_case_insensitive() -> None:
    as_of = date(2026, 9, 1)
    assert schemes_for_state("fl", as_of) == schemes_for_state("FL", as_of)


def test_an_unsupported_state_is_refused_not_guessed() -> None:
    with pytest.raises(KeyError, match="launch set"):
        schemes_for_state("CA", date(2026, 9, 1))


def test_every_launch_state_has_a_scheme() -> None:
    for state in LAUNCH_STATES:
        assert schemes_for_state(state, date(2026, 9, 1))


# --- resolution semantics -----------------------------------------------------


def test_georgia_resolves_to_the_hb1024_figures() -> None:
    scheme = resolve("exemptions/ga", date(2026, 9, 1)).scheme
    homesteads = [e for e in scheme.entries if e.category is Category.HOMESTEAD]
    assert [e.amount for e in homesteads] == ["50000.00"]
    assert [e.joint_amount for e in homesteads] == ["100000.00"]


def test_resolution_before_a_series_begins_refuses() -> None:
    # No fallback past the beginning: the GA snapshot describes H.B. 1024
    # law and must not answer for a case filed before it took effect.
    with pytest.raises(LookupError, match="refusing"):
        resolve("exemptions/ga", date(2026, 6, 30))


def test_an_unknown_series_is_a_key_error() -> None:
    with pytest.raises(KeyError, match="unknown series"):
        releases("exemptions/nowhere")


def test_latest_returns_the_newest_release_even_if_future() -> None:
    for series_id in series_ids():
        newest = latest(series_id)
        assert newest is releases(series_id)[-1]


def _release_stub(effective: date, sequence: int) -> Release:
    scheme = ExemptionScheme(
        scheme_id="stub",
        jurisdiction="US",
        name="stub",
        opted_out_of_federal=None,
        opt_out_citation=None,
        entries=(),
    )
    return Release(
        series_id="exemptions/stub",
        effective_date=effective,
        sequence=sequence,
        source_url="https://example.gov/",
        source_published=None,
        source_sha256=None,
        notes="stub",
        scheme=scheme,
        limits=(),
    )


def test_a_correction_wins_future_resolution_by_sequence() -> None:
    original = _release_stub(date(2025, 4, 1), 1)
    correction = _release_stub(date(2025, 4, 1), 2)
    picked = pick_release((original, correction), date(2025, 6, 1))
    assert picked is correction
    assert picked.release_id == "exemptions/stub@2025-04-01+2"


def test_resolution_picks_the_greatest_effective_date_not_the_newest() -> None:
    older = _release_stub(date(2025, 4, 1), 1)
    newer = _release_stub(date(2028, 4, 1), 1)
    assert pick_release((older, newer), date(2027, 1, 1)) is older


# --- the loader rejects malformed releases ------------------------------------


def _write_release(
    root: Path,
    *,
    series: str = "stub",
    dirname: str = "2025-04-01",
    manifest_overrides: dict[str, object] | None = None,
    entry_overrides: dict[str, object] | None = None,
) -> Path:
    release_dir = root / "exemptions" / series / dirname
    release_dir.mkdir(parents=True)
    manifest: dict[str, object] = {
        "series_id": f"exemptions/{series}",
        "effective_date": "2025-04-01",
        "sequence": 1,
        "source": {"url": "https://example.gov/", "published": None, "sha256": None},
        "notes": "a well-formed stub release",
    }
    manifest.update(manifest_overrides or {})
    entry: dict[str, object] = {
        "entry_id": "stub-homestead",
        "category": "homestead",
        "description": "stub",
        "citation": "11 U.S.C. § 522(d)(1)",
        "amount": "31575.00",
        "unlimited": False,
        "verification": "primary",
        "sources": [
            {"title": "stub", "url": "https://example.gov/", "accessed": "2026-09-01"}
        ],
    }
    entry.update(entry_overrides or {})
    payload = {
        "scheme_id": "stub",
        "jurisdiction": "US",
        "name": "stub scheme",
        "opted_out_of_federal": None,
        "opt_out_citation": None,
        "notes": "",
        "entries": [entry],
        "limits": [],
    }
    (release_dir / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
    (release_dir / "scheme.json").write_text(json.dumps(payload), encoding="utf-8")
    return root


def test_the_loader_accepts_a_well_formed_release(tmp_path: Path) -> None:
    loaded = load_registry(_write_release(tmp_path))
    assert loaded["exemptions/stub"][0].release_id == "exemptions/stub@2025-04-01"


@pytest.mark.parametrize(
    ("manifest_overrides", "problem"),
    [
        ({"series_id": "exemptions/other"}, "series_id"),
        ({"effective_date": "2025-05-01"}, "disagrees with the path"),
        ({"sequence": 2}, "sequence disagrees"),
        ({"notes": ""}, "notes missing"),
        ({"source": None}, "source missing"),
    ],
)
def test_the_loader_rejects_a_manifest_that_lies_about_its_path(
    tmp_path: Path, manifest_overrides: dict[str, object], problem: str
) -> None:
    root = _write_release(tmp_path, manifest_overrides=manifest_overrides)
    with pytest.raises(ValueError, match=problem):
        load_registry(root)


@pytest.mark.parametrize(
    ("entry_overrides", "problem"),
    [
        ({"amount": "1,000.00"}, "money"),
        ({"amount": "1000.0"}, "money"),
        ({"amount": 1000}, "money"),
        ({"category": "yacht"}, "yacht"),
        ({"verification": "vibes"}, "vibes"),
        ({"sources": []}, "sources"),
        ({"citation": ""}, "citation"),
    ],
)
def test_the_loader_rejects_a_malformed_figure(
    tmp_path: Path, entry_overrides: dict[str, object], problem: str
) -> None:
    root = _write_release(tmp_path, entry_overrides=entry_overrides)
    with pytest.raises(ValueError, match=problem):
        load_registry(root)


def test_the_loader_rejects_a_misnamed_release_directory(tmp_path: Path) -> None:
    root = _write_release(tmp_path, dirname="april-2025")
    with pytest.raises(ValueError, match="effective_date"):
        load_registry(root)
