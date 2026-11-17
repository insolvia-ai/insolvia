"""The calendar read and the per-user ICS feed (issue 14.6 / #358).

    GET  /v1/calendar?from=&to=[&attendee=][&case_id=]   the window, as JSON
    GET  /v1/me/calendar.ics[?token=]                     the feed
    POST /v1/me/calendar-token                            mint / rotate
    DELETE /v1/me/calendar-token                          revoke

THE CALENDAR IS ONE FIRM-KEYED QUERY, THEN THE ACCESS RULE. The store hands
back every event of the firm in the window; this module keeps the firm's
own events, and of the case events only those on cases the caller may see
— `core/access.may_see_case` applied through the case store's own listing
for a linked-only user, so nothing here re-derives the rule. An admin or an
`access_all_cases` colleague skips the filter, because the firm key already
IS their rule.

THE FEED TAKES A SESSION OR A TOKEN, and the choice is argued in
core/calendar_feed.py: a calendar application cannot send a bearer token,
so a subscription needs a capability in the URL; a signed-in download does
not. Both resolve to one firm user and then run the identical read, so the
feed can never show an event the same person could not open in the app.
The token path resolves the user WITHOUT `@require_auth` — it is the one
authenticated-by-something-else route in this service, and it answers 401
with the same body a bad bearer gets.
"""

from __future__ import annotations

import logging
from datetime import date, timedelta

from flask import Blueprint, Response, jsonify, request
from flask.typing import ResponseReturnValue
from insolvia_core.access import Accessor
from insolvia_core.errors import NotFoundError, ValidationError
from insolvia_core.fields import timestamp
from insolvia_core.firms import EVENTS, VIEW_ONLY
from insolvia_core.ports import CaseStore, FirmStore

from insolvia_api.api.auth import (
    UNAUTHORIZED_BODY,
    current_accessor,
    require_auth,
    requires,
)
from insolvia_api.api.dependencies import dependencies
from insolvia_api.core.calendar_feed import (
    CalendarToken,
    feed_token,
    hash_secret,
    mint_secret,
    render_ics,
    secret_matches,
    split_feed_token,
)
from insolvia_api.core.events import (
    MAX_EVENT_SPAN_DAYS,
    Event,
    day_instant,
    end_instant,
    event_json,
)
from insolvia_api.core.ports import CalendarTokenStore, EventStore

logger = logging.getLogger(__name__)

blueprint = Blueprint("calendar", __name__)

# The widest window one read answers. A year of a firm's calendar is a few
# hundred rows; the cap exists so a client cannot ask for a decade.
MAX_WINDOW_DAYS = 400
# What the feed carries: the past two months (a deadline that just passed
# is still worth seeing) and the year ahead.
FEED_LOOKBACK_DAYS = 62
FEED_LOOKAHEAD_DAYS = 366


def _event_store() -> EventStore:
    deps = dependencies()
    if deps.event_store is None:
        raise RuntimeError("event store is not composed")
    return deps.event_store


def _token_store() -> CalendarTokenStore:
    deps = dependencies()
    if deps.calendar_token_store is None:
        raise RuntimeError("calendar token store is not composed")
    return deps.calendar_token_store


def _case_store() -> CaseStore:
    deps = dependencies()
    if deps.case_store is None:
        raise RuntimeError("case store is not composed")
    return deps.case_store


def _firm_store() -> FirmStore:
    deps = dependencies()
    if deps.firm_store is None:
        raise RuntimeError("firm store is not composed")
    return deps.firm_store


def _visible_case_ids(accessor: Accessor) -> frozenset[str] | None:
    """The cases this accessor may see, or None when that is every case in
    the firm. Walks the store's own listing — the by-assignee index for a
    linked-only user — so visibility here and on `GET /v1/cases` is one
    answer, not two."""
    if accessor.sees_every_case:
        return None
    store = _case_store()
    ids: set[str] = set()
    cursor: str | None = None
    while True:
        page = store.list_for_accessor(accessor, limit=100, cursor=cursor)
        ids.update(c.id for c in page.cases)
        cursor = page.next_cursor
        if cursor is None:
            return frozenset(ids)


def _window(
    accessor: Accessor,
    *,
    starting: date,
    ending: date,
    attendee: str | None = None,
    case_id: str | None = None,
) -> tuple[Event, ...]:
    """Every event the accessor may see that overlaps [starting, ending]."""
    # Widen the start by the longest an event may run — the index is keyed
    # on start, and the cap in core/events.py is what makes this exact.
    lower = day_instant(starting - timedelta(days=MAX_EVENT_SPAN_DAYS))
    upper = f"{ending.isoformat()}T23:59:59Z"
    candidates = _event_store().list_for_firm(
        accessor.firm_id, starting_from=lower, until=upper
    )
    visible = _visible_case_ids(accessor)
    window_start = day_instant(starting)
    kept: list[Event] = []
    for event in candidates:
        if end_instant(event) < window_start:
            continue
        if (
            event.case_id is not None
            and visible is not None
            and event.case_id not in visible
        ):
            continue
        if case_id is not None and event.case_id != case_id:
            continue
        if attendee is not None and attendee not in event.attendees:
            continue
        kept.append(event)
    return tuple(kept)


