"""The deadline engine: a case's anchors against the rule table → its generated
events (issue 14.6 / #358).

Pure. Given the case, the rule release in force on its petition date and
the holiday table, `generate` answers which deadlines exist today and on
what dates; `reconcile` turns that answer plus the events already stored
into the writes that make the store agree — creating what is new, replacing
what moved, deleting what no longer applies, and carrying a DISMISSAL across
all of it, because "we filed that with the petition" stays true when the
§ 341 date is corrected a week later.

Nothing here knows a rule's number. Every deadline the calendar shows was
read out of `code/deadline-rules` (core/deadline_rules.py), and the rules
that were left OUT are worth naming here because their absence is a
decision, not an oversight:

- Rule 4003(b)(1), objections to exemptions — counts from the CONCLUSION of
  the § 341 meeting, which the case does not record; the first date set is
  not the same fact, and a deadline stated against the wrong anchor is worse
  than none.
- Rule 4004(a)'s Chapter 11 branch and Rule 1007(c)(4)'s Chapter 11/13
  branch — counted from the confirmation hearing and the last plan payment,
  neither of which this case records yet.
- Rule 4007(d), Chapter 13 hardship-discharge dischargeability — set by the
  court on motion, no fixed count.
- Rule 9006(a)(6)(B)/(C) holidays — see core/legal_holidays.py.

Anchors are the case's two form dates: `filed_at` (the petition, which is
the order for relief in a voluntary case, § 301(b)) and `meeting_341_at`
(the first date set for the § 341(a) meeting). No filed date, no deadlines
— an expected filing date is a plan, and a calendar of deadlines from a
plan is a calendar of dates that will all be wrong.
"""

from __future__ import annotations

from dataclasses import dataclass, replace
from datetime import date, timedelta

from insolvia_core.cases import Case
from insolvia_core.fields import timestamp

from .deadline_rules import DeadlineRule, RulesRelease
from .events import Event, EventDraft, EventScope, create_event
from .legal_holidays import HolidayRelease

# Chapter 9006(a)(6)(B)/(C)'s caveat, appended to every rolled deadline so
# the reader knows what the arithmetic did NOT check.
_HOLIDAY_CAVEAT = (
    "Counted under Fed. R. Bankr. P. 9006(a)(1) against the federal legal "
    "holidays in 5 U.S.C. § 6103(a); a day the President, Congress or the "
    "district's state has declared a holiday (Rule 9006(a)(6)(B) and (C)) is not "
    "in that table — check the court's calendar."
)


@dataclass(frozen=True)
class GeneratedDeadline:
    """One rule applied to one case: what the event will say."""

    rule: DeadlineRule
    start: date
    end: date

    @property
    def title(self) -> str:
        return self.rule.title

    def description(self) -> str:
        parts = [self.rule.description]
        if self.rule.computation == "rule_9006a":
            parts.append(_HOLIDAY_CAVEAT)
        return "\n\n".join(parts)


def _anchor_date(case: Case, anchor: str) -> date | None:
    raw = case.filed_at if anchor == "petition" else case.meeting_341_at
    return date.fromisoformat(raw) if raw is not None else None


def _apply(
    rule: DeadlineRule, case: Case, holidays: HolidayRelease
) -> GeneratedDeadline | None:
    anchor = _anchor_date(case, rule.anchor)
    if anchor is None:
        return None
    if (
        rule.until_anchor is not None
        and _anchor_date(case, rule.until_anchor) is not None
    ):
        # A window the court's actual date has replaced.
        return None

    if rule.kind == "window":
        # The loader guarantees a window carries its start; the `or 0` is
        # for mypy, not for a release that could reach here without one.
        return GeneratedDeadline(
            rule=rule,
            start=anchor + timedelta(days=rule.window_start_days or 0),
            end=anchor + timedelta(days=rule.count_days),
        )

    if rule.computation == "rule_9006a":
        last = holidays.add_days(anchor, rule.count_days)
    else:
        last = anchor + timedelta(days=rule.count_days)

    if rule.not_later_than_anchor is not None:
        cap = _anchor_date(case, rule.not_later_than_anchor)
        if cap is not None and cap < last:
            last = cap
    return GeneratedDeadline(rule=rule, start=last, end=last)


def generate(
    case: Case, release: RulesRelease, holidays: HolidayRelease
) -> tuple[GeneratedDeadline, ...]:
    """Every deadline the rule table produces for this case today — none
    without a filed date, and none for a rule whose anchor is unset."""
    if case.filed_at is None:
        return ()
    produced: list[GeneratedDeadline] = []
    for rule in release.rules:
        if case.chapter not in rule.chapters:
            continue
        deadline = _apply(rule, case, holidays)
        if deadline is not None:
            produced.append(deadline)
    return tuple(produced)


@dataclass(frozen=True)
class ReconcilePlan:
    """The writes that make the store agree with `generate`'s answer."""

    create: tuple[Event, ...]
    put: tuple[Event, ...]
    delete: tuple[Event, ...]


def reconcile(
    case: Case,
    generated: tuple[GeneratedDeadline, ...],
    existing: tuple[Event, ...],
    *,
    attendees: tuple[str, ...],
    actor: str,
) -> ReconcilePlan:
    """Diff the generated deadlines against the case's stored events.

    Keyed by `rule_id`: a stored generated event for a rule that still
    applies is kept (its id, its dismissal, its author) and rewritten only
    when its dates or text changed; one for a rule that no longer applies is
    deleted; a rule with no stored event is created. Hand-made events are
    never touched — they have no rule_id and are not in the diff.

    `attendees` is who the new events are for — the case's assignees at
    generation time, so a colleague's "my calendar" carries the deadlines on
    their matters. An existing event keeps its own list.
    """
    stored = {e.rule_id: e for e in existing if e.rule_id is not None}
    create: list[Event] = []
    put: list[Event] = []
    for deadline in generated:
        rule = deadline.rule
        draft = EventDraft(
            title=deadline.title,
            description=deadline.description(),
            start=deadline.start.isoformat(),
            end=deadline.end.isoformat(),
            all_day=True,
            location=None,
            attendees=attendees,
        )
        current = stored.pop(rule.rule_id, None)
        if current is None:
            fresh = create_event(
                draft, scope=EventScope(case.firm_id, case.id), created_by=actor
            )
            create.append(
                replace(fresh, rule_id=rule.rule_id, rule_citation=rule.citation)
            )
            continue
        if (
            current.start != draft.start
            or current.end != draft.end
            or current.title != draft.title
            or current.description != draft.description
            or current.rule_citation != rule.citation
        ):
            put.append(
                replace(
                    current,
                    title=draft.title,
                    description=draft.description,
                    start=draft.start,
                    end=draft.end,
                    all_day=True,
                    rule_citation=rule.citation,
                    updated_at=timestamp(),
                )
            )
    return ReconcilePlan(
        create=tuple(create), put=tuple(put), delete=tuple(stored.values())
    )
