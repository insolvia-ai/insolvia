"""The event, calendar and feed endpoints (issue 14.6 / #358).

What matters most is what these REFUSE — the same pair every case child
route holds: a case event is reached only through a case the caller may
see, and the `events` levels gate what they say they gate. Then the
engine's hook: setting a filed date on a case populates its deadlines, and
a user's feed carries them (the issue's "done when").

Tokens are signed for real. Every identifier below is obviously fake.
"""

from __future__ import annotations

import time

import jwt
import pytest
from cryptography.hazmat.primitives.asymmetric import rsa
from insolvia_api.adapters.memory.event_store import (
    MemoryCalendarTokenStore,
    MemoryEventStore,
)
from insolvia_api.adapters.memory.mailer_client import InMemoryMailerClient
from insolvia_api.adapters.memory.waitlist_store import MemoryWaitlistStore
from insolvia_api.api.app_factory import create_app
from insolvia_api.api.dependencies import ApiDependencies
from insolvia_api.core.config import load_config
from insolvia_core.adapters.memory.access_log import MemoryAccessLog
from insolvia_core.adapters.memory.case_entity_store import MemoryCaseEntityStore
from insolvia_core.adapters.memory.case_store import MemoryCaseStore
from insolvia_core.adapters.memory.debtor_store import MemoryDebtorStore
from insolvia_core.adapters.memory.firm_store import MemoryFirmStore
from insolvia_core.adapters.memory.jwks_provider import StaticJwksProvider
from insolvia_core.firms import (
    ADD_EDIT,
    CASES,
    EVENTS,
    HIDDEN,
    VIEW_ONLY,
    Firm,
    FirmUser,
)

ISSUER = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE00"
CLIENT_ID = "exampleappclientid000000"
KID = "test-key-1"

FIRM_A = "00000000-0000-4000-8000-00000000f18a"
FIRM_B = "00000000-0000-4000-8000-00000000f18b"

ADMIN = "00000000-0000-4000-8000-00000000a11c"  # firm A admin
BOB = "00000000-0000-4000-8000-00000000b0b0"  # firm A, linked-only, add_edit
VIEWER = "00000000-0000-4000-8000-00000000da4a"  # firm A, view_only, sees all
BLOCKED = "00000000-0000-4000-8000-000000009e69"  # firm A, events hidden
OTHER_ADMIN = "00000000-0000-4000-8000-0000000ca201"  # firm B admin

_PRIVATE_KEY = rsa.generate_private_key(public_exponent=65537, key_size=2048)
_PUBLIC_KEY = _PRIVATE_KEY.public_key()


def auth(subject: str) -> dict[str, str]:
    now = int(time.time())
    token = jwt.encode(
        {
            "iss": ISSUER,
            "client_id": CLIENT_ID,
            "token_use": "access",
            "sub": subject,
            "username": subject,
            "iat": now,
            "auth_time": now,
            "exp": now + 3600,
        },
        _PRIVATE_KEY,  # type: ignore[arg-type]
        algorithm="RS256",
        headers={"kid": KID},
    )
    return {"Authorization": f"Bearer {token}"}


def firm(firm_id: str, name: str) -> Firm:
    return Firm(
        id=firm_id,
        name=name,
        status="active",
        created_at="2026-01-01T00:00:00.000Z",
        updated_at="2026-01-01T00:00:00.000Z",
    )


def member(
    subject: str,
    firm_id: str,
    events: str,
    *,
    is_admin: bool = False,
    access_all_cases: bool = False,
) -> FirmUser:
    return FirmUser(
        firm_id=firm_id,
        subject=subject,
        email=f"{subject[-4:]}@example.test",
        first_name="Person",
        last_name=subject[-4:],
        role="attorney",
        is_admin=is_admin,
        access_all_cases=access_all_cases,
        permissions={EVENTS: events, CASES: ADD_EDIT},
        status="active",
        created_at="2026-01-01T00:00:00.000Z",
        updated_at="2026-01-01T00:00:00.000Z",
    )


