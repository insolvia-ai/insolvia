"""Legal holidays and Rule 9006(a) day counting (issue 14.6 / #358).

Fed. R. Bankr. P. 9006(a)(1) says how a period stated in days is computed —
exclude the day of the triggering event, count every calendar day, and if
the last day is a Saturday, Sunday or legal holiday the period runs to the
end of the next day that is none of those — and 9006(a)(6) says which days
are legal holidays: the ones 5 U.S.C. § 6103(a) sets aside, plus days the
President or Congress declare, plus (for periods measured after an event)
the district's state holidays.

The § 6103(a) list is DATA in the regulatory registry (docs/adr/0014):
`code/legal-holidays`, one release, each holiday a date RULE (a fixed
month/day or the nth weekday of a month) with its citation, and the two
observance shifts (a Saturday holiday is observed the Friday before under
§ 6103(b)(1); a Sunday holiday the Monday after under Exec. Order 11582
§ 3(a)) carried with theirs. The 9006(a)(6)(B) and (C) days are not a table
anyone publishes and are deliberately NOT here — the deadline description
says so, and a filing that lands on one is the preparer's to check.

Effective-dated like every other series, and resolved the same way
(core/dollar_amounts.py is the pattern-setter): a case filed before the
earliest release refuses rather than guessing, and a new holiday arrives as
a new release. Stdlib only; reading the registry shipped inside this package
is configuration access, not an external dependency.
"""

from __future__ import annotations

import json
from collections.abc import Mapping
from dataclasses import dataclass
from datetime import date, timedelta
from functools import cache
from importlib import resources
from importlib.resources.abc import Traversable

from .exemptions import Source

LEGAL_HOLIDAYS_SERIES = "code/legal-holidays"

_WEEKDAYS = (
    "monday",
    "tuesday",
    "wednesday",
    "thursday",
    "friday",
    "saturday",
    "sunday",
)
_SATURDAY = 5
_SUNDAY = 6


@dataclass(frozen=True)
class HolidayRule:
    """One § 6103(a) holiday as a rule over the calendar. Exactly one of
    `day` (a fixed date) or `weekday`+`ordinal` (the nth weekday, or the
    last) is set; the loader refuses anything else."""

    holiday_id: str
    name: str
    citation: str
    month: int
    day: int | None
    weekday: int | None
    # 1..5, or -1 for "last".
    ordinal: int | None

    def date_in(self, year: int) -> date:
        if self.day is not None:
            return date(year, self.month, self.day)
        if self.weekday is None or self.ordinal is None:  # pragma: no cover
            raise ValueError(f"holiday {self.holiday_id} has no date rule")
        if self.ordinal > 0:
            first = date(year, self.month, 1)
            offset = (self.weekday - first.weekday()) % 7
            return first + timedelta(days=offset + 7 * (self.ordinal - 1))
        # The last <weekday> of the month: step back from the month's end.
        next_month = date(year + (self.month == 12), self.month % 12 + 1, 1)
        last = next_month - timedelta(days=1)
        return last - timedelta(days=(last.weekday() - self.weekday) % 7)


@dataclass(frozen=True)
class Observance:
    """How a holiday falling on a weekend is observed, with the authority."""

    saturday_shift: str
    saturday_citation: str
    sunday_shift: str
    sunday_citation: str


