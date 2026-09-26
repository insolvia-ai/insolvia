"""The deadline rule table: loader and resolution for `code/deadline-rules`
(issue 14.6 / #358).

Once a case is filed, a fixed set of deadlines follows from two dates — the
petition (the order for relief in a voluntary case) and the first date set
for the § 341(a) meeting. Which deadlines, from which anchor, counted how,
in which chapters, is LAW, and law is effective-dated data in this repo
(docs/adr/0014), not code: each rule in a release names the Federal Rule of
Bankruptcy Procedure or Code section it implements, quotes the operative
sentence, and records where and when that text was read. Adding a deadline
is a diff to `rules.json` reviewed against the rule it cites; the engine in
core/deadlines.py never learns a rule's number.

The shape of a rule:

    rule_id             stable, kebab, named for the authority (frbp-4004a-…)
    title, description  what the calendar shows
    citation            the authority, as a person reads it
    quoted_text         the operative sentence, verbatim — the review surface
    chapters            which chapters the rule applies to
    kind                deadline | window | meeting
    anchor              petition | meeting_341 — the date it counts from
    count_days          the count (a window's END for `window`)
    window_start_days   a window's start offset (windows only)
    computation         rule_9006a | calendar — whether 9006(a)(1) rolls it
    not_later_than_anchor   the deadline is the EARLIER of the computed day
                        and this anchor's date (§ 521(a)(2)(A))
    until_anchor        generate only while this anchor is unset — a window
                        the court's actual date replaces
    verification, sources   core/exemptions.py's tiers, never UNVERIFIED

Resolution follows effective-dating.md exactly — the release effective on
the case's petition date, refusing a date before the series begins — and
tests/unit/test_deadline_rules.py loads the committed registry so an
uncited or malformed rule fails the pull request, not a filing.
"""

from __future__ import annotations

import json
from collections.abc import Mapping
from dataclasses import dataclass
from datetime import date
from functools import cache
from importlib import resources
from importlib.resources.abc import Traversable

from .exemptions import Source, Verification

DEADLINE_RULES_SERIES = "code/deadline-rules"

ANCHORS = ("petition", "meeting_341")
KINDS = ("deadline", "window", "meeting")
COMPUTATIONS = ("rule_9006a", "calendar")
CHAPTERS = (7, 11, 12, 13)


@dataclass(frozen=True)
class DeadlineRule:
    rule_id: str
    title: str
    citation: str
    quoted_text: str
    description: str
    chapters: tuple[int, ...]
    kind: str
    anchor: str
    count_days: int
    computation: str
    verification: Verification
    sources: tuple[Source, ...]
    window_start_days: int | None = None
    not_later_than_anchor: str | None = None
    until_anchor: str | None = None


@dataclass(frozen=True)
class RulesRelease:
    series_id: str
    effective_date: date
    sequence: int
    source_url: str
    notes: str
    rules: tuple[DeadlineRule, ...]

    @property
    def release_id(self) -> str:
        base = f"{self.series_id}@{self.effective_date.isoformat()}"
        return base if self.sequence == 1 else f"{base}+{self.sequence}"

    def rule(self, rule_id: str) -> DeadlineRule:
        found = next((r for r in self.rules if r.rule_id == rule_id), None)
        if found is None:
            raise KeyError(f"{self.release_id} has no rule {rule_id!r}")
        return found


# --- Loading and validation --------------------------------------------------


def _fail(where: str, problem: str) -> ValueError:
    return ValueError(f"malformed release {where}: {problem}")


def _str_field(data: Mapping[str, object], key: str, where: str) -> str:
    value = data.get(key)
    if not isinstance(value, str) or not value.strip():
        raise _fail(where, f"{key} missing or empty")
    return value


def _count(value: object, where: str, key: str, *, minimum: int) -> int:
    if not isinstance(value, int) or isinstance(value, bool) or value < minimum:
        raise _fail(where, f"{key} must be a whole number >= {minimum}")
    return value


def _sources(value: object, where: str) -> tuple[Source, ...]:
    if not isinstance(value, list) or not value:
        raise _fail(
            where, "sources missing or empty — a rule must say where it was read"
        )
    parsed: list[Source] = []
    for raw in value:
        if not isinstance(raw, dict):
            raise _fail(where, "a source is not an object")
        try:
            accessed = date.fromisoformat(_str_field(raw, "accessed", where))
        except ValueError as exc:
            raise _fail(where, f"source.accessed: {exc}") from exc
        parsed.append(
            Source(
                title=_str_field(raw, "title", where),
                url=_str_field(raw, "url", where),
                accessed=accessed,
            )
        )
    return tuple(parsed)


def _optional_anchor(value: object, where: str, key: str) -> str | None:
    if value is None:
        return None
    if not isinstance(value, str) or value not in ANCHORS:
        raise _fail(where, f"{key} must be one of {', '.join(ANCHORS)}")
    return value