def _parse_date(raw: str | None, name: str) -> date:
    if raw is None or not raw.strip():
        raise ValidationError(f"{name} is required (YYYY-MM-DD)")
    try:
        return date.fromisoformat(raw.strip())
    except ValueError as error:
        raise ValidationError(f"{name} must be a date in YYYY-MM-DD form") from error


@blueprint.get("/v1/calendar")
@require_auth
@requires(EVENTS, VIEW_ONLY)
def calendar_route() -> ResponseReturnValue:
    """The events in a window, filtered per user and per case.

    `attendee=me` is the caller's own subject; any other value must be a
    subject (the client passes what the directory gave it). `case_id`
    narrows to one case — one the caller may see, or the answer is empty
    rather than a 404, because an empty month is not an oracle.
    """
    accessor = current_accessor()
    starting = _parse_date(request.args.get("from"), "from")
    ending = _parse_date(request.args.get("to"), "to")
    if ending < starting:
        raise ValidationError("to must not precede from")
    if (ending - starting).days > MAX_WINDOW_DAYS:
        raise ValidationError(f"the window may span at most {MAX_WINDOW_DAYS} days")
    attendee = request.args.get("attendee") or None
    if attendee == "me":
        attendee = accessor.subject
    case_id = request.args.get("case_id") or None
    events = _window(
        accessor, starting=starting, ending=ending, attendee=attendee, case_id=case_id
    )
    return jsonify(
        {
            "from": starting.isoformat(),
            "to": ending.isoformat(),
            "events": [event_json(e) for e in events],
        }
    ), 200


# ── The feed ────────────────────────────────────────────────────


def _feed_for(accessor: Accessor) -> Response:
    today = date.today()
    events = _window(
        accessor,
        starting=today - timedelta(days=FEED_LOOKBACK_DAYS),
        ending=today + timedelta(days=FEED_LOOKAHEAD_DAYS),
    )
    body = render_ics(events)
    return Response(
        body,
        status=200,
        mimetype="text/calendar",
        headers={
            "Content-Disposition": 'attachment; filename="insolvia.ics"',
            "Cache-Control": "private, no-store",
        },
    )


def _accessor_from_token(token: str) -> Accessor | None:
    """The firm user a feed token names, or None for any failure — a
    malformed token, an unknown subject, a disabled user, a suspended firm,
    a revoked or mismatched secret. One None for every reason, exactly as
    api/auth.py answers one 401 for every reason."""
    split = split_feed_token(token)
    if split is None:
        return None
    subject, secret = split
    firm_store = _firm_store()
    user = firm_store.find_user(subject)
    if user is None or user.status != "active":
        return None
    firm = firm_store.get_firm(user.firm_id)
    if firm is None or firm.status != "active":
        return None
    stored = _token_store().get(user.firm_id, subject)
    if stored is None or not secret_matches(stored, secret):
        return None
    accessor = Accessor(firm=firm, user=user)
    if not accessor.may(EVENTS, VIEW_ONLY):
        return None
    return accessor


@blueprint.get("/v1/me/calendar.ics")
def calendar_feed_route() -> ResponseReturnValue:
    """The caller's calendar as ICS — by feed token, or by session.

    NOT under `@require_auth`, on purpose and documented at the top of this
    module: the token path has no bearer to verify. When no token is sent
    the ordinary session applies, through the same `current_accessor()` and
    the same feature gate, so a signed-in download and a subscription see
    the same feed.
    """
    token = request.args.get("token")
    if token is not None:
        accessor = _accessor_from_token(token)
        if accessor is None:
            logger.info("calendar feed rejected", extra={"reason": "bad_token"})
            return jsonify(UNAUTHORIZED_BODY), 401
        return _feed_for(accessor)
    return _session_feed()


@require_auth
@requires(EVENTS, VIEW_ONLY)
def _session_feed() -> ResponseReturnValue:
    return _feed_for(current_accessor())


@blueprint.post("/v1/me/calendar-token")
@require_auth
@requires(EVENTS, VIEW_ONLY)
def mint_calendar_token_route() -> ResponseReturnValue:
    """Mint the caller's feed token — or rotate it, since the row is
    replaced and the old secret stops matching. The secret is returned ONCE,
    in this response, inside the feed URL; only its hash is stored."""
    accessor = current_accessor()
    secret = mint_secret()
    _token_store().put(
        CalendarToken(
            firm_id=accessor.firm_id,
            subject=accessor.subject,
            secret_hash=hash_secret(secret),
            created_at=timestamp(),
        )
    )
    logger.info("calendar token minted")
    token = feed_token(accessor.subject, secret)
    return jsonify(
        {"token": token, "feedPath": f"/v1/me/calendar.ics?token={token}"}
    ), 201


@blueprint.delete("/v1/me/calendar-token")
@require_auth
@requires(EVENTS, VIEW_ONLY)
def revoke_calendar_token_route() -> ResponseReturnValue:
    accessor = current_accessor()
    if not _token_store().delete(accessor.firm_id, accessor.subject):
        raise NotFoundError("no calendar token to revoke")
    logger.info("calendar token revoked")
    return "", 204