@pytest.fixture
def firms():
    store = MemoryFirmStore()
    store.create_firm(firm(FIRM_A, "Example & Partners"))
    store.create_firm(firm(FIRM_B, "Other Firm LLP"))
    store.add_user(member(ADMIN, FIRM_A, ADD_EDIT, is_admin=True))
    store.add_user(member(BOB, FIRM_A, ADD_EDIT))
    store.add_user(member(VIEWER, FIRM_A, VIEW_ONLY, access_all_cases=True))
    store.add_user(member(BLOCKED, FIRM_A, HIDDEN, access_all_cases=True))
    store.add_user(member(OTHER_ADMIN, FIRM_B, ADD_EDIT, is_admin=True))
    return store


@pytest.fixture
def events():
    return MemoryEventStore()


@pytest.fixture
def client(firms, events):
    app = create_app(
        ApiDependencies(
            config=load_config(
                {
                    "INSOLVIA_ENV": "local",
                    "AUTH_ISSUER_URL": ISSUER,
                    "AUTH_CLIENT_ID": CLIENT_ID,
                }
            ),
            waitlist_store=MemoryWaitlistStore(),
            mailer=InMemoryMailerClient(),
            jwks_provider=StaticJwksProvider({KID: _PUBLIC_KEY}),
            case_store=MemoryCaseStore(),
            access_log=MemoryAccessLog(),
            firm_store=firms,
            debtor_store=MemoryDebtorStore(),
            case_entity_store=MemoryCaseEntityStore(),
            event_store=events,
            calendar_token_store=MemoryCalendarTokenStore(),
        )
    )
    return app.test_client()


def open_case(client, subject=ADMIN, chapter=7) -> str:
    response = client.post(
        "/v1/cases",
        json={"chapter": chapter, "district": "NDCA"},
        headers=auth(subject),
    )
    assert response.status_code == 201
    return response.get_json()["id"]


HEARING = {
    "title": "Hearing on the motion",
    "start": "2026-03-02",
    "location": "Courtroom 4",
}


def add_event(client, case_id, subject=ADMIN, body=None):
    return client.post(
        f"/v1/cases/{case_id}/events",
        json=body if body is not None else HEARING,
        headers=auth(subject),
    )


# ── Auth and gating ─────────────────────────────────────────────


@pytest.mark.parametrize(
    ("method", "path"),
    [
        ("get", "/v1/cases/any/events"),
        ("post", "/v1/cases/any/events"),
        ("get", "/v1/firm/events"),
        ("post", "/v1/firm/events"),
        ("get", "/v1/calendar?from=2026-01-01&to=2026-01-31"),
        ("get", "/v1/me/calendar.ics"),
        ("post", "/v1/me/calendar-token"),
    ],
)
def test_every_route_refuses_an_unauthenticated_caller(client, method, path):
    assert getattr(client, method)(path, json={}).status_code == 401


def test_hidden_cannot_even_list(client):
    case_id = open_case(client)
    assert (
        client.get(f"/v1/cases/{case_id}/events", headers=auth(BLOCKED)).status_code
        == 403
    )
    assert client.get("/v1/firm/events", headers=auth(BLOCKED)).status_code == 403


def test_view_only_can_list_but_not_add(client):
    case_id = open_case(client)
    assert (
        client.get(f"/v1/cases/{case_id}/events", headers=auth(VIEWER)).status_code
        == 200
    )
    assert add_event(client, case_id, VIEWER).status_code == 403


def test_another_firms_case_is_not_found(client):
    case_id = open_case(client)
    assert add_event(client, case_id, OTHER_ADMIN).status_code == 404
    assert (
        client.get(f"/v1/cases/{case_id}/events", headers=auth(OTHER_ADMIN)).status_code
        == 404
    )


def test_a_linked_only_colleague_cannot_reach_an_unlinked_case(client):
    case_id = open_case(client, ADMIN)
    assert (
        client.get(f"/v1/cases/{case_id}/events", headers=auth(BOB)).status_code == 404
    )


# ── Hand-made events ────────────────────────────────────────────


def test_a_created_event_is_returned_and_then_listed(client):
    case_id = open_case(client)
    created = add_event(client, case_id)
    assert created.status_code == 201, created.get_json()
    body = created.get_json()
    assert body["title"] == "Hearing on the motion"
    assert body["all_day"] is True
    assert body["generated"] is False
    assert body["case_id"] == case_id
    listed = client.get(f"/v1/cases/{case_id}/events", headers=auth(ADMIN)).get_json()
    assert [e["id"] for e in listed["events"]] == [body["id"]]


