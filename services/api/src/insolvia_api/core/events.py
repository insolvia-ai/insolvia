"""Calendar events: validation, identity, and the stored item shape
(issue 14.6 / #358).

An event is a titled span of time — a § 341 meeting, a hearing, a client
call, an office closure — with a place and the colleagues attending it. It
lives at CASE scope (a case's partition, alongside its documents and jobs)
or at FIRM scope (no case: an office event), and the calendar reads both
through one index. Two kinds share the shape:

- A HAND-MADE event is ordinary: whoever may edit events writes it whole
  (PUT), like a library creditor, and removes it.
- A GENERATED event names the deadline rule that produced it (`rule_id`)
  and is the deadline engine's to write (core/deadlines.py). Nobody edits
  one by hand — the date IS the rule applied to the case's anchors, and a
  hand-moved deadline is a lie the calendar tells — but anyone who may edit
  events may DISMISS one ("filed with the petition", "nothing to reaffirm"),
  and a dismissal survives regeneration.

WHY NOT A CASE COLLECTION. core/case_entities.py's machinery is for the
FACTS of a case — every field carries provenance, because every field may
land on a signed form. An event lands on nobody's form; its author is
`created_by` and that is provenance enough, exactly the argument
core/documents.py makes for a document row.

TIMES. An all-day event carries form dates (`YYYY-MM-DD`, no zone — a
hearing on the 14th is on the 14th wherever the reader sits), `end`
inclusive. A timed event carries RFC 3339 instants normalised to UTC with a
literal Z. Both sort under one key, `sort_instant`, which is what the
calendar's range query runs over. An event spans at most
MAX_EVENT_SPAN_DAYS, and that cap is load-bearing: the range query reads
starts from `from - MAX_EVENT_SPAN_DAYS`, so it is the cap that makes a
start-keyed index answer "what overlaps this window" exactly.
"""

from __future__ import annotations

import re
import uuid
from collections.abc import Mapping
from dataclasses import dataclass, replace
from datetime import UTC, date, datetime, timedelta
from typing import Any, Final

from insolvia_core.cases import partition_key as case_partition_key
from insolvia_core.errors import FieldValidationError, ValidationError
from insolvia_core.fields import form_date, narrative, text, timestamp

MAX_TITLE: Final = 200
MAX_LOCATION: Final = 200
MAX_DESCRIPTION: Final = 2000
MAX_ATTENDEES: Final = 50
# The longest an event may run, in days. See the module docstring: the
# calendar's index is keyed on START, so the query looks back this far and
# filters on END — a longer event would fall out of a window it overlaps.
MAX_EVENT_SPAN_DAYS: Final = 62

SK_PREFIX: Final = "EVENT"
# The GSI1 partition every event of a firm shares, DISTINCT from the case
# listing's FIRM#<id> on the same index: the by-firm listing queries
# GSI1PK = FIRM#<id> with no sort condition, and events under that key
# would pour into it.
_CALENDAR_KEY_PREFIX: Final = "FIRMCAL"

