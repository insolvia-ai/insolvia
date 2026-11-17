"""The one shape most case children share (ADR 0030): a table keyed
`(case_id, …, id)`, carrying `firm_id` copied from the case and the domain's
stored item as `item jsonb`. Each store wraps one `ChildTable` and turns its
items into domain records with the domain's own `*_from_item`."""

from __future__ import annotations

from collections.abc import Mapping
from typing import Any

import psycopg
from psycopg import sql

from insolvia_core.adapters.postgres.connection import connect
from insolvia_core.adapters.postgres.queries import document


class ChildTable:
    def __init__(self, database_url: str, table: str, keys: tuple[str, ...]) -> None:
        """`keys` are the primary key's columns, `case_id` first."""
        self.database_url = database_url
        self.table = sql.Identifier(table)
        self.keys = keys

    def _where(self, key: Mapping[str, str]) -> sql.Composable:
        return sql.SQL(" AND ").join(
            sql.SQL("{} = %s").format(sql.Identifier(column)) for column in self.keys
        )

    def create(self, key: Mapping[str, str], item: Mapping[str, object]) -> None:
        """Insert under the case's firm. MUST refuse an existing key: ids are
        server-minted uuid4s, and replacing would erase a record to hide that
        the minting broke. A missing case raises too."""
        columns = [*self.keys, "firm_id", "item"]
        statement = sql.SQL(
            "INSERT INTO {} ({}) SELECT {}, firm_id, %s FROM cases WHERE id = %s"
        ).format(
            self.table,
            sql.SQL(", ").join(map(sql.Identifier, columns)),
            sql.SQL(", ").join([sql.Placeholder()] * len(self.keys)),
        )
        try:
            with connect(self.database_url) as conn:
                inserted = conn.execute(
                    statement,
                    [*(key[k] for k in self.keys), document(item), key["case_id"]],
                ).rowcount
        except psycopg.errors.UniqueViolation as error:
            raise RuntimeError(f"{dict(key)} already exists") from error
        if inserted == 0:
            raise RuntimeError(f"no case {key['case_id']}")

    def get(self, key: Mapping[str, str]) -> dict[str, Any] | None:
        with connect(self.database_url) as conn:
            row = conn.execute(
                sql.SQL("SELECT item FROM {} WHERE {}").format(
                    self.table, self._where(key)
                ),
                [key[k] for k in self.keys],
            ).fetchone()
        return None if row is None else dict(row["item"])

    def replace(
        self,
        key: Mapping[str, str],
        item: Mapping[str, object],
        *,
        expected_status: str | None = None,
    ) -> bool:
        """Overwrite an existing row only — never resurrect a deleted one —
        and, with `expected_status`, only while the stored item's status is
        that (the compare-and-set a status transition rides on)."""
        condition = self._where(key)
        params: list[object] = [document(item), *(key[k] for k in self.keys)]
        if expected_status is not None:
            condition = sql.SQL("{} AND item ->> 'status' = %s").format(condition)
            params.append(expected_status)
        with connect(self.database_url) as conn:
            updated = conn.execute(
                sql.SQL("UPDATE {} SET item = %s WHERE {}").format(
                    self.table, condition
                ),
                params,
            ).rowcount
        return updated > 0

    def delete(self, key: Mapping[str, str]) -> bool:
        """True only if this call removed the row — a delete of nothing is
        not a success a caller may act on."""
        with connect(self.database_url) as conn:
            deleted = conn.execute(
                sql.SQL("DELETE FROM {} WHERE {}").format(self.table, self._where(key)),
                [key[k] for k in self.keys],
            ).rowcount
        return deleted > 0

    def list(self, key: Mapping[str, str]) -> list[dict[str, Any]]:
        """Every item under a key prefix (`case_id`, then any further key
        columns given). Unordered: each store sorts with its domain's
        `list_order`, the one definition its memory twin uses too."""
        columns = [k for k in self.keys if k in key]
        with connect(self.database_url) as conn:
            rows = conn.execute(
                sql.SQL("SELECT item FROM {} WHERE {}").format(
                    self.table,
                    sql.SQL(" AND ").join(
                        sql.SQL("{} = %s").format(sql.Identifier(c)) for c in columns
                    ),
                ),
                [key[c] for c in columns],
            ).fetchall()
        return [dict(row["item"]) for row in rows]
