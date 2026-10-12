"""The court registry (issue #360): the loader over the committed release,
and the invariants a district record must hold.

Runs the real loader over the real registry — this is the CI validation
ADR 0014 asks of a series' first consumer, so a malformed or half-cited
record fails a pull request rather than a filing. The launch set is ADR
0017's ten districts; the field list is ADR 0024's.
"""

from __future__ import annotations

import dataclasses
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
        check(district.chapter_13_plan, where, ids)


def test_every_district_states_its_chapter_13_plan_form(release):
    # Issue #367: the latest release carries the key on every record (a
    # missing key would load as "unknown" and silently mean Form 113).
    root = Path(courts.__file__).parent / "regulatory/courts/us-bankruptcy"
    districts = root / release.release_id.split("@")[1] / "districts"
    for path in sorted(districts.glob("*.json")):
        assert "chapter_13_plan" in json.loads(path.read_text()), path.name
    for district in release.districts:
        fact = district.chapter_13_plan
        # An unverified answer carries no value: "we have not read the page"
        # must never be mistaken for "the court takes Form 113".
        assert fact.verified or fact.value is None, district.code


def test_flsb_takes_its_local_plan_form():
    flsb = courts.district("flsb")
    assert flsb is not None
    fact = flsb.chapter_13_plan
    assert fact.verified
    assert fact.value is not None
    assert fact.value.form == "local"


@pytest.mark.parametrize("release_id", ["2026-09-24", "2026-09-26"])
def test_a_release_before_the_fact_loads_it_as_unknown(release_id):
    # Append-only: the releases that predate the fact still load, as unknown.
    earlier = courts.get(f"courts/us-bankruptcy@{release_id}")
    for district in earlier.districts:
        assert not district.chapter_13_plan.verified
        assert district.chapter_13_plan.value is None


def test_the_plan_form_release_adds_only_the_plan_form_fact():
    # @2026-09-26+2 (issue #367) is @2026-09-26 plus `chapter_13_plan`.
    before = courts.get("courts/us-bankruptcy@2026-09-26")
    after = courts.get("courts/us-bankruptcy@2026-09-26+2")
    for old, new in zip(before.districts, after.districts, strict=True):
        assert dataclasses.replace(new, chapter_13_plan=old.chapter_13_plan) == old


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


def test_the_second_release_changes_only_middle_georgias_matrix():
    # courts/us-bankruptcy@2026-09-26 (ADR 0024 PR 2) re-read one court's
    # matrix section; everything else is the first release, carried over.
    # A difference anywhere else is an edit nobody meant to ship.
    first = courts.get("courts/us-bankruptcy@2026-09-24")
    second = courts.get("courts/us-bankruptcy@2026-09-26")
    for old, new in zip(first.districts, second.districts, strict=True):
        if old.code != "gamb":
            assert old == new, old.code
            continue
        assert old.matrix != new.matrix
        assert (
            dataclasses.replace(
                new, matrix=old.matrix, sources=old.sources, notes=old.notes
            )
            == old
        )
    gamb = second.district("gamb")
    assert gamb is not None
    rules = gamb.matrix
    assert rules.blank_lines_between.verified
    assert rules.blank_lines_between.value == 2
    assert rules.comma_after_city.value is True
    assert rules.case_number_header_when_separate.value is False
    assert not rules.certification_required.verified


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


def test_a_local_plan_form_without_a_title_fails_the_load(tmp_path: Path):
    here = Path(courts.__file__).parent / "regulatory/courts/us-bankruptcy/2026-09-26+2"
    release_dir = tmp_path / "regulatory" / "courts" / "us-bankruptcy" / "2026-09-26+2"
    (release_dir / "districts").mkdir(parents=True)
    (release_dir / "manifest.json").write_text((here / "manifest.json").read_text())
    real = json.loads((here / "districts" / "flsb.json").read_text())
    real["chapter_13_plan"]["value"]["title"] = None
    (release_dir / "districts" / "flsb.json").write_text(json.dumps(real))
    with pytest.raises(ValueError, match="needs a title"):
        courts.load_registry(tmp_path / "regulatory")


# --- The filing set's two facts (ADR 0024 build PR 3) -----------------------