def _rule(raw: object, where: str) -> DeadlineRule:
    if not isinstance(raw, dict):
        raise _fail(where, "a rule is not an object")
    rule_id = _str_field(raw, "rule_id", where)
    where = f"{where}:{rule_id}"

    chapters_raw = raw.get("chapters")
    if not isinstance(chapters_raw, list) or not chapters_raw:
        raise _fail(where, "chapters missing or empty")
    chapters: list[int] = []
    for chapter in chapters_raw:
        if not isinstance(chapter, int) or isinstance(chapter, bool):
            raise _fail(where, "chapters must be numbers")
        if chapter not in CHAPTERS:
            raise _fail(where, f"chapter {chapter} is not one this product files")
        chapters.append(chapter)

    kind = _str_field(raw, "kind", where)
    if kind not in KINDS:
        raise _fail(where, f"kind must be one of {', '.join(KINDS)}")
    anchor = _str_field(raw, "anchor", where)
    if anchor not in ANCHORS:
        raise _fail(where, f"anchor must be one of {', '.join(ANCHORS)}")
    computation = _str_field(raw, "computation", where)
    if computation not in COMPUTATIONS:
        raise _fail(where, f"computation must be one of {', '.join(COMPUTATIONS)}")
    count_days = _count(raw.get("count_days"), where, "count_days", minimum=0)

    window_start = raw.get("window_start_days")
    if kind == "window":
        if window_start is None:
            raise _fail(where, "a window needs window_start_days")
        window_start_days: int | None = _count(
            window_start, where, "window_start_days", minimum=0
        )
        if window_start_days is not None and window_start_days > count_days:
            raise _fail(where, "a window cannot start after it ends")
    else:
        if window_start is not None:
            raise _fail(where, "window_start_days only belongs on a window")
        window_start_days = None
    if kind == "deadline" and count_days == 0:
        raise _fail(where, "a deadline needs a positive count")
    if kind == "meeting" and (count_days != 0 or anchor != "meeting_341"):
        raise _fail(where, "a meeting is the meeting_341 anchor itself")

    verification_raw = _str_field(raw, "verification", where)
    try:
        verification = Verification(verification_raw)
    except ValueError as exc:
        raise _fail(where, f"verification {verification_raw!r}") from exc
    if verification is Verification.UNVERIFIED:
        raise _fail(where, "a deadline rule may never be unverified")

    return DeadlineRule(
        rule_id=rule_id,
        title=_str_field(raw, "title", where),
        citation=_str_field(raw, "citation", where),
        quoted_text=_str_field(raw, "quoted_text", where),
        description=_str_field(raw, "description", where),
        chapters=tuple(chapters),
        kind=kind,
        anchor=anchor,
        count_days=count_days,
        computation=computation,
        verification=verification,
        sources=_sources(raw.get("sources"), where),
        window_start_days=window_start_days,
        not_later_than_anchor=_optional_anchor(
            raw.get("not_later_than_anchor"), where, "not_later_than_anchor"
        ),
        until_anchor=_optional_anchor(raw.get("until_anchor"), where, "until_anchor"),
    )


def _load_json(node: Traversable, where: str) -> dict[str, object]:
    try:
        parsed = json.loads(node.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise _fail(where, f"{node.name} missing") from exc
    except json.JSONDecodeError as exc:
        raise _fail(where, f"{node.name} is not valid JSON: {exc}") from exc
    if not isinstance(parsed, dict):
        raise _fail(where, f"{node.name} is not a JSON object")
    return parsed


def load_release(release_dir: Traversable) -> RulesRelease:
    where = f"{DEADLINE_RULES_SERIES}/{release_dir.name}"
    effective_text, _, sequence_text = release_dir.name.partition("+")
    try:
        effective = date.fromisoformat(effective_text)
    except ValueError as exc:
        raise _fail(
            where, "directory name is not <effective_date>[+<sequence>]"
        ) from exc
    sequence = int(sequence_text) if sequence_text else 1

    manifest = _load_json(release_dir.joinpath("manifest.json"), where)
    if manifest.get("series_id") != DEADLINE_RULES_SERIES:
        raise _fail(where, f"manifest series_id {manifest.get('series_id')!r}")
    if manifest.get("effective_date") != effective.isoformat():
        raise _fail(where, "manifest effective_date disagrees with the path")
    if manifest.get("sequence") != sequence:
        raise _fail(where, "manifest sequence disagrees with the path")
    source = manifest.get("source")
    if not isinstance(source, dict):
        raise _fail(where, "manifest source missing")

    payload = _load_json(release_dir.joinpath("rules.json"), where)
    raw_rules = payload.get("rules")
    if not isinstance(raw_rules, list) or not raw_rules:
        raise _fail(where, "payload rules missing or empty")
    rules = tuple(_rule(raw, where) for raw in raw_rules)
    ids = [r.rule_id for r in rules]
    if len(ids) != len(set(ids)):
        raise _fail(where, "duplicate rule ids")

    return RulesRelease(
        series_id=DEADLINE_RULES_SERIES,
        effective_date=effective,
        sequence=sequence,
        source_url=_str_field(source, "url", where),
        notes=_str_field(manifest, "notes", where),
        rules=rules,
    )


def load_registry(root: Traversable) -> tuple[RulesRelease, ...]:
    series_dir = root.joinpath("code").joinpath("deadline-rules")
    loaded = [
        load_release(release_dir)
        for release_dir in sorted(series_dir.iterdir(), key=lambda n: n.name)
        if release_dir.is_dir()
    ]
    if not loaded:
        raise _fail(DEADLINE_RULES_SERIES, "series has no releases")
    loaded.sort(key=lambda r: (r.effective_date, r.sequence))
    return tuple(loaded)


@cache
def releases() -> tuple[RulesRelease, ...]:
    return load_registry(resources.files("insolvia_api").joinpath("regulatory"))


def resolve(as_of: date) -> RulesRelease:
    """The release in force on `as_of` — the petition date, since the rules
    that govern a case are the ones in force when it was filed."""
    applicable = [r for r in releases() if r.effective_date <= as_of]
    if not applicable:
        earliest = min(r.effective_date for r in releases())
        raise LookupError(
            f"no release of {DEADLINE_RULES_SERIES} is effective on or before "
            f"{as_of.isoformat()} (series begins {earliest.isoformat()})"
        )
    return max(applicable, key=lambda r: (r.effective_date, r.sequence))


def latest() -> RulesRelease:
    return releases()[-1]