@dataclass(frozen=True)
class HolidayRelease:
    series_id: str
    effective_date: date
    sequence: int
    source_url: str
    notes: str
    holidays: tuple[HolidayRule, ...]
    observance: Observance
    sources: tuple[Source, ...]

    @property
    def release_id(self) -> str:
        base = f"{self.series_id}@{self.effective_date.isoformat()}"
        return base if self.sequence == 1 else f"{base}+{self.sequence}"

    def observed_in(self, year: int) -> frozenset[date]:
        """Every day that is a legal holiday in `year`: each holiday's own
        date, shifted per the observance rules when it lands on a weekend.
        The shifted day replaces the weekend day — a period never ends on a
        weekend anyway, so nothing is lost by not listing it twice."""
        days: set[date] = set()
        for holiday in self.holidays:
            actual = holiday.date_in(year)
            if actual.weekday() == _SATURDAY:
                days.add(actual - timedelta(days=1))
            elif actual.weekday() == _SUNDAY:
                days.add(actual + timedelta(days=1))
            else:
                days.add(actual)
        return frozenset(days)

    def is_legal_holiday(self, day: date) -> bool:
        return day in self.observed_in(day.year)

    def is_court_day(self, day: date) -> bool:
        """Not a Saturday, a Sunday, or a legal holiday — the day a period
        may end on under Rule 9006(a)(1)(C)."""
        return day.weekday() < _SATURDAY and not self.is_legal_holiday(day)

    def add_days(self, trigger: date, days: int) -> date:
        """Rule 9006(a)(1): the last day of a `days`-day period triggered by
        `trigger`. The trigger day is excluded, every intermediate day counts,
        and a last day that is a Saturday, Sunday or legal holiday rolls
        forward to the next day that is none of those.

        A zero-day period is the trigger day itself, unrolled — that is not a
        period 9006(a) computes, and the one caller that asks for it (the
        § 341 meeting, an event ON its date) wants the date the court set.
        """
        if days <= 0:
            return trigger
        last = trigger + timedelta(days=days)
        while not self.is_court_day(last):
            last += timedelta(days=1)
        return last


# --- Loading and validation --------------------------------------------------


def _fail(where: str, problem: str) -> ValueError:
    return ValueError(f"malformed release {where}: {problem}")


def _str_field(data: Mapping[str, object], key: str, where: str) -> str:
    value = data.get(key)
    if not isinstance(value, str) or not value.strip():
        raise _fail(where, f"{key} missing or empty")
    return value


def _sources(value: object, where: str) -> tuple[Source, ...]:
    if not isinstance(value, list) or not value:
        raise _fail(where, "sources missing or empty")
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


def _holiday(raw: object, where: str) -> HolidayRule:
    if not isinstance(raw, dict):
        raise _fail(where, "a holiday is not an object")
    holiday_id = _str_field(raw, "holiday_id", where)
    where = f"{where}:{holiday_id}"
    rule = raw.get("rule")
    if not isinstance(rule, dict):
        raise _fail(where, "rule missing")
    month = rule.get("month")
    if not isinstance(month, int) or isinstance(month, bool) or not 1 <= month <= 12:
        raise _fail(where, "rule.month must be 1-12")
    day = rule.get("day")
    weekday_name = rule.get("weekday")
    ordinal_raw = rule.get("ordinal")
    if day is not None:
        if weekday_name is not None or ordinal_raw is not None:
            raise _fail(where, "rule names both a day and a weekday")
        if not isinstance(day, int) or isinstance(day, bool) or not 1 <= day <= 31:
            raise _fail(where, "rule.day must be 1-31")
        return HolidayRule(
            holiday_id=holiday_id,
            name=_str_field(raw, "name", where),
            citation=_str_field(raw, "citation", where),
            month=month,
            day=day,
            weekday=None,
            ordinal=None,
        )
    if not isinstance(weekday_name, str) or weekday_name not in _WEEKDAYS:
        raise _fail(where, "rule.weekday must name a weekday")
    if ordinal_raw == "last":
        ordinal = -1
    elif (
        isinstance(ordinal_raw, int)
        and not isinstance(ordinal_raw, bool)
        and 1 <= ordinal_raw <= 5
    ):
        ordinal = ordinal_raw
    else:
        raise _fail(where, "rule.ordinal must be 1-5 or 'last'")
    return HolidayRule(
        holiday_id=holiday_id,
        name=_str_field(raw, "name", where),
        citation=_str_field(raw, "citation", where),
        month=month,
        day=None,
        weekday=_WEEKDAYS.index(weekday_name),
        ordinal=ordinal,
    )


