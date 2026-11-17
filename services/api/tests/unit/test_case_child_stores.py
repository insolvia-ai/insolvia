"""The API's own case-child stores — packets, events and calendar tokens —
held to one contract by the memory twins and the Postgres adapters (ADR
0030). The job store's contract is test_job_store.py's, run the same way.
Every identifier is obviously fake."""

from __future__ import annotations

from dataclasses import replace

import pytest
from insolvia_api.adapters.memory.event_store import (
    MemoryCalendarTokenStore,
    MemoryEventStore,
)
from insolvia_api.adapters.memory.packet_store import MemoryPacketStore
from insolvia_api.adapters.postgres.event_store import (
    PostgresCalendarTokenStore,
    PostgresEventStore,
)
from insolvia_api.adapters.postgres.packet_store import PostgresPacketStore
from insolvia_api.core.calendar_feed import CalendarToken
from insolvia_api.core.events import EventScope, create_event, parse_event
from insolvia_api.core.packets import new_packet
from insolvia_core.adapters.memory.case_store import MemoryCaseStore
from insolvia_core.adapters.postgres.case_store import PostgresCaseStore
from insolvia_core.cases import create_case, parse_case_creation, pin_case

FIRM = "00000000-0000-4000-8000-00000000f18a"
OTHER_FIRM = "00000000-0000-4000-8000-00000000f18b"
ALICE = "00000000-0000-4000-8000-00000000a11c"
BACKENDS = ("memory", "postgres")


@pytest.fixture(params=BACKENDS)
def database(request):
    """None for the memory twins; an empty, migrated database otherwise."""
    if request.param == "memory":
        return None
    request.getfixturevalue("postgres")
    return request.getfixturevalue("postgres_url")


@pytest.fixture
def cases(database):
    return MemoryCaseStore() if database is None else PostgresCaseStore(database)


def opened(cases, *, status: str = "intake"):
    case, assignment = create_case(
        parse_case_creation(
            {"chapter": 7, "court": "flmb", "division": "tampa"},
            require_clients=False,
        ),
        firm_id=FIRM,
        created_by=ALICE,
    )
    case = replace(case, status=status)
    cases.create(case, assignment)
    return case


# ── Packets: one transaction with the pinned case ───────────────────


@pytest.fixture
def packets(database, cases):
    return (
        MemoryPacketStore(cases) if database is None else PostgresPacketStore(database)
    )


def a_packet(case):
    return new_packet(
        case_id=case.id,
        chapter=case.chapter,
        job_id="job-1",
        byte_size=1234,
        sha256="ab" * 32,
        form_revisions={"form/b101": "2024-06-22"},
        constants_set_id="code/dollar-amounts@2025-04-01",
        creditor_count=4,
        created_by=ALICE,
    )


def pinned(case):
    return pin_case(
        case,
        form_revisions={"form/b101": "2024-06-22"},
        constants_set_id="code/dollar-amounts@2025-04-01",
    )


def test_a_packet_and_its_pins_land_together(cases, packets):
    case = opened(cases)
    packet = a_packet(case)

    assert packets.create(
        packet, pinned_case=pinned(case), expected_updated_at=case.updated_at
    )

    assert packets.get(case.id, packet.id) == packet
    assert packets.list_for_case(case.id) == (packet,)
    assert cases.read_for_worker(case.id).constants_set_id is not None


def test_a_case_that_moved_since_the_read_refuses_both_writes(cases, packets):
    case = opened(cases)
    stale = case.updated_at
    cases.update(replace(case, judge="J", updated_at="2030-01-01T00:00:00.000000Z"))

    assert not packets.create(
        a_packet(case), pinned_case=pinned(case), expected_updated_at=stale
    )
    assert packets.list_for_case(case.id) == ()
    assert cases.read_for_worker(case.id).constants_set_id is None


def test_a_filed_case_is_pinned_only_when_the_caller_allows_it(cases, packets):
    case = opened(cases, status="filed")
    read_at = case.updated_at

    assert not packets.create(
        a_packet(case), pinned_case=pinned(case), expected_updated_at=read_at
    )
    assert packets.create(
        a_packet(case),
        pinned_case=pinned(case),
        expected_updated_at=read_at,
        allow_filed=True,
    )


def test_a_packet_id_is_never_written_twice(cases, packets):
    case = opened(cases)
    packet = a_packet(case)
    packets.create(
        packet, pinned_case=pinned(case), expected_updated_at=case.updated_at
    )
    again = cases.read_for_worker(case.id)
    with pytest.raises(RuntimeError):
        packets.create(
            packet, pinned_case=pinned(again), expected_updated_at=again.updated_at
        )


# ── Events and calendar tokens ──────────────────────────────────────


@pytest.fixture
def events(database):
    return MemoryEventStore() if database is None else PostgresEventStore(database)


@pytest.fixture
def tokens(database):
    if database is None:
        return MemoryCalendarTokenStore()
    return PostgresCalendarTokenStore(database)


def an_event(scope, start, title="Hearing"):
    draft = parse_event({"title": title, "start": start})
    return create_event(draft, scope=scope, created_by=ALICE)


def test_case_and_firm_events_live_in_their_own_scopes(cases, events):
    case = opened(cases)
    on_case = an_event(EventScope(FIRM, case.id), "2026-10-14T15:00:00Z")
    on_firm = an_event(EventScope(FIRM), "2026-10-14")
    events.create(on_case)
    events.create(on_firm)

    assert events.list_for_scope(EventScope(FIRM, case.id)) == (on_case,)
    assert events.list_for_scope(EventScope(FIRM)) == (on_firm,)
    assert events.get(EventScope(FIRM), on_case.id) is None
    with pytest.raises(RuntimeError):
        events.create(on_firm)


def test_the_firm_calendar_is_an_inclusive_range_in_start_order(cases, events):
    case = opened(cases)
    early = an_event(EventScope(FIRM), "2026-10-01")
    late = an_event(EventScope(FIRM, case.id), "2026-10-31T23:00:00Z")
    outside = an_event(EventScope(FIRM), "2026-11-01")
    elsewhere = an_event(EventScope(OTHER_FIRM), "2026-10-10")
    for event in (late, outside, early, elsewhere):
        events.create(event)

    found = events.list_for_firm(
        FIRM, starting_from="2026-10-01T00:00:00Z", until="2026-10-31T23:00:00Z"
    )

    assert found == (early, late)


def test_an_event_is_replaced_and_deleted_only_while_it_exists(events):
    event = an_event(EventScope(FIRM), "2026-10-14")
    assert events.put(event) is False
    events.create(event)
    moved = replace(event, title="Moved", start="2026-10-15", end="2026-10-15")
    assert events.put(moved) is True
    assert events.get(EventScope(FIRM), event.id) == moved
    assert events.delete(EventScope(FIRM), event.id) is True
    assert events.delete(EventScope(FIRM), event.id) is False


def test_a_calendar_token_is_replaced_on_rotation(tokens):
    first = CalendarToken(FIRM, ALICE, secret_hash="a" * 64, created_at="t1")
    second = replace(first, secret_hash="b" * 64, created_at="t2")
    tokens.put(first)
    tokens.put(second)
    assert tokens.get(FIRM, ALICE) == second
    assert tokens.get(OTHER_FIRM, ALICE) is None
    assert tokens.delete(FIRM, ALICE) is True
    assert tokens.delete(FIRM, ALICE) is False
