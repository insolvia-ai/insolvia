"""Checks over the code/dollar-amounts series and its loader (issue #99).

test_exemptions.py's two layers, for the same reason: the loader is
exercised against malformed releases built in tmp_path, and the committed
registry is loaded for real and swept — an uncited, unverified, or malformed
figure fails the pull request, not a filing. The known-answer layer pins the
figures themselves: these numbers land on B122A-2 and B107, so the test
records what the Federal Register says and the dataset must keep agreeing.
"""

import json
from datetime import date
from decimal import Decimal
from pathlib import Path

import pytest
from insolvia_api.core.dollar_amounts import (
    DOLLAR_AMOUNTS_SERIES,
    NEXT_SECTION_104_ADJUSTMENT,
    DollarAmount,
    Release,
    Verification,
    get,
    latest,
    load_registry,
    pick_release,
    releases,
    resolve,
)

ALL_AMOUNTS = [
    (release, amount) for release in releases() for amount in release.amounts
]


def _amount_params() -> list[object]:
    return [
        pytest.param(release, amount, id=f"{release.release_id}:{amount.amount_id}")
        for release, amount in ALL_AMOUNTS
    ]


# --- the committed registry loads, whole -------------------------------------


def test_the_committed_series_loads_and_names_itself() -> None:
    assert [release.release_id for release in releases()] == [
        "code/dollar-amounts@2025-04-01"
    ]


def test_the_apr_2025_figures_match_the_federal_register() -> None:
    # 90 FR 8941 (and the printed 04/25 forms). A disagreement here means
    # either a bad edit to the dataset or a new adjustment this test must
    # learn about together with a new release.
    release = get("code/dollar-amounts@2025-04-01")
    expected = {
        "means-test-presumption-floor-60mo": "10275.00",
        "means-test-presumption-ceiling-60mo": "17150.00",
        "means-test-education-annual-cap-per-child": "2575.00",
        "special-circumstances-presumption-floor-60mo": "10275.00",
        "special-circumstances-presumption-ceiling-60mo": "17150.00",
        "median-addon-per-person-motion-standing": "925.00",
        "median-addon-per-person-safe-harbor": "925.00",
        "attorney-sanctions-small-case-cap": "1725.00",
        "sofa-payment-floor-business": "8575.00",
        "sofa-payment-floor-consumer": "600.00",
    }
    assert {a.amount_id: a.amount for a in release.amounts} == expected


@pytest.mark.parametrize(("release", "amount"), _amount_params())
def test_every_figure_is_cited_verified_and_double_sourced(
    release: Release, amount: DollarAmount
) -> None:
    # The 9.5 bar: a dollar figure on a federal filing is never unverified
    # (the loader refuses) and never rests on a single document.
    assert amount.verification is not Verification.UNVERIFIED
    assert "U.S.C." in amount.citation
    assert len(amount.sources) >= 2
    assert Decimal(amount.amount) > 0


def test_the_dataset_does_not_outlive_its_review_date() -> None:
    # The § 104 calendar: when the next triennial lands, this constant, a new
    # release, and the register move together — and until then every adjusted
    # figure must agree about when it dies.
    assert date(2028, 4, 1) == NEXT_SECTION_104_ADJUSTMENT
    for amount in latest().amounts:
        if amount.next_adjustment is not None:
            assert amount.next_adjustment == NEXT_SECTION_104_ADJUSTMENT
    assert latest().effective_date < NEXT_SECTION_104_ADJUSTMENT


def test_the_consumer_floor_is_the_one_unadjusted_figure() -> None:
    consumer = latest().amount("sofa-payment-floor-consumer")
    assert consumer.next_adjustment is None
    assert consumer.citation == "11 U.S.C. § 547(c)(8)"


# --- resolution semantics -----------------------------------------------------


def test_resolution_before_the_series_begins_refuses() -> None:
    with pytest.raises(LookupError, match="refusing"):
        resolve(date(2025, 3, 31))


def test_resolve_and_get_agree_on_the_current_release() -> None:
    release = resolve(date(2026, 9, 4))
    assert get(release.release_id) is release
    assert latest() is release


