"""Rule 9006(a) day counting and the legal-holiday table (issue 14.6 / #358).

Known-answer tests: every date here was worked out by hand against a
calendar and the rule's text, because a day-counting bug produces a
plausible date and nobody notices until a filing is late. The loader layer
runs over the committed registry so a malformed release fails the pull
request, not a filing.
"""

from __future__ import annotations

import json
from datetime import date
from pathlib import Path

import pytest
from insolvia_api.core.legal_holidays import (
    LEGAL_HOLIDAYS_SERIES,
    load_registry,
    releases,
    resolve,
)

# 2026's calendar, the year the tests count in. 2026-09-24 is a Thursday.
HOLIDAYS = resolve(date(2026, 1, 1))


def test_the_committed_series_loads_and_names_itself() -> None:
    assert [r.release_id for r in releases()] == ["code/legal-holidays@2021-06-17"]
    assert len(HOLIDAYS.holidays) == 11


@pytest.mark.parametrize(
    ("year", "expected"),
    [
        # 5 U.S.C. § 6103(a), 2026 — checked against a printed calendar.
        (
            2026,
            {
                date(2026, 1, 1),  # New Year's Day (Thursday)
                date(2026, 1, 19),  # MLK — third Monday in January
                date(2026, 2, 16),  # Washington's Birthday — third Monday
                date(2026, 5, 25),  # Memorial Day — last Monday in May
                date(2026, 6, 19),  # Juneteenth (Friday)
                date(2026, 7, 3),  # Independence Day falls on Saturday → Friday
                date(2026, 9, 7),  # Labor Day — first Monday in September
                date(2026, 10, 12),  # Columbus Day — second Monday in October
                date(2026, 11, 11),  # Veterans Day (Wednesday)
                date(2026, 11, 26),  # Thanksgiving — fourth Thursday
                date(2026, 12, 25),  # Christmas (Friday)
            },
        ),
        # 2027: New Year's Day is a Friday; Christmas is a Saturday →
        # observed Friday the 24th; Juneteenth is a Saturday → Friday 18th.
        (
            2027,
            {
                date(2027, 1, 1),
                date(2027, 1, 18),
                date(2027, 2, 15),
                date(2027, 5, 31),
                date(2027, 6, 18),
                date(2027, 7, 5),  # July 4 is a Sunday → Monday
                date(2027, 9, 6),
                date(2027, 10, 11),
                date(2027, 11, 11),
                date(2027, 11, 25),
                date(2027, 12, 24),
            },
        ),
    ],
)
def test_the_observed_holidays_of_a_year(year: int, expected: set[date]) -> None:
    assert HOLIDAYS.observed_in(year) == frozenset(expected)


# ── Rule 9006(a)(1) known answers ───────────────────────────────


def test_the_trigger_day_is_excluded_and_every_day_counts() -> None:
    # 14 days after Monday 2026-03-02 is Monday 2026-03-16: day 1 is the
    # 3rd, day 14 the 16th — weekends in between count.
    assert HOLIDAYS.add_days(date(2026, 3, 2), 14) == date(2026, 3, 16)


def test_a_last_day_on_a_saturday_rolls_to_monday() -> None:
    # 60 days after Thursday 2026-03-05 is Monday 2026-05-04? No: day 60 is
    # Monday 2026-05-04 only if... count: 3/5 + 60 = 5/4 (Monday). Use a
    # trigger that lands on a Saturday instead: 14 days after Saturday
    # 2026-02-28 is Saturday 2026-03-14 → Monday 2026-03-16.
    assert HOLIDAYS.add_days(date(2026, 2, 28), 14) == date(2026, 3, 16)


def test_a_last_day_on_a_sunday_rolls_to_monday() -> None:
    # 14 days after Sunday 2026-03-01 is Sunday 2026-03-15 → Monday the 16th.
    assert HOLIDAYS.add_days(date(2026, 3, 1), 14) == date(2026, 3, 16)


def test_a_last_day_on_a_legal_holiday_rolls_past_it() -> None:
    # 30 days after Friday 2026-08-07 is Sunday 2026-09-06; Monday the 7th
    # is Labor Day, so the period runs to Tuesday 2026-09-08.
    assert HOLIDAYS.add_days(date(2026, 8, 7), 30) == date(2026, 9, 8)