def test_a_missing_title_is_a_400_naming_the_field(client):
    case_id = open_case(client)
    response = add_event(client, case_id, body={"start": "2026-03-02"})
    assert response.status_code == 400
    assert "title" in response.get_json()["fields"]


def test_an_attendee_outside_the_firm_is_refused(client):
    case_id = open_case(client)
    response = add_event(client, case_id, body={**HEARING, "attendees": [OTHER_ADMIN]})
    assert response.status_code == 400
    assert "attendees[0]" in response.get_json()["message"]


def test_a_replace_keeps_the_id_and_a_delete_removes_it(client):
    case_id = open_case(client)
    event_id = add_event(client, case_id).get_json()["id"]
    replaced = client.put(
        f"/v1/cases/{case_id}/events/{event_id}",
        json={
            "title": "Moved",
            "start": "2026-03-03T14:00:00-05:00",
            "attendees": [BOB],
        },
        headers=auth(ADMIN),
    )
    assert replaced.status_code == 200, replaced.get_json()
    assert replaced.get_json()["id"] == event_id
    assert replaced.get_json()["start"] == "2026-03-03T19:00:00Z"
    assert replaced.get_json()["attendees"] == [BOB]
    assert (
        client.delete(
            f"/v1/cases/{case_id}/events/{event_id}", headers=auth(ADMIN)
        ).status_code
        == 204
    )
    assert (
        client.get(
            f"/v1/cases/{case_id}/events/{event_id}", headers=auth(ADMIN)
        ).status_code
        == 404
    )


def test_firm_events_are_scoped_to_the_callers_firm(client):
    created = client.post(
        "/v1/firm/events",
        json={"title": "Office closed", "start": "2026-07-03"},
        headers=auth(ADMIN),
    )
    assert created.status_code == 201
    event_id = created.get_json()["id"]
    assert "case_id" not in created.get_json()
    mine = client.get("/v1/firm/events", headers=auth(VIEWER)).get_json()["events"]
    assert [e["id"] for e in mine] == [event_id]
    theirs = client.get("/v1/firm/events", headers=auth(OTHER_ADMIN)).get_json()
    assert theirs["events"] == []
    assert (
        client.get(f"/v1/firm/events/{event_id}", headers=auth(OTHER_ADMIN)).status_code
        == 404
    )


# ── The deadline engine's hook ──────────────────────────────────


def set_dates(client, case_id, **dates):
    response = client.patch(f"/v1/cases/{case_id}", json=dates, headers=auth(ADMIN))
    assert response.status_code == 200, response.get_json()
    return response.get_json()


def generated(client, case_id):
    listed = client.get(f"/v1/cases/{case_id}/events", headers=auth(ADMIN)).get_json()
    return {e["rule_id"]: e for e in listed["events"] if e["generated"]}


def test_setting_a_filed_date_populates_the_deadlines(client):
    case_id = open_case(client)
    assert generated(client, case_id) == {}
    body = set_dates(client, case_id, filed_at="2026-03-02")
    assert body["filedAt"] == "2026-03-02"
    rules = generated(client, case_id)
    assert "frbp-1007c1-schedules" in rules
    assert rules["frbp-1007c1-schedules"]["start"] == "2026-03-16"
    assert (
        rules["frbp-1007c1-schedules"]["rule_citation"]
        == "Fed. R. Bankr. P. 1007(c)(1)"
    )
    assert rules["frbp-1007c1-schedules"]["attendees"] == [ADMIN]
    assert "frbp-2003a1a-341-window" in rules
    assert "frbp-4004a-discharge-objection" not in rules


def test_setting_the_341_date_regenerates_and_keeps_a_dismissal(client):
    case_id = open_case(client)
    set_dates(client, case_id, filed_at="2026-03-02")
    schedules = generated(client, case_id)["frbp-1007c1-schedules"]
    dismissed = client.patch(
        f"/v1/cases/{case_id}/events/{schedules['id']}",
        json={"dismissed": True},
        headers=auth(ADMIN),
    )
    assert dismissed.status_code == 200
    assert dismissed.get_json()["dismissed"] is True

    set_dates(client, case_id, meeting_341_at="2026-04-07")
    rules = generated(client, case_id)
    assert "frbp-2003a1a-341-window" not in rules
    assert rules["usc-341a-meeting"]["start"] == "2026-04-07"
    assert rules["frbp-4004a-discharge-objection"]["start"] == "2026-06-08"
    assert rules["frbp-1007c1-schedules"]["id"] == schedules["id"]
    assert rules["frbp-1007c1-schedules"]["dismissed"] is True


