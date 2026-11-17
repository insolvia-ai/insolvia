"""EventStore and CalendarTokenStore over Postgres (ADR 0030): the `events`
table — a case event (case_id set) or a firm event (case_id NULL) — and
`calendar_tokens`, one per firm member."""

from __future__ import annotations

import psycopg
from insolvia_core.adapters.postgres.connection import connect
from insolvia_core.adapters.postgres.queries import document

from insolvia_api.core.calendar_feed import (
    CalendarToken,
    token_from_item,
    token_item,
)
from insolvia_api.core.events import (
    Event,
    EventScope,
    event_from_item,
    event_item,
    list_order,
    partition_key,
    sort_instant,
)


class PostgresEventStore:
    def __init__(self, database_url: str) -> None:
        self.database_url = database_url

    def create(self, event: Event) -> None:
        try:
            with connect(self.database_url) as conn:
                conn.execute(
                    "INSERT INTO events (scope, id, firm_id, case_id, sort_key, item)"
                    " VALUES (%s, %s, %s, %s, %s, %s)",
                    (
                        partition_key(event.scope),
                        event.id,
                        event.firm_id,
                        event.scope.case_id,
                        sort_instant(event),
                        document(event_item(event)),
                    ),
                )
        except psycopg.errors.UniqueViolation as error:
            raise RuntimeError("event id already exists in this scope") from error

    def get(self, scope: EventScope, event_id: str) -> Event | None:
        with connect(self.database_url) as conn:
            row = conn.execute(
                "SELECT item FROM events WHERE scope = %s AND id = %s",
                (partition_key(scope), event_id),
            ).fetchone()
        return None if row is None else event_from_item(row["item"])

    def put(self, event: Event) -> bool:
        with connect(self.database_url) as conn:
            updated = conn.execute(
                "UPDATE events SET sort_key = %s, item = %s"
                " WHERE scope = %s AND id = %s",
                (
                    sort_instant(event),
                    document(event_item(event)),
                    partition_key(event.scope),
                    event.id,
                ),
            ).rowcount
        return updated > 0

    def delete(self, scope: EventScope, event_id: str) -> bool:
        with connect(self.database_url) as conn:
            deleted = conn.execute(
                "DELETE FROM events WHERE scope = %s AND id = %s",
                (partition_key(scope), event_id),
            ).rowcount
        return deleted > 0

    def list_for_scope(self, scope: EventScope) -> tuple[Event, ...]:
        with connect(self.database_url) as conn:
            rows = conn.execute(
                "SELECT item FROM events WHERE scope = %s", (partition_key(scope),)
            ).fetchall()
        return tuple(sorted((event_from_item(r["item"]) for r in rows), key=list_order))

    def list_for_firm(
        self, firm_id: str, *, starting_from: str, until: str
    ) -> tuple[Event, ...]:
        # Inclusive both ends, compared byte-wise (the column is COLLATE "C"),
        # exactly as the memory twin compares the same strings.
        with connect(self.database_url) as conn:
            rows = conn.execute(
                "SELECT item FROM events WHERE firm_id = %s"
                " AND sort_key BETWEEN %s AND %s",
                (firm_id, starting_from, until),
            ).fetchall()
        return tuple(sorted((event_from_item(r["item"]) for r in rows), key=list_order))


class PostgresCalendarTokenStore:
    def __init__(self, database_url: str) -> None:
        self.database_url = database_url

    def put(self, token: CalendarToken) -> None:
        # Replaced on rotation: one row per person.
        with connect(self.database_url) as conn:
            conn.execute(
                "INSERT INTO calendar_tokens (firm_id, subject, item)"
                " VALUES (%s, %s, %s)"
                " ON CONFLICT (firm_id, subject) DO UPDATE SET item = EXCLUDED.item",
                (token.firm_id, token.subject, document(token_item(token))),
            )

    def get(self, firm_id: str, subject: str) -> CalendarToken | None:
        with connect(self.database_url) as conn:
            row = conn.execute(
                "SELECT item FROM calendar_tokens WHERE firm_id = %s AND subject = %s",
                (firm_id, subject),
            ).fetchone()
        return None if row is None else token_from_item(dict(row["item"]))

    def delete(self, firm_id: str, subject: str) -> bool:
        with connect(self.database_url) as conn:
            deleted = conn.execute(
                "DELETE FROM calendar_tokens WHERE firm_id = %s AND subject = %s",
                (firm_id, subject),
            ).rowcount
        return deleted > 0