def test_a_holiday_that_lands_on_a_weekday_rolls_once() -> None:
    # 14 days after Wednesday 2026-12-11 is Christmas Day (Friday) →
    # Monday 2026-12-28.
    assert HOLIDAYS.add_days(date(2026, 12, 11), 14) == date(2026, 12, 28)


def test_an_observed_holiday_counts_as_the_holiday() -> None:
    # 2026-07-04 is a Saturday, observed Friday 2026-07-03. 60 days after
    # Monday 2026-05-04 is Friday 2026-07-03 → Monday 2026-07-06.
    assert HOLIDAYS.add_days(date(2026, 5, 4), 60) == date(2026, 7, 6)


def test_intermediate_holidays_still_count() -> None:
    # 70 days after Monday 2026-11-02 is Sunday 2027-01-11? Count: 11/2 + 70
    # = 1/11/2027 (Monday). Thanksgiving, Christmas and New Year's Day fall
    # inside the period and change nothing; the last day is a Monday.
    assert HOLIDAYS.add_days(date(2026, 11, 2), 70) == date(2027, 1, 11)


def test_a_zero_day_period_is_the_trigger_itself() -> None:
    assert HOLIDAYS.add_days(date(2026, 7, 4), 0) == date(2026, 7, 4)


def test_court_days_exclude_weekends_and_holidays() -> None:
    assert HOLIDAYS.is_court_day(date(2026, 9, 24))  # a Thursday
    assert not HOLIDAYS.is_court_day(date(2026, 9, 26))  # Saturday
    assert not HOLIDAYS.is_court_day(date(2026, 11, 26))  # Thanksgiving


# ── The loader refuses what it should ───────────────────────────


def _write_release(
    root: Path, holidays: object, manifest_over: dict | None = None
) -> Path:
    release = root / "code" / "legal-holidays" / "2021-06-17"
    release.mkdir(parents=True)
    manifest = {
        "series_id": LEGAL_HOLIDAYS_SERIES,
        "effective_date": "2021-06-17",
        "sequence": 1,
        "source": {"url": "https://example.test/6103", "published": None},
        "notes": "test",
        **(manifest_over or {}),
    }
    (release / "manifest.json").write_text(json.dumps(manifest))
    payload = {
        "holidays": holidays,
        "observance": {
            "saturday": {"shift": "friday_before", "citation": "c"},
            "sunday": {"shift": "monday_after", "citation": "c"},
        },
        "sources": [
            {"title": "t", "url": "https://example.test", "accessed": "2026-01-01"}
        ],
    }
    (release / "holidays.json").write_text(json.dumps(payload))
    return root


def test_a_well_formed_release_loads(tmp_path: Path) -> None:
    root = _write_release(
        tmp_path,
        [
            {
                "holiday_id": "x",
                "name": "X",
                "citation": "c",
                "rule": {"month": 1, "day": 1},
            }
        ],
    )
    assert load_registry(root)[0].release_id == "code/legal-holidays@2021-06-17"


@pytest.mark.parametrize(
    "rule",
    [
        {"month": 13, "day": 1},
        {"month": 1},
        {"month": 1, "day": 1, "weekday": "monday", "ordinal": 1},
        {"month": 1, "weekday": "someday", "ordinal": 1},
        {"month": 1, "weekday": "monday", "ordinal": 6},
    ],
)
def test_a_malformed_date_rule_is_refused(tmp_path: Path, rule: dict) -> None:
    root = _write_release(
        tmp_path, [{"holiday_id": "x", "name": "X", "citation": "c", "rule": rule}]
    )
    with pytest.raises(ValueError, match="malformed release"):
        load_registry(root)


def test_a_manifest_that_disagrees_with_its_path_is_refused(tmp_path: Path) -> None:
    root = _write_release(
        tmp_path,
        [
            {
                "holiday_id": "x",
                "name": "X",
                "citation": "c",
                "rule": {"month": 1, "day": 1},
            }
        ],
        {"effective_date": "2020-01-01"},
    )
    with pytest.raises(ValueError, match="disagrees with the path"):
        load_registry(root)


def test_resolution_refuses_a_date_before_the_series() -> None:
    with pytest.raises(LookupError):
        resolve(date(2020, 1, 1))
