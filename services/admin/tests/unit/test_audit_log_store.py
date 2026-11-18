"""The admin audit log over Postgres (ADR 0030): one appended row per event,
its `item` the domain's `event_item` minus the DynamoDB keys. That the admin
role may only INSERT into it is insolvia_core's grants test. Every
identifier is obviously fake."""

from __future__ import annotations

from insolvia_admin.adapters.postgres.audit_log import PostgresAuditLog
from insolvia_admin.core.audit import ACTIONS, event_item, record_event


def test_each_event_is_appended_as_its_item(postgres, postgres_url):
    log = PostgresAuditLog(postgres_url)
    first = record_event(
        firm_id="firm-0001",
        action=ACTIONS[0],
        principal="staff-sub-0001",
        principal_email="staff@example.test",
    )
    log.record(first)
    log.record(
        record_event(
            firm_id="firm-0001",
            action=ACTIONS[0],
            principal="staff-sub-0001",
            principal_email="staff@example.test",
        )
    )
    rows = postgres.execute(
        "SELECT event_id, firm_id, item FROM admin_audit_log ORDER BY recorded_at"
    ).fetchall()
    assert len(rows) == 2
    assert (rows[0]["event_id"], rows[0]["firm_id"]) == (first.event_id, "firm-0001")
    expected = {k: v for k, v in event_item(first).items() if k not in ("PK", "SK")}
    assert rows[0]["item"] == expected
