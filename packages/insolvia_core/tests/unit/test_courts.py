"""The court registry (issue #360): the loader over the committed release,
and the invariants a district record must hold.

Runs the real loader over the real registry — this is the CI validation
ADR 0014 asks of a series' first consumer, so a malformed or half-cited
record fails a pull request rather than a filing. The launch set is ADR
0017's ten districts; the field list is ADR 0024's.
"""

from __future__ import annotations

import json
import urllib.parse
from datetime import date
from pathlib import Path

import pytest
from insolvia_core import courts

LAUNCH_DISTRICTS = {
    "flnb",
    "flmb",
    "flsb",
    "txnb",
    "txsb",
    "txeb",
    "txwb",
    "ganb",
    "gamb",
    "gasb",
}

# The B101 dropdown's own option strings (forms/acroform/b101.json) — what
# every form caption prints, and what `case.district` is derived as.
B101_NAMES = {
    "Northern District of Florida",
    "Middle District of Florida",
    "Southern District of Florida",
    "Northern District of Texas",
    "Southern District of Texas",
    "Eastern District of Texas",
    "Western District of Texas",
    "Northern District of Georgia",
    "Middle District of Georgia",
    "Southern District of Georgia",
}


@pytest.fixture(scope="module")
def release() -> courts.CourtsRelease:
    return courts.latest()


def test_the_registry_holds_exactly_the_launch_districts(release):
    assert {d.code for d in release.districts} == LAUNCH_DISTRICTS
    assert {d.name for d in release.districts} == B101_NAMES


def test_every_district_has_a_pacer_court_id(release):
    for district in release.districts:
        assert district.court_id == district.code.upper() + "K", district.code


def test_every_county_of_a_district_is_in_exactly_one_division(release):
    for district in release.districts:
        seen: set[str] = set()
        for division in district.divisions:
            for county in division.counties:
                assert county.fips not in seen, (district.code, county)
                seen.add(county.fips)
        assert seen, district.code


@pytest.mark.parametrize(
    ("code", "expected"),
    [("flnb", 23), ("flmb", 35), ("flsb", 9), ("txnb", 100), ("txsb", 43), ("txeb", 43),
     ("txwb", 68), ("ganb", 46), ("gamb", 70), ("gasb", 43)],
)  # fmt: skip
def test_county_counts_match_the_venue_statutes(release, code, expected):
    # 28 U.S.C. §§ 89, 90, 124 — a dropped county is a debtor with no venue.
    district = release.district(code)
    assert district is not None
    assert sum(len(d.counties) for d in district.divisions) == expected


def test_a_county_resolves_to_its_division(release):
    flmb = release.district("flmb")
    assert flmb is not None
    hillsborough = flmb.division_for_county("12057")
    assert hillsborough is not None
    assert hillsborough.code == "tampa"
    assert flmb.division_for_county("48201") is None  # Harris, TX


def test_every_verified_fact_cites_a_source_read_on_a_date(release):
    def check(fact: courts.Fact[object], where: str, ids: set[str]) -> None:
        if fact.verified:
            assert fact.source in ids, where
            assert fact.verified_at is not None, where

    for district in release.districts:
        ids = {s.id for s in district.sources}
        where = district.code
        for division in district.divisions:
            check(division.office_code, f"{where} {division.code}", ids)
            check(division.courthouse, f"{where} {division.code}", ids)
            assert division.counties_source in ids
        for section in (district.cmecf, district.pdf, district.matrix):
            for name, fact in vars(section).items():
                check(fact, f"{where} {name}", ids)
        check(district.opening.signature_instrument, where, ids)
        check(district.opening.ssn_statement, where, ids)
        check(district.opening.fee_rule, where, ids)
        check(district.registration.training_required, where, ids)


def test_case_upload_is_unverified_everywhere_until_a_training_session(release):
    # ADR 0024, PR 5: proven per court, by a human, outside CI.
    for district in release.districts:
        assert district.case_upload.status == "unverified", district.code
        assert district.case_upload.verified_at is None


def test_no_pdf_a_requirement_is_recorded_as_true(release):
    # "PDF/A (false everywhere today)" — a court flipping it is a release.
    for district in release.districts:
        assert district.pdf.pdf_a_required.value is not True, district.code


def test_resolution_follows_effective_dating(release):
    assert courts.resolve(date.today()) == release
    assert courts.get(release.release_id) == release
    with pytest.raises(LookupError):
        courts.resolve(date(2000, 1, 1))
    with pytest.raises(KeyError):
        courts.get("courts/us-bankruptcy@1999-01-01")


def test_the_lookup_pair_refuses_a_division_of_another_court():
    assert courts.division("flmb", "tampa") is not None
    assert courts.division("flmb", "miami") is None
    assert courts.division("nyeb", "brooklyn") is None
    assert courts.district("flsb") is not None


def test_every_source_is_a_court_government_or_statute_host():
    # This repo is public and the registry is court data: every fact traces to
    # a court, PACER, the Census Bureau, or the statute text — never to a
    # vendor's page or to anything that is not a primary source.
    allowed = (".uscourts.gov", ".census.gov", "www.law.cornell.edu")
    root = Path(courts.__file__).parent / "regulatory" / "courts"
    seen = 0
    for path in root.rglob("*.json"):
        for source in json.loads(path.read_text()).get("sources", []):
            host = urllib.parse.urlparse(source["url"]).netloc
            primary = any(host == a or host.endswith(a) for a in allowed)
            assert primary, (path.name, host)
            seen += 1
    assert seen > 0


def test_a_malformed_record_fails_the_load(tmp_path: Path):
    root = tmp_path / "regulatory"
    release_dir = root / "courts" / "us-bankruptcy" / "2026-01-01"
    (release_dir / "districts").mkdir(parents=True)
    (release_dir / "manifest.json").write_text(
        json.dumps(
            {
                "series_id": "courts/us-bankruptcy",
                "effective_date": "2026-01-01",
                "sequence": 1,
                "source": {"url": "https://example.test"},
                "notes": "a test release",
            }
        )
    )
    real = json.loads(
        (
            Path(courts.__file__).parent
            / "regulatory/courts/us-bankruptcy/2026-09-24/districts/flsb.json"
        ).read_text()
    )
    # A verified fact that cites a source the record does not list.
    real["pdf"]["max_bytes"]["source"] = "S999"
    (release_dir / "districts" / "flsb.json").write_text(json.dumps(real))
    with pytest.raises(ValueError, match="S999"):
        courts.load_registry(root)
