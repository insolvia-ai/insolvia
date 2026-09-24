"""The per-user ICS feed and the token that authenticates it (issue 14.6 /
#358).

A calendar application subscribes to a feed by URL and fetches it on a
schedule with NO way to send a bearer token — so `GET /v1/me/calendar.ics`
takes either the session (a signed-in download) or a FEED TOKEN in the
query string. The token is a capability: whoever holds it reads that one
user's calendar, nothing else, and it is minted, rotated and revoked from
the account screen without touching the pool.

The token is `<subject>.<secret>`. The subject is in the clear so the API
can resolve the firm user through the existing by-subject index — the only
lookup that needs no firm — and the secret is 32 random bytes, stored only
as a SHA-256 hash and compared in constant time. A leaked table row yields
no usable token; a leaked token is revoked by minting a new one. The feed
URL carries the secret and so is never logged: the request logger records
paths only (no query strings) by design, which is the one thing that makes
a query-string credential acceptable here at all.

The ICS itself follows RFC 5545: one VEVENT per event, all-day events as
DATE values with an EXCLUSIVE end (the one place this shape differs from
the wire's inclusive `end`), timed events as UTC instants, text escaped and
lines folded at 75 octets. Dismissed deadlines are omitted — dismissing one
means "this does not apply", and a subscriber's calendar should agree.
"""

from __future__ import annotations

import hashlib
import hmac
import secrets
from collections.abc import Iterable
from dataclasses import dataclass
from datetime import UTC, date, datetime, timedelta

from .events import Event, valid_subject

_TOKEN_BYTES = 32


@dataclass(frozen=True)
class CalendarToken:
    """The stored half: whose feed, and the hash of the secret that opens it."""

    firm_id: str
    subject: str
    secret_hash: str
    created_at: str


def mint_secret() -> str:
    return secrets.token_urlsafe(_TOKEN_BYTES)


def hash_secret(secret: str) -> str:
    return hashlib.sha256(secret.encode("utf-8")).hexdigest()


def feed_token(subject: str, secret: str) -> str:
    return f"{subject}.{secret}"


def split_feed_token(token: str) -> tuple[str, str] | None:
    """`<subject>.<secret>`, or None for anything that is not that shape —
    the shape check is what keeps a malformed token from reaching the
    store as a lookup key."""
    subject, dot, secret = token.partition(".")
    if not dot or not secret or not valid_subject(subject):
        return None
    return subject, secret


def secret_matches(stored: CalendarToken, secret: str) -> bool:
    return hmac.compare_digest(stored.secret_hash, hash_secret(secret))


def token_item(token: CalendarToken) -> dict[str, object]:
    """PK FIRM#<firm_id> / SK CALTOKEN#<subject>, in the case table beside
    the firm's own events — one row per person, replaced on rotation."""
    return {
        "PK": f"FIRM#{token.firm_id}",
        "SK": f"CALTOKEN#{token.subject}",
        "firmId": token.firm_id,
        "subject": token.subject,
        "secretHash": token.secret_hash,
        "createdAt": token.created_at,
    }


def token_from_item(item: dict[str, object]) -> CalendarToken:
    return CalendarToken(
        firm_id=str(item["firmId"]),
        subject=str(item["subject"]),
        secret_hash=str(item["secretHash"]),
        created_at=str(item["createdAt"]),
    )


# --- ICS ---------------------------------------------------------------------


def _escape(value: str) -> str:
    return (
        value.replace("\\", "\\\\")
        .replace(";", "\\;")
        .replace(",", "\\,")
        .replace("\r\n", "\\n")
        .replace("\n", "\\n")
    )


def _fold(line: str) -> list[str]:
    """RFC 5545 §3.1: lines longer than 75 octets are folded with CRLF plus
    a single space. Folded on characters rather than octets for simplicity;
    a multi-byte character can push a physical line a few octets over 75,
    which every parser tolerates."""
    if len(line) <= 75:
        return [line]
    chunks = [line[:75]]
    rest = line[75:]
    while rest:
        chunks.append(" " + rest[:74])
        rest = rest[74:]
    return chunks


def _ics_date(value: str) -> str:
    return value.replace("-", "")


def _ics_instant(value: str) -> str:
    return value.replace("-", "").replace(":", "")


def _vevent(event: Event, stamp: str) -> list[str]:
    lines = ["BEGIN:VEVENT", f"UID:{event.id}@insolvia", f"DTSTAMP:{stamp}"]
    if event.all_day:
        exclusive_end = date.fromisoformat(event.end) + timedelta(days=1)
        lines.append(f"DTSTART;VALUE=DATE:{_ics_date(event.start)}")
        lines.append(f"DTEND;VALUE=DATE:{_ics_date(exclusive_end.isoformat())}")
    else:
        lines.append(f"DTSTART:{_ics_instant(event.start)}")
        lines.append(f"DTEND:{_ics_instant(event.end)}")
    lines.append(f"SUMMARY:{_escape(event.title)}")
    description_parts = []
    if event.rule_citation is not None:
        description_parts.append(event.rule_citation)
    if event.description is not None:
        description_parts.append(event.description)
    if description_parts:
        lines.append(f"DESCRIPTION:{_escape(chr(10).join(description_parts))}")
    if event.location is not None:
        lines.append(f"LOCATION:{_escape(event.location)}")
    if event.rule_id is not None:
        lines.append("CATEGORIES:Deadline")
    lines.append("END:VEVENT")
    return lines


def render_ics(events: Iterable[Event], *, now: datetime | None = None) -> str:
    """The whole feed, CRLF-terminated, dismissed deadlines left out."""
    stamp = (now or datetime.now(UTC)).astimezone(UTC).strftime("%Y%m%dT%H%M%SZ")
    lines = [
        "BEGIN:VCALENDAR",
        "VERSION:2.0",
        "PRODID:-//Insolvia//Case calendar//EN",
        "CALSCALE:GREGORIAN",
        "METHOD:PUBLISH",
        "X-WR-CALNAME:Insolvia",
    ]
    for event in events:
        if event.dismissed:
            continue
        lines.extend(_vevent(event, stamp))
    lines.append("END:VCALENDAR")
    folded: list[str] = []
    for line in lines:
        folded.extend(_fold(line))
    return "\r\n".join(folded) + "\r\n"