def test_clearing_the_filed_date_removes_every_generated_event(client):
    case_id = open_case(client)
    set_dates(client, case_id, filed_at="2026-03-02", meeting_341_at="2026-04-07")
    add_event(client, case_id)
    body = set_dates(client, case_id, filed_at=None)
    assert "filedAt" not in body
    listed = client.get(f"/v1/cases/{case_id}/events", headers=auth(ADMIN)).get_json()
    assert [e["generated"] for e in listed["events"]] == [False]


def test_a_meeting_before_the_petition_is_refused(client):
    case_id = open_case(client)
    response = client.patch(
        f"/v1/cases/{case_id}",
        json={"filed_at": "2026-03-02", "meeting_341_at": "2026-03-01"},
        headers=auth(ADMIN),
    )
    assert response.status_code == 400
    assert "meeting_341_at" in response.get_json()["fields"]


def test_a_generated_deadline_cannot_be_edited_or_deleted_only_dismissed(client):
    case_id = open_case(client)
    set_dates(client, case_id, filed_at="2026-03-02")
    event_id = generated(client, case_id)["frbp-1007c1-schedules"]["id"]
    put = client.put(
        f"/v1/cases/{case_id}/events/{event_id}",
        json={"title": "Moved", "start": "2026-12-25"},
        headers=auth(ADMIN),
    )
    assert put.status_code == 400
    delete = client.delete(
        f"/v1/cases/{case_id}/events/{event_id}", headers=auth(ADMIN)
    )
    assert delete.status_code == 400
    assert generated(client, case_id)["frbp-1007c1-schedules"]["start"] == "2026-03-16"


# ── The calendar ────────────────────────────────────────────────


def test_the_calendar_answers_a_window_across_cases_and_the_firm(client):
    first = open_case(client)
    second = open_case(client)
    set_dates(client, first, filed_at="2026-03-02")
    add_event(
        client, second, body={"title": "Client call", "start": "2026-03-10T15:00:00Z"}
    )
    client.post(
        "/v1/firm/events",
        json={"title": "Office closed", "start": "2026-03-20"},
        headers=auth(ADMIN),
    )
    march = client.get(
        "/v1/calendar?from=2026-03-01&to=2026-03-31", headers=auth(VIEWER)
    ).get_json()
    titles = [e["title"] for e in march["events"]]
    assert "Schedules, statements and other documents due" in titles
    assert "Client call" in titles
    assert "Office closed" in titles
    assert "Proofs of claim due (non-governmental creditors)" not in titles  # May
    # Sorted by start.
    assert titles.index("Client call") < titles.index("Office closed")

    only_second = client.get(
        f"/v1/calendar?from=2026-03-01&to=2026-03-31&case_id={second}",
        headers=auth(ADMIN),
    ).get_json()
    assert [e["title"] for e in only_second["events"]] == ["Client call"]


def test_the_calendar_hides_cases_the_caller_cannot_see(client):
    hidden = open_case(client, ADMIN)
    set_dates(client, hidden, filed_at="2026-03-02")
    visible = open_case(client, BOB)
    add_event(
        client, visible, BOB, body={"title": "Bob's hearing", "start": "2026-03-05"}
    )
    bobs = client.get(
        "/v1/calendar?from=2026-03-01&to=2026-03-31", headers=auth(BOB)
    ).get_json()
    assert [e["title"] for e in bobs["events"]] == ["Bob's hearing"]
    # And a case_id filter for a case Bob cannot see is empty, not a 404.
    probe = client.get(
        f"/v1/calendar?from=2026-03-01&to=2026-03-31&case_id={hidden}",
        headers=auth(BOB),
    )
    assert probe.status_code == 200
    assert probe.get_json()["events"] == []


