"""One way to open a connection, so every store reads rows the same shape."""

from __future__ import annotations

from typing import Any

import psycopg
from psycopg.rows import DictRow, dict_row


def connect(database_url: str, **kwargs: Any) -> psycopg.Connection[DictRow]:
    """A connection whose rows are dicts keyed by column name.

    Not autocommit: a store groups its statements with `conn.transaction()`
    (or the connection's own `with` block), which is how a DynamoDB
    multi-item transaction becomes one database transaction.
    """
    return psycopg.connect(database_url, row_factory=dict_row, **kwargs)
