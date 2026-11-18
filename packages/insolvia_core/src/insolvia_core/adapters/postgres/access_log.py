"""AccessLog over Postgres (ADR 0030): the append-only `case_access_log`.

INSERT is the only statement this class runs, and the only one any
service's role is granted on the table (migration 0003): there is
deliberately no read path."""

from __future__ import annotations

from insolvia_core.access_log import AccessEvent, access_item
from insolvia_core.adapters.postgres.connection import connect
from insolvia_core.adapters.postgres.queries import document


class PostgresAccessLog:
    def __init__(self, database_url: str) -> None:
        self.database_url = database_url

    def record(self, event: AccessEvent) -> None:
        with connect(self.database_url) as conn:
            conn.execute(
                "INSERT INTO case_access_log (event_id, subject_key, recorded_at, item)"
                " VALUES (%s, %s, %s, %s)",
                (
                    event.event_id,
                    event.subject_key,
                    event.recorded_at,
                    document(access_item(event)),
                ),
            )