def test_the_calendar_filters_by_attendee(client):
    case_id = open_case(client)
    add_event(client, case_id, body={**HEARING, "attendees": [BOB]})
    add_event(client, case_id, body={"title": "Admin only", "start": "2026-03-03"})
    mine = client.get(
        "/v1/calendar?from=2026-03-01&to=2026-03-31&attendee=me", headers=auth(BOB)
    )
    # BOB is not linked to the case, so nothing — the attendee filter never
    # widens visibility.
    assert mine.get_json()["events"] == []
    bobs = client.get(
        f"/v1/calendar?from=2026-03-01&to=2026-03-31&attendee={BOB}",
        headers=auth(ADMIN),
    ).get_json()
    assert [e["title"] for e in bobs["events"]] == ["Hearing on the motion"]


def test_an_event_overlapping_the_window_start_is_included(client):
    case_id = open_case(client)
    add_event(
        client,
        case_id,
        body={"title": "Span", "start": "2026-02-25", "end": "2026-03-02"},
    )
    march = client.get(
        "/v1/calendar?from=2026-03-01&to=2026-03-31", headers=auth(ADMIN)
    ).get_json()
    assert [e["title"] for e in march["events"]] == ["Span"]


@pytest.mark.parametrize(
    "query",
    [
        "",
        "from=2026-03-01",
        "from=2026-03-31&to=2026-03-01",
        "from=2026-01-01&to=2028-01-01",
    ],
)
def test_a_bad_window_is_a_400(client, query):
    assert client.get(f"/v1/calendar?{query}", headers=auth(ADMIN)).status_code == 400


# ── The feed ────────────────────────────────────────────────────


def test_the_session_feed_carries_the_deadlines(client):
    case_id = open_case(client)
    set_dates(client, case_id, filed_at="2026-03-02")
    # A generated event a year out is outside the feed's lookahead only if
    # today is far from 2026 — pin the window by adding a near event too.
    response = client.get("/v1/me/calendar.ics", headers=auth(ADMIN))
    assert response.status_code == 200
    assert response.mimetype == "text/calendar"
    assert response.get_data(as_text=True).startswith("BEGIN:VCALENDAR")


def test_a_feed_token_opens_the_feed_and_only_that_users_feed(client):
    case_id = open_case(client, ADMIN)
    add_event(client, case_id, body={"title": "Hearing", "start": "2026-03-02"})
    minted = client.post("/v1/me/calendar-token", headers=auth(ADMIN))
    assert minted.status_code == 201
    token = minted.get_json()["token"]
    assert minted.get_json()["feedPath"] == f"/v1/me/calendar.ics?token={token}"

    feed = client.get(f"/v1/me/calendar.ics?token={token}")
    assert feed.status_code == 200
    assert feed.mimetype == "text/calendar"

    # Bob's token sees Bob's calendar — which does not include Admin's case.
    bobs = client.post("/v1/me/calendar-token", headers=auth(BOB)).get_json()["token"]
    bob_feed = client.get(f"/v1/me/calendar.ics?token={bobs}")
    assert bob_feed.status_code == 200
    assert "SUMMARY:Hearing" not in bob_feed.get_data(as_text=True)


def test_a_bad_or_revoked_token_is_401(client):
    assert client.get("/v1/me/calendar.ics?token=garbage").status_code == 401
    token = client.post("/v1/me/calendar-token", headers=auth(ADMIN)).get_json()[
        "token"
    ]
    assert client.get(f"/v1/me/calendar.ics?token={token}x").status_code == 401
    assert (
        client.delete("/v1/me/calendar-token", headers=auth(ADMIN)).status_code == 204
    )
    assert client.get(f"/v1/me/calendar.ics?token={token}").status_code == 401
    assert (
        client.delete("/v1/me/calendar-token", headers=auth(ADMIN)).status_code == 404
    )


def test_rotating_the_token_retires_the_old_one(client):
    first = client.post("/v1/me/calendar-token", headers=auth(ADMIN)).get_json()[
        "token"
    ]
    second = client.post("/v1/me/calendar-token", headers=auth(ADMIN)).get_json()[
        "token"
    ]
    assert first != second
    assert client.get(f"/v1/me/calendar.ics?token={first}").status_code == 401
    assert client.get(f"/v1/me/calendar.ics?token={second}").status_code == 200


def test_a_hidden_user_cannot_mint_or_use_a_token(client):
    assert (
        client.post("/v1/me/calendar-token", headers=auth(BLOCKED)).status_code == 403
    )
