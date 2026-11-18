"""AuditLog over Postgres (ADR 0030): the append-only `admin_audit_log`.

INSERT is the only statement this class runs, and the only one the admin
role is granted on the table (migration 0003) — a read here would fail at
the grant, which is the point. The event id is a fresh uuid, so a collision
is not a state this can reach."""

from __future__ import annotations

from insolvia_core.adapters.postgres.connection import connect
from insolvia_core.adapters.postgres.queries import document

from insolvia_admin.core.audit import AdminEvent, event_item


class PostgresAuditLog:
    def __init__(self, database_url: str) -> None:
        self.database_url = database_url

    def record(self, event: AdminEvent) -> None:
        with connect(self.database_url) as conn:
            conn.execute(
                "INSERT INTO admin_audit_log (event_id, firm_id, recorded_at, item)"
                " VALUES (%s, %s, %s, %s)",
                (
                    event.event_id,
                    event.firm_id,
                    event.recorded_at,
                    document(event_item(event)),
                ),
            )
