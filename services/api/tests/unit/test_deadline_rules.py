"""The committed deadline-rule release and its loader (issue 14.6 / #358).

test_dollar_amounts.py's two layers: the loader against malformed releases
in tmp_path, and the committed registry loaded for real and swept — every
rule cited, verified, sourced with a date, and shaped as the engine
expects. The known-answer layer pins the rules themselves: a count changed
by a bad edit lands on somebody's calendar.
"""

from __future__ import annotations

import json
from datetime import date
from pathlib import Path

import pytest
from insolvia_api.core.deadline_rules import (
    DEADLINE_RULES_SERIES,
    DeadlineRule,
    RulesRelease,
    load_registry,
    releases,
    resolve,
)
from insolvia_api.core.exemptions import Verification

ALL_RULES = [(release, rule) for release in releases() for rule in release.rules]


def _rule_params() -> list[object]:
    return [
        pytest.param(release, rule, id=f"{release.release_id}:{rule.rule_id}")
        for release, rule in ALL_RULES
    ]


def test_the_committed_series_loads_and_names_itself() -> None:
    assert [r.release_id for r in releases()] == ["code/deadline-rules@2024-12-01"]


def test_the_restyled_rules_carry_the_counts_the_text_states() -> None:
    """Each count as Cornell LII's text of the rule reads on 2026-09-24."""
    release = resolve(date(2026, 1, 1))
    expected = {
        "frbp-1007c1-schedules": ("petition", 14, (7, 11, 12, 13)),
        "frbp-1007c3-counseling-certificate": ("petition", 14, (7, 11, 12, 13)),
        "frbp-3015b-chapter-13-plan": ("petition", 14, (13,)),
        "frbp-2003a1a-341-window": ("petition", 40, (7, 11)),
        "frbp-2003a1b-341-window": ("petition", 35, (12,)),
        "frbp-2003a1c-341-window": ("petition", 50, (13,)),
        "usc-341a-meeting": ("meeting_341", 0, (7, 11, 12, 13)),
        "usc-521a2a-statement-of-intention": ("petition", 30, (7,)),
        "usc-521a2b-perform-intention": ("meeting_341", 30, (7,)),
        "usc-1326a1-first-plan-payment": ("petition", 30, (13,)),
        "frbp-4004a-discharge-objection": ("meeting_341", 60, (7, 13)),
        "frbp-4007c-dischargeability-complaint": ("meeting_341", 60, (7, 11, 12, 13)),
        "frbp-4008a-reaffirmation": ("meeting_341", 60, (7,)),
        "frbp-1007c4-financial-management-certificate": ("meeting_341", 60, (7,)),
        "frbp-3002c-proof-of-claim": ("petition", 70, (7, 12, 13)),
        "frbp-3002c1-governmental-proof-of-claim": ("petition", 180, (7, 12, 13)),
    }
    assert {r.rule_id for r in release.rules} == set(expected)
    for rule_id, (anchor, count, chapters) in expected.items():
        rule = release.rule(rule_id)
        assert (rule.anchor, rule.count_days, rule.chapters) == (
            anchor,
            count,
            chapters,
        )


def test_the_341_windows_start_at_twenty_one_days() -> None:
    release = resolve(date(2026, 1, 1))
    for rule_id in (
        "frbp-2003a1a-341-window",
        "frbp-2003a1b-341-window",
        "frbp-2003a1c-341-window",
    ):
        rule = release.rule(rule_id)
        assert rule.kind == "window"
        assert rule.window_start_days == 21
        assert rule.until_anchor == "meeting_341"
        assert rule.computation == "calendar"


@pytest.mark.parametrize(("release", "rule"), _rule_params())
def test_every_rule_is_cited_verified_and_dated(
    release: RulesRelease, rule: DeadlineRule
) -> None:
    assert rule.citation
    assert rule.quoted_text
    assert rule.verification is not Verification.UNVERIFIED
    assert rule.sources
    for source in rule.sources:
        assert source.url.startswith("https://www.law.cornell.edu/")
        assert source.accessed <= date(2026, 12, 31)


@pytest.mark.parametrize(("release", "rule"), _rule_params())
def test_every_rolled_rule_is_a_period_stated_in_days(
    release: RulesRelease, rule: DeadlineRule
) -> None:
    if rule.computation == "rule_9006a":
        assert rule.kind == "deadline"
        assert rule.count_days > 0


# ── The loader refuses what it should ───────────────────────────


def _base_rule() -> dict[str, object]:
    return {
        "rule_id": "x",
        "title": "X",
        "citation": "c",
        "quoted_text": "q",
        "description": "d",
        "chapters": [7],
        "kind": "deadline",
        "anchor": "petition",
        "count_days": 14,
        "computation": "rule_9006a",
        "verification": "primary",
        "sources": [{"title": "t", "url": "https://x.test", "accessed": "2026-01-01"}],
    }


def _write_release(root: Path, rules: list[dict[str, object]]) -> Path:
    release = root / "code" / "deadline-rules" / "2024-12-01"
    release.mkdir(parents=True)
    (release / "manifest.json").write_text(
        json.dumps(
            {
                "series_id": DEADLINE_RULES_SERIES,
                "effective_date": "2024-12-01",
                "sequence": 1,
                "source": {"url": "https://x.test", "published": None},
                "notes": "test",
            }
        )
    )
    (release / "rules.json").write_text(json.dumps({"rules": rules}))
    return root


def test_a_well_formed_rule_loads(tmp_path: Path) -> None:
    root = _write_release(tmp_path, [_base_rule()])
    assert load_registry(root)[0].rule("x").count_days == 14


@pytest.mark.parametrize(
    "over",
    [
        {"verification": "unverified"},
        {"sources": []},
        {"chapters": [9]},
        {"anchor": "confirmation_hearing"},
        {"kind": "deadline", "count_days": 0},
        {"kind": "window"},
        {"kind": "window", "window_start_days": 50, "count_days": 40},
        {"kind": "meeting", "anchor": "petition", "count_days": 0},
        {"computation": "business_days"},
        {"quoted_text": ""},
    ],
)
def test_a_malformed_rule_is_refused(tmp_path: Path, over: dict[str, object]) -> None:
    root = _write_release(tmp_path, [{**_base_rule(), **over}])
    with pytest.raises(ValueError, match="malformed release"):
        load_registry(root)


def test_duplicate_rule_ids_are_refused(tmp_path: Path) -> None:
    root = _write_release(tmp_path, [_base_rule(), _base_rule()])
    with pytest.raises(ValueError, match="duplicate rule ids"):
        load_registry(root)


def test_resolution_refuses_a_petition_before_the_series() -> None:
    with pytest.raises(LookupError):
        resolve(date(2024, 11, 30))