def test_every_district_states_its_docket_order_and_file_names(release):
    # The latest release carries both keys on every record, so "unknown" is
    # something the record SAYS, never a key someone forgot.
    root = Path(courts.__file__).parent / "regulatory/courts/us-bankruptcy"
    districts = root / release.release_id.split("@")[1] / "districts"
    for path in sorted(districts.glob("*.json")):
        opening = json.loads(path.read_text())["opening"]
        assert "docket_order" in opening, path.name
        assert "file_names" in opening, path.name
    for district in release.districts:
        for fact in (district.opening.docket_order, district.opening.file_names):
            # Nothing is guessed: an unverified answer carries no value, and
            # every one says why (the filing set shows the note).
            assert fact.verified or fact.value is None, district.code
            assert fact.note, district.code


@pytest.mark.parametrize("release_id", ["2026-09-24", "2026-09-26", "2026-09-26+2"])
def test_a_release_before_the_filing_set_facts_loads_them_as_unknown(release_id):
    earlier = courts.get(f"courts/us-bankruptcy@{release_id}")
    for district in earlier.districts:
        assert district.opening.docket_order.value is None
        assert not district.opening.docket_order.verified
        assert district.opening.file_names.value is None
        assert "2026-10-06" in district.opening.file_names.note


def test_the_filing_set_release_adds_only_the_two_opening_facts():
    before = courts.get("courts/us-bankruptcy@2026-09-26+2")
    after = courts.get("courts/us-bankruptcy@2026-10-06")
    for old, new in zip(before.districts, after.districts, strict=True):
        opening = dataclasses.replace(
            new.opening,
            docket_order=old.opening.docket_order,
            file_names=old.opening.file_names,
        )
        assert dataclasses.replace(new, opening=opening) == old, old.code


def _filing_set_release(tmp_path: Path, mutate) -> Path:
    here = Path(courts.__file__).parent / "regulatory/courts/us-bankruptcy/2026-10-06"
    release_dir = tmp_path / "regulatory" / "courts" / "us-bankruptcy" / "2026-10-06"
    (release_dir / "districts").mkdir(parents=True)
    (release_dir / "manifest.json").write_text((here / "manifest.json").read_text())
    real = json.loads((here / "districts" / "txwb.json").read_text())
    mutate(real["opening"])
    (release_dir / "districts" / "txwb.json").write_text(json.dumps(real))
    return tmp_path / "regulatory"


def test_a_docket_order_with_an_unknown_document_key_fails_the_load(tmp_path: Path):
    def mutate(opening):
        opening["docket_order"]["value"] = ["form/b101", "the petition"]

    with pytest.raises(ValueError, match="not a document key"):
        courts.load_registry(_filing_set_release(tmp_path, mutate))


def test_a_docket_order_naming_a_document_twice_fails_the_load(tmp_path: Path):
    def mutate(opening):
        opening["docket_order"]["value"] = ["form/b101", "form/b101"]

    with pytest.raises(ValueError, match="appears twice"):
        courts.load_registry(_filing_set_release(tmp_path, mutate))


@pytest.mark.parametrize("name", ["../petition.pdf", "petition.exe", "petition.pdf\n"])
def test_a_file_name_that_is_not_a_plain_upload_name_fails_the_load(
    tmp_path: Path, name: str
):
    def mutate(opening):
        opening["file_names"]["value"] = {"form/b101": name}

    with pytest.raises(ValueError, match=r"plain \.pdf/\.txt file name"):
        courts.load_registry(_filing_set_release(tmp_path, mutate))


def test_a_well_formed_docket_order_and_file_names_load(tmp_path: Path):
    def mutate(opening):
        opening["docket_order"]["value"] = ["form/b101", "creditor_matrix"]
        opening["file_names"]["value"] = {
            "form/b101": "Petition.pdf",
            "creditor_matrix": "Creditor.txt",
        }

    (loaded,) = courts.load_registry(_filing_set_release(tmp_path, mutate))
    txwb = loaded.district("txwb")
    assert txwb is not None
    assert txwb.opening.docket_order.value == ("form/b101", "creditor_matrix")
    assert txwb.opening.file_names.value == {
        "form/b101": "Petition.pdf",
        "creditor_matrix": "Creditor.txt",
    }
