from __future__ import annotations

from insolvia_core.adapters.postgres.connection import connect
from insolvia_core.adapters.postgres.firm_store import stored

from insolvia_api.core.waitlist import WaitlistRecord, record_item


class PostgresWaitlistStore:
    """WaitlistStore backed by migration 0001's `waitlist` table. Append-only:
    the API only ever inserts."""

    def __init__(self, database_url: str) -> None:
        self.database_url = database_url

    def add(self, record: WaitlistRecord) -> None:
        with connect(self.database_url) as conn:
            conn.execute(
                "INSERT INTO waitlist (id, submitted_at, item) VALUES (%s, %s, %s)",
                (record.id, record.submitted_at, stored(record_item(record))),
            )
