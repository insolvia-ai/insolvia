"""Events, the deadline engine and the ICS feed, in the pure layer (issue
14.6 / #358): parsing, the item shape, generation from a case's anchors,
reconciliation against stored events, and the feed's rendering.

Every identifier below is obviously fake. This repo is public.
"""

from __future__ import annotations

from datetime import UTC, date, datetime

import pytest
from insolvia_api.core import deadline_rules, legal_holidays
from insolvia_api.core.calendar_feed import (
    CalendarToken,
    feed_token,
    hash_secret,
    render_ics,
    secret_matches,
    split_feed_token,
)
from insolvia_api.core.deadlines import generate, reconcile
from insolvia_api.core.events import (
    Event,
    EventScope,
    create_event,
    event_from_item,
    event_item,
    event_json,
    parse_dismissal,
    parse_event,
    replace_event,
    set_dismissed,
)
from insolvia_core.cases import Case
from insolvia_core.errors import FieldValidationError, ValidationError

FIRM = "00000000-0000-4000-8000-00000000f18a"
CASE = "00000000-0000-4000-8000-0000000000c1"
ALICE = "00000000-0000-4000-8000-00000000a11c"
BOB = "00000000-0000-4000-8000-00000000b0b0"


def case(**over: object) -> Case:
    base: dict[str, object] = {
        "id": CASE,
        "firm_id": FIRM,
        "created_by": ALICE,
        "chapter": 7,
        "district": "NDCA",
        "status": "filed",
        "created_at": "2026-01-01T00:00:00.000000Z",
        "updated_at": "2026-01-01T00:00:00.000000Z",
    }
    return Case(**{**base, **over})  # type: ignore[arg-type]


# ── Parsing ─────────────────────────────────────────────────────


def test_a_title_and_a_start_are_required() -> None:
    with pytest.raises(FieldValidationError) as caught:
        parse_event({})
    assert set(caught.value.fields) == {"title", "start"}


def test_a_bare_date_start_means_all_day_and_end_defaults_to_start() -> None:
    draft = parse_event({"title": "Hearing", "start": "2026-03-02"})
    assert draft.all_day
    assert (draft.start, draft.end) == ("2026-03-02", "2026-03-02")


def test_a_timed_event_is_normalised_to_utc_and_ends_an_hour_later() -> None:
    draft = parse_event({"title": "Call", "start": "2026-03-02T10:00:00-05:00"})
    assert not draft.all_day
    assert (draft.start, draft.end) == ("2026-03-02T15:00:00Z", "2026-03-02T16:00:00Z")


def test_a_timed_start_without_an_offset_is_refused() -> None:
    with pytest.raises(FieldValidationError) as caught:
        parse_event({"title": "Call", "start": "2026-03-02T10:00:00"})
    assert "start" in caught.value.fields


def test_an_end_before_the_start_is_refused() -> None:
    with pytest.raises(FieldValidationError) as caught:
        parse_event({"title": "x", "start": "2026-03-02", "end": "2026-03-01"})
    assert "end" in caught.value.fields


def test_an_event_longer_than_the_span_cap_is_refused() -> None:
    with pytest.raises(FieldValidationError) as caught:
        parse_event({"title": "x", "start": "2026-01-01", "end": "2026-04-01"})
    assert "end" in caught.value.fields


def test_attendees_are_subjects_without_duplicates() -> None:
    with pytest.raises(FieldValidationError) as caught:
        parse_event(
            {"title": "x", "start": "2026-03-02", "attendees": [ALICE, "bob", ALICE]}
        )
    assert set(caught.value.fields) == {"attendees[1]", "attendees[2]"}


def test_a_dismissal_body_is_the_one_flag_alone() -> None:
    assert parse_dismissal({"dismissed": True}) is True
    with pytest.raises(FieldValidationError):
        parse_dismissal({"dismissed": True, "title": "sneaky"})
    with pytest.raises(FieldValidationError):
        parse_dismissal({"dismissed": "yes"})


# ── Construction, replacement, the item ─────────────────────────


def hand_made(**over: object) -> Event:
    draft = parse_event({"title": "Hearing", "start": "2026-03-02", **over})
    return create_event(draft, scope=EventScope(FIRM, CASE), created_by=ALICE)


def test_a_case_event_item_sits_in_its_case_and_on_the_calendar_index() -> None:
    item = event_item(hand_made())
    assert item["PK"] == f"CASE#{CASE}"
    assert item["SK"].startswith("EVENT#")
    assert item["GSI1PK"] == f"FIRMCAL#{FIRM}"
    assert item["GSI1SK"].startswith("2026-03-02T00:00:00Z#")


def test_a_firm_event_item_sits_in_the_firm_partition() -> None:
    draft = parse_event({"title": "Office closed", "start": "2026-03-02"})
    event = create_event(draft, scope=EventScope(FIRM), created_by=ALICE)
    item = event_item(event)
    assert item["PK"] == f"FIRM#{FIRM}"
    assert "caseId" not in item
    assert event_from_item(item) == event