_SUBJECT_RE: Final = re.compile(
    r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\Z"
)
_INSTANT_RE: Final = re.compile(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z\Z")


@dataclass(frozen=True)
class EventScope:
    """Where an event lives: its firm, and its case when it has one."""

    firm_id: str
    case_id: str | None = None


@dataclass(frozen=True)
class Event:
    id: str
    firm_id: str
    case_id: str | None
    title: str
    description: str | None
    start: str
    end: str
    all_day: bool
    location: str | None
    attendees: tuple[str, ...]
    rule_id: str | None
    rule_citation: str | None
    dismissed: bool
    created_by: str
    created_at: str
    updated_at: str

    @property
    def generated(self) -> bool:
        return self.rule_id is not None

    @property
    def scope(self) -> EventScope:
        return EventScope(firm_id=self.firm_id, case_id=self.case_id)


@dataclass(frozen=True)
class EventDraft:
    """A validated whole-record save — POST to create, PUT to replace."""

    title: str
    description: str | None
    start: str
    end: str
    all_day: bool
    location: str | None
    attendees: tuple[str, ...]


# --- Time handling -----------------------------------------------------------


def _instant(value: object, path: str, errors: dict[str, str]) -> str | None:
    """An RFC 3339 instant with an offset (or Z), normalised to UTC."""
    if value is None:
        return None
    if not isinstance(value, str) or not value.strip():
        errors[path] = "Must be a date-time."
        return None
    try:
        parsed = datetime.fromisoformat(value.strip().replace("Z", "+00:00"))
    except ValueError:
        errors[path] = "Must be an RFC 3339 date-time, like 2026-03-02T15:00:00Z."
        return None
    if parsed.tzinfo is None:
        errors[path] = "Must carry a time zone offset (or Z)."
        return None
    return parsed.astimezone(UTC).strftime("%Y-%m-%dT%H:%M:%SZ")


def parse_instant(value: str) -> datetime:
    return datetime.strptime(value, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=UTC)


def sort_instant(event: Event) -> str:
    """The one key both stores order and range-query on: an all-day event
    sorts at the start of its first day, a timed one at its instant."""
    return f"{event.start}T00:00:00Z" if event.all_day else event.start


def end_instant(event: Event) -> str:
    """The instant an event is over — the END of its last day when all-day,
    which is what makes an all-day event on the 14th overlap the 14th."""
    if event.all_day:
        return f"{event.end}T23:59:59Z"
    return event.end


def day_instant(day: date) -> str:
    return f"{day.isoformat()}T00:00:00Z"


# --- Parsing -----------------------------------------------------------------


def _attendees(value: object, errors: dict[str, str]) -> tuple[str, ...]:
    if value is None:
        return ()
    if not isinstance(value, list):
        errors["attendees"] = "Must be a list of firm-user subjects."
        return ()
    if len(value) > MAX_ATTENDEES:
        errors["attendees"] = f"At most {MAX_ATTENDEES} attendees."
        return ()
    seen: list[str] = []
    for index, raw in enumerate(value):
        if not isinstance(raw, str) or not _SUBJECT_RE.match(raw):
            errors[f"attendees[{index}]"] = "Must be a firm-user subject."
            continue
        if raw in seen:
            errors[f"attendees[{index}]"] = "Duplicate attendee."
            continue
        seen.append(raw)
    return tuple(seen)


def parse_event(payload: Mapping[str, object]) -> EventDraft:
    """Validate POST/PUT of an event. Unknown keys are ignored.

    `title` and `start` are required — an event with neither a name nor a
    time is not on a calendar. `end` defaults to `start` for an all-day
    event and to an hour after it for a timed one. `all_day` defaults to
    whether `start` reads as a bare date.
    """
    errors: dict[str, str] = {}
    title = text(payload.get("title"), "title", errors, limit=MAX_TITLE)
    if title is None and "title" not in errors:
        errors["title"] = "A title is required."
    description = narrative(
        payload.get("description"), "description", errors, limit=MAX_DESCRIPTION
    )
    location = text(payload.get("location"), "location", errors, limit=MAX_LOCATION)
    attendees = _attendees(payload.get("attendees"), errors)

    raw_all_day = payload.get("all_day")
    raw_start = payload.get("start")
    if raw_all_day is None:
        all_day = isinstance(raw_start, str) and len(raw_start.strip()) == 10
    elif isinstance(raw_all_day, bool):
        all_day = raw_all_day
    else:
        errors["all_day"] = "Must be true or false."
        all_day = False

    start: str | None
    end: str | None
    if all_day:
        start = form_date(raw_start, "start", errors)
        end = form_date(payload.get("end"), "end", errors)
        if start is None and "start" not in errors:
            errors["start"] = "A start date is required."
        if start is not None:
            if end is None and "end" not in errors:
                end = start
            if end is not None and end < start:
                errors["end"] = "The end cannot precede the start."
            elif end is not None:
                span = date.fromisoformat(end) - date.fromisoformat(start)
                if span.days > MAX_EVENT_SPAN_DAYS:
                    errors["end"] = (
                        f"An event may span at most {MAX_EVENT_SPAN_DAYS} days."
                    )
    else:
        start = _instant(raw_start, "start", errors)
        end = _instant(payload.get("end"), "end", errors)
        if start is None and "start" not in errors:
            errors["start"] = "A start time is required."
        if start is not None:
            if end is None and "end" not in errors:
                end = (parse_instant(start) + timedelta(hours=1)).strftime(
                    "%Y-%m-%dT%H:%M:%SZ"
                )
            if end is not None and end < start:
                errors["end"] = "The end cannot precede the start."
            elif end is not None:
                span = parse_instant(end) - parse_instant(start)
                if span > timedelta(days=MAX_EVENT_SPAN_DAYS):
                    errors["end"] = (
                        f"An event may span at most {MAX_EVENT_SPAN_DAYS} days."
                    )

    if errors or title is None or start is None or end is None:
        raise FieldValidationError(errors)
    return EventDraft(
        title=title,
        description=description,
        start=start,
        end=end,
        all_day=all_day,
        location=location,
        attendees=attendees,
    )


def parse_dismissal(payload: Mapping[str, object]) -> bool:
    """PATCH's one field. Refuses anything else so a client that meant PUT
    finds out, rather than having its edits silently dropped."""
    if set(payload) != {"dismissed"} or not isinstance(payload.get("dismissed"), bool):
        raise FieldValidationError({"dismissed": "Must be true or false, and alone."})
    return bool(payload["dismissed"])


# --- Construction ------------------------------------------------------------


def create_event(draft: EventDraft, *, scope: EventScope, created_by: str) -> Event:
    now = timestamp()
    return Event(
        id=str(uuid.uuid4()),
        firm_id=scope.firm_id,
        case_id=scope.case_id,
        title=draft.title,
        description=draft.description,
        start=draft.start,
        end=draft.end,
        all_day=draft.all_day,
        location=draft.location,
        attendees=draft.attendees,
        rule_id=None,
        rule_citation=None,
        dismissed=False,
        created_by=created_by,
        created_at=now,
        updated_at=now,
    )


def replace_event(existing: Event, draft: EventDraft) -> Event:
    """A hand-made event with the draft applied — id, scope, author and
    created_at kept. Refuses a generated event: its dates are the rule's."""
    if existing.generated:
        raise ValidationError(
            "a generated deadline cannot be edited — dismiss it, or change the "
            "case's dates and it is regenerated"
        )
    return replace(
        existing,
        title=draft.title,
        description=draft.description,
        start=draft.start,
        end=draft.end,
        all_day=draft.all_day,
        location=draft.location,
        attendees=draft.attendees,
        updated_at=timestamp(),
    )


def set_dismissed(existing: Event, dismissed: bool) -> Event:
    return replace(existing, dismissed=dismissed, updated_at=timestamp())


# --- Storage -----------------------------------------------------------------


def partition_key(scope: EventScope) -> str:
    """A case event is a child of its case's partition; a firm event has the
    firm's own partition IN THE CASE TABLE (PK = FIRM#<firm_id>), so both
    kinds sit under one index and one grant."""
    if scope.case_id is not None:
        return case_partition_key(scope.case_id)
    return f"FIRM#{scope.firm_id}"


def sort_key(event_id: str) -> str:
    return f"{SK_PREFIX}#{event_id}"


def calendar_key(firm_id: str) -> str:
    return f"{_CALENDAR_KEY_PREFIX}#{firm_id}"


def calendar_sort_key(event: Event) -> str:
    return f"{sort_instant(event)}#{event.id}"


def list_order(event: Event) -> tuple[str, str]:
    """Start order, id as the tiebreak — one definition for both stores."""
    return (sort_instant(event), event.id)


def event_item(event: Event) -> dict[str, Any]:
    """The exact stored item shape, shared by both EventStore implementations.

    PK      CASE#<case_id> | FIRM#<firm_id>    where the event lives
    SK      EVENT#<id>
    GSI1PK  FIRMCAL#<firm_id>                   the firm's calendar, one
    GSI1SK  <sort_instant>#<id>                 range query on start
    """
    item: dict[str, Any] = {
        "PK": partition_key(event.scope),
        "SK": sort_key(event.id),
        "GSI1PK": calendar_key(event.firm_id),
        "GSI1SK": calendar_sort_key(event),
        "id": event.id,
        "firmId": event.firm_id,
        "title": event.title,
        "start": event.start,
        "end": event.end,
        "allDay": event.all_day,
        "attendees": list(event.attendees),
        "dismissed": event.dismissed,
        "createdBy": event.created_by,
        "createdAt": event.created_at,
        "updatedAt": event.updated_at,
    }
    if event.case_id is not None:
        item["caseId"] = event.case_id
    if event.description is not None:
        item["description"] = event.description
    if event.location is not None:
        item["location"] = event.location
    if event.rule_id is not None:
        item["ruleId"] = event.rule_id
    if event.rule_citation is not None:
        item["ruleCitation"] = event.rule_citation
    return item


def _optional_str(item: Mapping[str, Any], key: str) -> str | None:
    value = item.get(key)
    return str(value) if value is not None else None


def event_from_item(item: Mapping[str, Any]) -> Event:
    try:
        raw_attendees = item.get("attendees", [])
        if not isinstance(raw_attendees, list):
            raise ValueError("attendees is not a list")
        return Event(
            id=str(item["id"]),
            firm_id=str(item["firmId"]),
            case_id=_optional_str(item, "caseId"),
            title=str(item["title"]),
            description=_optional_str(item, "description"),
            start=str(item["start"]),
            end=str(item["end"]),
            all_day=item.get("allDay") is True,
            location=_optional_str(item, "location"),
            attendees=tuple(str(a) for a in raw_attendees),
            rule_id=_optional_str(item, "ruleId"),
            rule_citation=_optional_str(item, "ruleCitation"),
            dismissed=item.get("dismissed") is True,
            created_by=str(item["createdBy"]),
            created_at=str(item["createdAt"]),
            updated_at=str(item["updatedAt"]),
        )
    except (KeyError, ValueError) as error:
        raise ValidationError(f"stored event item is malformed: {error}") from error


def event_json(event: Event) -> dict[str, Any]:
    """The API representation. Snake_case like every case-domain body;
    absent values omitted rather than sent as null. `generated` is derived
    and sent because it is the one fact the client branches on."""
    body: dict[str, Any] = {
        "id": event.id,
        "title": event.title,
        "start": event.start,
        "end": event.end,
        "all_day": event.all_day,
        "attendees": list(event.attendees),
        "generated": event.generated,
        "dismissed": event.dismissed,
        "created_by": event.created_by,
        "created_at": event.created_at,
        "updated_at": event.updated_at,
    }
    if event.case_id is not None:
        body["case_id"] = event.case_id
    if event.description is not None:
        body["description"] = event.description
    if event.location is not None:
        body["location"] = event.location
    if event.rule_id is not None:
        body["rule_id"] = event.rule_id
    if event.rule_citation is not None:
        body["rule_citation"] = event.rule_citation
    return body


def valid_subject(value: str) -> bool:
    return bool(_SUBJECT_RE.match(value))


def is_valid_instant(value: str) -> bool:
    return bool(_INSTANT_RE.match(value))
