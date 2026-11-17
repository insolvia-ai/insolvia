import pytest
from insolvia_core.adapters.postgres.queries import keyset_after, next_key, require_rows
from insolvia_core.cases import decode_cursor, encode_cursor
from insolvia_core.errors import ConflictError, ValidationError
from psycopg import sql


@pytest.fixture
def items(postgres):
    postgres.execute(
        "CREATE TEMP TABLE items ("
        " id text PRIMARY KEY, status text NOT NULL, created_at timestamptz NOT NULL)"
    )
    for i in range(5):
        postgres.execute(
            "INSERT INTO items VALUES (%s, 'open', %s)",
            (f"i{i}", f"2026-11-1{i} 09:00:00+00"),
        )
    return postgres


def test_zero_rows_updated_raises_the_stores_domain_error(items):
    cursor = items.execute(
        "UPDATE items SET status = 'closed' WHERE id = 'i0' AND status = 'filed'"
    )

    with pytest.raises(ConflictError):
        require_rows(cursor, ConflictError("moved"))


def test_a_matching_update_does_not_raise(items):
    cursor = items.execute(
        "UPDATE items SET status = 'closed' WHERE id = 'i0' AND status = 'open'"
    )

    require_rows(cursor, ConflictError("moved"))


def _page(conn, after, *, limit=2, descending=False):
    columns = ("created_at", "id")
    where, params = keyset_after(columns, after, descending=descending)
    order = "DESC" if descending else "ASC"
    query = sql.SQL(
        "SELECT * FROM items WHERE {} ORDER BY created_at {}, id {} LIMIT %s"
    ).format(where, sql.SQL(order), sql.SQL(order))
    rows = conn.execute(query, [*params, limit + 1]).fetchall()
    return [r["id"] for r in rows[:limit]], next_key(rows, limit, columns)


@pytest.mark.parametrize(
    ("descending", "expected"),
    [(False, ["i0", "i1", "i2", "i3", "i4"]), (True, ["i4", "i3", "i2", "i1", "i0"])],
)
def test_keyset_pages_walk_the_whole_listing_once_through_an_opaque_cursor(
    items, descending, expected
):
    seen, cursor = [], None
    while True:
        after = None if cursor is None else decode_cursor(cursor, index="items")
        ids, key = _page(items, after, descending=descending)
        seen += ids
        if key is None:
            break
        cursor = encode_cursor(key, index="items")

    assert seen == expected


@pytest.mark.parametrize(
    "after", [{"id": "i1"}, {"id": "i1", "created_at": "x", "extra": "y"}]
)
def test_a_cursor_with_the_wrong_keys_is_refused(after):
    with pytest.raises(ValidationError):
        keyset_after(("created_at", "id"), after)