def test_the_item_round_trips_every_field() -> None:
    event = hand_made(
        description="Bring the file", location="Courtroom 4", attendees=[ALICE, BOB]
    )
    assert event_from_item(event_item(event)) == event


def test_the_wire_shape_omits_absent_fields_and_names_generated() -> None:
    body = event_json(hand_made())
    assert body["generated"] is False
    assert body["all_day"] is True
    assert "description" not in body
    assert "rule_id" not in body
    assert body["case_id"] == CASE


def test_replacing_keeps_identity_and_refuses_a_generated_event() -> None:
    event = hand_made()
    draft = parse_event({"title": "Moved hearing", "start": "2026-03-03"})
    replaced = replace_event(event, draft)
    assert (replaced.id, replaced.created_by, replaced.created_at) == (
        event.id,
        event.created_by,
        event.created_at,
    )
    assert replaced.title == "Moved hearing"
    generated = set_dismissed(event, False).__class__(
        **{**event.__dict__, "rule_id": "frbp-x", "rule_citation": "c"}
    )
    with pytest.raises(ValidationError):
        replace_event(generated, draft)


# ── The deadline engine ─────────────────────────────────────────

RULES = deadline_rules.resolve(date(2026, 1, 1))
HOLIDAYS = legal_holidays.resolve(date(2026, 1, 1))


def by_rule(deadlines: tuple[object, ...]) -> dict[str, tuple[date, date]]:
    return {d.rule.rule_id: (d.start, d.end) for d in deadlines}  # type: ignore[attr-defined]


def test_nothing_is_generated_without_a_filed_date() -> None:
    assert generate(case(), RULES, HOLIDAYS) == ()


def test_a_chapter_7_petition_alone_yields_the_petition_anchored_rules() -> None:
    # Filed Monday 2026-03-02.
    produced = by_rule(generate(case(filed_at="2026-03-02"), RULES, HOLIDAYS))
    assert set(produced) == {
        "frbp-1007c1-schedules",
        "frbp-1007c3-counseling-certificate",
        "frbp-2003a1a-341-window",
        "usc-521a2a-statement-of-intention",
        "frbp-3002c-proof-of-claim",
        "frbp-3002c1-governmental-proof-of-claim",
    }
    # 14 days → Monday 2026-03-16; 30 → Wednesday 04-01; 70 → Monday 05-11;
    # 180 → Saturday 08-29 → Monday 08-31.
    assert produced["frbp-1007c1-schedules"] == (date(2026, 3, 16), date(2026, 3, 16))
    assert produced["usc-521a2a-statement-of-intention"] == (
        date(2026, 4, 1),
        date(2026, 4, 1),
    )
    assert produced["frbp-3002c-proof-of-claim"] == (
        date(2026, 5, 11),
        date(2026, 5, 11),
    )
    assert produced["frbp-3002c1-governmental-proof-of-claim"] == (
        date(2026, 8, 31),
        date(2026, 8, 31),
    )
    # The window: calendar days 21 to 40, unrolled.
    assert produced["frbp-2003a1a-341-window"] == (date(2026, 3, 23), date(2026, 4, 11))


def test_the_341_date_replaces_the_window_and_unlocks_the_rest() -> None:
    # Meeting set for Tuesday 2026-04-07; 60 days → Saturday 2026-06-06 →
    # Monday 06-08; 30 days → Thursday 05-07.
    produced = by_rule(
        generate(
            case(filed_at="2026-03-02", meeting_341_at="2026-04-07"), RULES, HOLIDAYS
        )
    )
    assert "frbp-2003a1a-341-window" not in produced
    assert produced["usc-341a-meeting"] == (date(2026, 4, 7), date(2026, 4, 7))
    assert produced["frbp-4004a-discharge-objection"] == (
        date(2026, 6, 8),
        date(2026, 6, 8),
    )
    assert produced["frbp-4007c-dischargeability-complaint"] == (
        date(2026, 6, 8),
        date(2026, 6, 8),
    )
    assert produced["frbp-4008a-reaffirmation"] == (date(2026, 6, 8), date(2026, 6, 8))
    assert produced["frbp-1007c4-financial-management-certificate"] == (
        date(2026, 6, 8),
        date(2026, 6, 8),
    )
    assert produced["usc-521a2b-perform-intention"] == (
        date(2026, 5, 7),
        date(2026, 5, 7),
    )


def test_the_statement_of_intention_is_capped_by_an_earlier_341_date() -> None:
    # Meeting on Wednesday 2026-03-25, before petition + 30 (2026-04-01).
    produced = by_rule(
        generate(
            case(filed_at="2026-03-02", meeting_341_at="2026-03-25"), RULES, HOLIDAYS
        )
    )
    assert produced["usc-521a2a-statement-of-intention"] == (
        date(2026, 3, 25),
        date(2026, 3, 25),
    )