def _observance(raw: object, where: str) -> Observance:
    if not isinstance(raw, dict):
        raise _fail(where, "observance missing")
    saturday = raw.get("saturday")
    sunday = raw.get("sunday")
    if not isinstance(saturday, dict) or not isinstance(sunday, dict):
        raise _fail(where, "observance needs saturday and sunday")
    if (
        saturday.get("shift") != "friday_before"
        or sunday.get("shift") != "monday_after"
    ):
        # The only shifts the arithmetic implements; a release naming another
        # would be silently ignored, which is the failure this refuses.
        raise _fail(where, "unsupported observance shift")
    return Observance(
        saturday_shift="friday_before",
        saturday_citation=_str_field(saturday, "citation", where),
        sunday_shift="monday_after",
        sunday_citation=_str_field(sunday, "citation", where),
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


def load_release(release_dir: Traversable) -> HolidayRelease:
    where = f"{LEGAL_HOLIDAYS_SERIES}/{release_dir.name}"
    effective_text, _, sequence_text = release_dir.name.partition("+")
    try:
        effective = date.fromisoformat(effective_text)
    except ValueError as exc:
        raise _fail(
            where, "directory name is not <effective_date>[+<sequence>]"
        ) from exc
    sequence = int(sequence_text) if sequence_text else 1

    manifest = _load_json(release_dir.joinpath("manifest.json"), where)
    if manifest.get("series_id") != LEGAL_HOLIDAYS_SERIES:
        raise _fail(where, f"manifest series_id {manifest.get('series_id')!r}")
    if manifest.get("effective_date") != effective.isoformat():
        raise _fail(where, "manifest effective_date disagrees with the path")
    if manifest.get("sequence") != sequence:
        raise _fail(where, "manifest sequence disagrees with the path")
    source = manifest.get("source")
    if not isinstance(source, dict):
        raise _fail(where, "manifest source missing")

    payload = _load_json(release_dir.joinpath("holidays.json"), where)
    raw_holidays = payload.get("holidays")
    if not isinstance(raw_holidays, list) or not raw_holidays:
        raise _fail(where, "payload holidays missing or empty")
    holidays = tuple(_holiday(raw, where) for raw in raw_holidays)
    ids = [h.holiday_id for h in holidays]
    if len(ids) != len(set(ids)):
        raise _fail(where, "duplicate holiday ids")

    return HolidayRelease(
        series_id=LEGAL_HOLIDAYS_SERIES,
        effective_date=effective,
        sequence=sequence,
        source_url=_str_field(source, "url", where),
        notes=_str_field(manifest, "notes", where),
        holidays=holidays,
        observance=_observance(payload.get("observance"), where),
        sources=_sources(payload.get("sources"), where),
    )


def load_registry(root: Traversable) -> tuple[HolidayRelease, ...]:
    """Every release of the series under a registry root, oldest first.
    Raises ValueError on any malformed release — the loader-in-CI rule."""
    series_dir = root.joinpath("code").joinpath("legal-holidays")
    loaded = [
        load_release(release_dir)
        for release_dir in sorted(series_dir.iterdir(), key=lambda n: n.name)
        if release_dir.is_dir()
    ]
    if not loaded:
        raise _fail(LEGAL_HOLIDAYS_SERIES, "series has no releases")
    loaded.sort(key=lambda r: (r.effective_date, r.sequence))
    return tuple(loaded)


@cache
def releases() -> tuple[HolidayRelease, ...]:
    return load_registry(resources.files("insolvia_api").joinpath("regulatory"))


def resolve(as_of: date) -> HolidayRelease:
    """The release in force on `as_of` — effective-dating.md's rule, refusing
    a date before the series begins."""
    applicable = [r for r in releases() if r.effective_date <= as_of]
    if not applicable:
        earliest = min(r.effective_date for r in releases())
        raise LookupError(
            f"no release of {LEGAL_HOLIDAYS_SERIES} is effective on or before "
            f"{as_of.isoformat()} (series begins {earliest.isoformat()})"
        )
    return max(applicable, key=lambda r: (r.effective_date, r.sequence))
