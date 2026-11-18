"""The two query shapes every Postgres store repeats (ADR 0030).

A conditional write is `UPDATE … WHERE <condition>`, and zero rows affected
must raise the store's domain error. That error
differs per store (a 409 `ConflictError`, a 404 `None`, …), so the store
passes it in.

A keyset cursor: `WHERE (sort columns) > (last row's values)` instead of an
OFFSET. The values travel inside the existing opaque
cursor (`insolvia_core.cases.encode_cursor`/`decode_cursor`), so the wire
shape clients already hold does not change.
"""

from __future__ import annotations

from collections.abc import Mapping, Sequence
from datetime import UTC, datetime
from typing import Any

from psycopg import Cursor, sql
from psycopg.types.json import Jsonb

from insolvia_core.errors import ValidationError


def require_rows(cursor: Cursor[Any], error: Exception) -> None:
    """Raise `error` when the statement just run touched no row."""
    if cursor.rowcount == 0:
        raise error


def keyset_after(
    columns: Sequence[str],
    after: Mapping[str, str] | None,
    *,
    descending: bool = False,
) -> tuple[sql.Composable, list[str]]:
    """The WHERE fragment and parameters that resume a listing after `after`.

    `columns` is the listing's ORDER BY, ending in a unique column so no two
    rows tie. `after` is a decoded cursor (None on the first page), and it is
    caller-controlled, so its keys must be exactly `columns`.

    shortcut: one direction for every column (a row comparison); a listing
    that sorts columns in mixed directions needs the expanded OR form.
    """
    if after is None:
        return sql.SQL("TRUE"), []
    if set(after) != set(columns):
        raise ValidationError("cursor is not valid")
    fragment = sql.SQL("({}) {} ({})").format(
        sql.SQL(", ").join(sql.Identifier(column) for column in columns),
        sql.SQL("<" if descending else ">"),
        sql.SQL(", ").join(sql.Placeholder() for _ in columns),
    )
    return fragment, [after[column] for column in columns]


def next_key(
    rows: Sequence[Mapping[str, Any]], limit: int, columns: Sequence[str]
) -> dict[str, str] | None:
    """The key the next page resumes after, or None on the last page.

    The query fetches `limit + 1` rows: the extra row only proves another
    page exists, and is not returned. Values are strings (what the cursor
    carries); Postgres reads them back as the column's own type.
    """
    if len(rows) <= limit:
        return None
    last = rows[limit - 1]
    return {column: str(last[column]) for column in columns}


def document(item: Mapping[str, object]) -> Jsonb:
    """A domain item shape (`*_item`) as the `item jsonb` a child row stores:
    the same document, minus the item's key attributes (PK, SK, GSI*), which
    are the row's columns now. `*_from_item` reads it back unchanged."""
    return Jsonb(
        {
            key: value
            for key, value in item.items()
            if key not in ("PK", "SK") and not key.startswith("GSI")
        }
    )


def stamp(value: datetime) -> str:
    """A `timestamptz` back as the domain's timestamp text: microsecond UTC
    with a literal Z (`cases._timestamp`), so a value round-trips exactly."""
    return (
        value.astimezone(UTC).isoformat(timespec="microseconds").replace("+00:00", "Z")
    )