def test_chapter_13_gets_its_own_rules() -> None:
    produced = by_rule(
        generate(case(chapter=13, filed_at="2026-03-02"), RULES, HOLIDAYS)
    )
    assert "frbp-3015b-chapter-13-plan" in produced
    assert "usc-1326a1-first-plan-payment" in produced
    assert "usc-521a2a-statement-of-intention" not in produced
    assert produced["frbp-2003a1c-341-window"] == (date(2026, 3, 23), date(2026, 4, 21))
    # 30 days after Monday 03-02 is Wednesday 04-01.
    assert produced["usc-1326a1-first-plan-payment"] == (
        date(2026, 4, 1),
        date(2026, 4, 1),
    )


def test_a_deadline_rolls_over_a_legal_holiday() -> None:
    # Filed Friday 2026-08-07: 30 days is Sunday 09-06, Monday is Labor Day,
    # so the statement of intention is due Tuesday 2026-09-08.
    produced = by_rule(generate(case(filed_at="2026-08-07"), RULES, HOLIDAYS))
    assert produced["usc-521a2a-statement-of-intention"] == (
        date(2026, 9, 8),
        date(2026, 9, 8),
    )


def test_reconcile_creates_replaces_deletes_and_keeps_dismissals() -> None:
    filed = case(filed_at="2026-03-02")
    first = reconcile(
        filed, generate(filed, RULES, HOLIDAYS), (), attendees=(ALICE,), actor=ALICE
    )
    assert not first.put
    assert not first.delete
    stored = tuple(first.create)
    assert all(e.rule_id and e.all_day and e.attendees == (ALICE,) for e in stored)
    window = next(e for e in stored if e.rule_id == "frbp-2003a1a-341-window")
    schedules = next(e for e in stored if e.rule_id == "frbp-1007c1-schedules")
    dismissed = set_dismissed(schedules, True)
    stored = tuple(dismissed if e.id == schedules.id else e for e in stored)

    # The meeting is set: the window goes, the 341-anchored rules arrive, the
    # dismissed schedules deadline is untouched.
    later = case(filed_at="2026-03-02", meeting_341_at="2026-04-07")
    second = reconcile(
        later, generate(later, RULES, HOLIDAYS), stored, attendees=(ALICE,), actor=ALICE
    )
    assert [e.id for e in second.delete] == [window.id]
    assert {e.rule_id for e in second.create} >= {
        "usc-341a-meeting",
        "frbp-4004a-discharge-objection",
    }
    assert not second.put
    assert dismissed not in second.delete

    # The petition date moves a week: every petition-anchored event is
    # rewritten in place — same id, dismissal kept.
    moved = case(filed_at="2026-03-09", meeting_341_at="2026-04-07")
    all_stored = stored + tuple(second.create)
    all_stored = tuple(e for e in all_stored if e.id != window.id)
    third = reconcile(
        moved, generate(moved, RULES, HOLIDAYS), all_stored, attendees=(), actor=ALICE
    )
    rewritten = {e.id: e for e in third.put}
    assert schedules.id in rewritten
    assert rewritten[schedules.id].dismissed is True
    assert rewritten[schedules.id].start == "2026-03-23"
    assert not third.create
    assert not third.delete

    # And a hand-made event is never in any plan.
    plain = hand_made()
    fourth = reconcile(
        moved,
        generate(moved, RULES, HOLIDAYS),
        (*all_stored, plain),
        attendees=(),
        actor=ALICE,
    )
    assert plain not in fourth.delete
    assert plain not in fourth.put


# ── The feed ────────────────────────────────────────────────────


def test_the_feed_token_splits_and_hashes() -> None:
    token = feed_token(ALICE, "s3cret")
    assert split_feed_token(token) == (ALICE, "s3cret")
    assert split_feed_token("nonsense") is None
    assert split_feed_token("not-a-subject.x") is None
    stored = CalendarToken(FIRM, ALICE, hash_secret("s3cret"), "2026-01-01T00:00:00Z")
    assert secret_matches(stored, "s3cret")
    assert not secret_matches(stored, "S3CRET")


def test_the_ics_renders_all_day_and_timed_events_and_skips_dismissed() -> None:
    all_day = hand_made(description="Bring the file, and the plan; both")
    timed = create_event(
        parse_event({"title": "Call", "start": "2026-03-02T15:00:00Z"}),
        scope=EventScope(FIRM),
        created_by=ALICE,
    )
    gone = set_dismissed(hand_made(title="Not this one"), True)
    ics = render_ics([all_day, timed, gone], now=datetime(2026, 1, 1, tzinfo=UTC))
    assert ics.startswith("BEGIN:VCALENDAR\r\nVERSION:2.0\r\n")
    assert "DTSTART;VALUE=DATE:20260302\r\nDTEND;VALUE=DATE:20260303" in ics
    assert "DTSTART:20260302T150000Z\r\nDTEND:20260302T160000Z" in ics
    assert "DESCRIPTION:Bring the file\\, and the plan\\; both" in ics
    assert "Not this one" not in ics
    assert f"UID:{all_day.id}@insolvia" in ics
    assert ics.endswith("END:VCALENDAR\r\n")
    assert all(len(line) <= 76 for line in ics.split("\r\n"))
