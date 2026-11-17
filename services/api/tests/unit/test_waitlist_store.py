"""The Postgres waitlist store (ADR 0030): a submission lands as one row in
migration 0001's table, in `record_item`'s shape. Obviously fake values."""

from insolvia_api.adapters.postgres.waitlist_store import PostgresWaitlistStore
from insolvia_api.core.waitlist import (
    WaitlistSubmission,
    create_waitlist_record,
    record_item,
)


def test_a_submission_is_stored_as_its_item(postgres, postgres_url):
    record = create_waitlist_record(
        WaitlistSubmission(
            name="Ada Example",
            firm="Example & Partners",
            email="ada@example.test",
            current_software="",
            message="",
            host="",
        )
    )

    PostgresWaitlistStore(postgres_url).add(record)

    [row] = postgres.execute("SELECT id, item FROM waitlist").fetchall()
    expected = {k: v for k, v in record_item(record).items() if k not in ("PK", "SK")}
    assert row == {"id": record.id, "item": expected}
