"""FilingStore over Postgres (ADR 0030): the `filings` table, its `item` the
domain's `filing_item` document (insolvia_core.filings owns the shape; this
adapter owns only the conditions).

Every write is conditional, and the conditions are the idempotency:

- `claim`      INSERT … ON CONFLICT DO NOTHING — one record per filing;
- `transition` UPDATE … WHERE state = expected AND attemptId = attempt —
               only the attempt that holds the record moves it, and only
               from the state it read;
- `resolve`    the same, AND no resolution yet — a hand-back is resolved once.

With a `FiledCase` (ADR 0024 PR 8) either of the last two also files the
case IN THE SAME TRANSACTION: an UPDATE of exactly the lifecycle columns the
move changes, conditional on the case still existing in its firm, undeleted,
with the status the caller read; and the status-history row. Any refused
condition rolls the whole transaction back.
"""

from __future__ import annotations

import psycopg
from psycopg import sql
from psycopg.rows import DictRow

from insolvia_core.adapters.postgres.case_store import case_values
from insolvia_core.adapters.postgres.connection import connect
from insolvia_core.adapters.postgres.queries import document
from insolvia_core.filings import FiledCase, Filing, filing_from_item, filing_item

# The case columns an electronic filing changes — and nothing else, so a
# concurrent edit of anything else on the case is never written back over.
_FILED_COLUMNS = ("status", "filed_at", "case_number", "updated_at", "case_number_key")


class _RefusedError(Exception):
    """A condition failed: roll back, answer False."""


class PostgresFilingStore:
    def __init__(self, database_url: str) -> None:
        self.database_url = database_url

    def get(self, case_id: str, filing_id: str) -> Filing | None:
        with connect(self.database_url) as conn:
            row = conn.execute(
                "SELECT item FROM filings WHERE case_id = %s AND filing_id = %s",
                (case_id, filing_id),
            ).fetchone()
        return None if row is None else filing_from_item(row["item"])

    def claim(self, filing: Filing) -> bool:
        with connect(self.database_url) as conn:
            inserted = conn.execute(
                "INSERT INTO filings (case_id, filing_id, firm_id, item)"
                " VALUES (%s, %s, %s, %s) ON CONFLICT DO NOTHING",
                (
                    filing.case_id,
                    filing.filing_id,
                    filing.firm_id,
                    document(filing_item(filing)),
                ),
            ).rowcount
        return inserted > 0

    def transition(
        self,
        filing: Filing,
        *,
        expected_state: str,
        filed_case: FiledCase | None = None,
    ) -> bool:
        return self._write(
            filing,
            expected_state=expected_state,
            filed_case=filed_case,
            unresolved=False,
        )

    def resolve(
        self,
        filing: Filing,
        *,
        expected_state: str,
        filed_case: FiledCase | None = None,
    ) -> bool:
        return self._write(
            filing,
            expected_state=expected_state,
            filed_case=filed_case,
            unresolved=True,
        )

    def _write(
        self,
        filing: Filing,
        *,
        expected_state: str,
        filed_case: FiledCase | None,
        unresolved: bool,
    ) -> bool:
        condition = "item ->> 'state' = %s AND item ->> 'attemptId' = %s"
        if unresolved:
            condition += " AND item -> 'resolution' IS NULL"
        try:
            with connect(self.database_url) as conn, conn.transaction():
                updated = conn.execute(
                    "UPDATE filings SET item = %s"
                    " WHERE case_id = %s AND filing_id = %s AND " + condition,
                    (
                        document(filing_item(filing)),
                        filing.case_id,
                        filing.filing_id,
                        expected_state,
                        filing.attempt_id,
                    ),
                ).rowcount
                if updated == 0:
                    raise _RefusedError
                if filed_case is not None:
                    _file_case(conn, filed_case)
        except _RefusedError:
            return False
        return True


def _file_case(conn: psycopg.Connection[DictRow], filed_case: FiledCase) -> None:
    case = filed_case.case
    values = case_values(case)
    updated = conn.execute(
        sql.SQL(
            "UPDATE cases SET {} WHERE id = %s AND firm_id = %s AND status = %s"
            " AND deleted_at IS NULL"
        ).format(
            sql.SQL(", ").join(
                sql.SQL("{} = %s").format(sql.Identifier(column))
                for column in _FILED_COLUMNS
            )
        ),
        [
            *(values[column] for column in _FILED_COLUMNS),
            case.id,
            case.firm_id,
            filed_case.expected_status,
        ],
    ).rowcount
    if updated == 0:
        raise _RefusedError
    change = filed_case.status_change
    try:
        with conn.transaction():  # a savepoint, so the refusal is catchable
            conn.execute(
                "INSERT INTO case_status_changes (case_id, changed_at, firm_id,"
                " changed_by, from_status, to_status, filing_id)"
                " VALUES (%s, %s, %s, %s, %s, %s, %s)",
                (
                    change.case_id,
                    change.changed_at,
                    case.firm_id,
                    change.changed_by,
                    change.from_status,
                    change.to_status,
                    change.filing_id,
                ),
            )
    except psycopg.errors.UniqueViolation as error:
        raise _RefusedError from error