def test_an_unknown_release_id_is_a_key_error() -> None:
    with pytest.raises(KeyError, match="no release"):
        get("code/dollar-amounts@1999-01-01")


def _release_stub(effective: date, sequence: int) -> Release:
    return Release(
        series_id=DOLLAR_AMOUNTS_SERIES,
        effective_date=effective,
        sequence=sequence,
        source_url="https://example.gov/",
        source_published=None,
        source_sha256=None,
        notes="stub",
        amounts=(),
    )


def test_a_correction_wins_future_resolution_by_sequence() -> None:
    original = _release_stub(date(2025, 4, 1), 1)
    correction = _release_stub(date(2025, 4, 1), 2)
    picked = pick_release((original, correction), date(2025, 6, 1))
    assert picked is correction
    assert picked.release_id == "code/dollar-amounts@2025-04-01+2"


# --- the loader rejects malformed releases ------------------------------------


def _write_release(
    root: Path,
    *,
    manifest_overrides: dict[str, object] | None = None,
    amount_overrides: dict[str, object] | None = None,
) -> Path:
    release_dir = root / "code" / "dollar-amounts" / "2025-04-01"
    release_dir.mkdir(parents=True)
    manifest: dict[str, object] = {
        "series_id": DOLLAR_AMOUNTS_SERIES,
        "effective_date": "2025-04-01",
        "sequence": 1,
        "source": {"url": "https://example.gov/", "published": None, "sha256": None},
        "notes": "a well-formed stub release",
    }
    manifest.update(manifest_overrides or {})
    amount: dict[str, object] = {
        "amount_id": "stub-floor",
        "citation": "11 U.S.C. § 707(b)(2)(A)(i)(I)",
        "description": "stub",
        "amount": "10275.00",
        "verification": "primary",
        "sources": [
            {"title": "stub", "url": "https://example.gov/", "accessed": "2026-09-04"}
        ],
        "figure_since": "2025-04-01",
        "next_adjustment": "2028-04-01",
    }
    amount.update(amount_overrides or {})
    (release_dir / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
    (release_dir / "amounts.json").write_text(
        json.dumps({"amounts": [amount]}), encoding="utf-8"
    )
    return root


def test_the_loader_accepts_a_well_formed_release(tmp_path: Path) -> None:
    loaded = load_registry(_write_release(tmp_path))
    assert loaded[0].release_id == "code/dollar-amounts@2025-04-01"
    assert loaded[0].amount("stub-floor").value == Decimal("10275.00")


@pytest.mark.parametrize(
    ("manifest_overrides", "problem"),
    [
        ({"series_id": "code/other"}, "series_id"),
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
    ("amount_overrides", "problem"),
    [
        ({"amount": "1,000.00"}, "money"),
        ({"amount": "1000.0"}, "money"),
        ({"amount": 1000}, "money"),
        ({"verification": "vibes"}, "vibes"),
        ({"verification": "unverified"}, "never be unverified"),
        ({"sources": []}, "sources"),
        ({"citation": ""}, "citation"),
        ({"description": ""}, "description"),
    ],
)
def test_the_loader_rejects_a_malformed_figure(
    tmp_path: Path, amount_overrides: dict[str, object], problem: str
) -> None:
    root = _write_release(tmp_path, amount_overrides=amount_overrides)
    with pytest.raises(ValueError, match=problem):
        load_registry(root)


def test_the_loader_rejects_duplicate_amount_ids(tmp_path: Path) -> None:
    root = _write_release(tmp_path)
    payload_path = root / "code" / "dollar-amounts" / "2025-04-01" / "amounts.json"
    payload = json.loads(payload_path.read_text(encoding="utf-8"))
    payload["amounts"].append(payload["amounts"][0])
    payload_path.write_text(json.dumps(payload), encoding="utf-8")
    with pytest.raises(ValueError, match="duplicate amount ids"):
        load_registry(root)


def test_an_empty_series_directory_is_refused(tmp_path: Path) -> None:
    (tmp_path / "code" / "dollar-amounts").mkdir(parents=True)
    with pytest.raises(ValueError, match="no releases"):
        load_registry(tmp_path)
