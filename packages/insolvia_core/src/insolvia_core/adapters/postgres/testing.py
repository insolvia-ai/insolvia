"""A pytest plugin: store tests get a real, clean Postgres (ADR 0030).

Load it from a `tests/conftest.py`:

    pytest_plugins = ("insolvia_core.adapters.postgres.testing",)

then ask for `postgres` (a connection to an empty, migrated database) or
`postgres_url` (its URL, for a store that opens its own connections).

Each test session creates its own database on the server, migrates it once,
and drops it at the end; each test starts from empty tables. The server is
`TEST_DATABASE_URL`, defaulting to the local Docker Postgres — start it with
`docker compose -f services/api/docker-compose.yml up -d --wait postgres`.
CI runs it as a service container with the same credentials.
"""

from __future__ import annotations

import os
from collections.abc import Iterator

import psycopg
import pytest
from psycopg import sql
from psycopg.conninfo import make_conninfo
from psycopg.rows import DictRow

from insolvia_core.adapters.postgres.connection import connect
from insolvia_core.adapters.postgres.migrations import migrate

LOCAL_SERVER = "postgresql://insolvia:insolvia@127.0.0.1:5432/postgres"


@pytest.fixture(scope="session")
def postgres_url() -> Iterator[str]:
    server = os.environ.get("TEST_DATABASE_URL") or LOCAL_SERVER
    # Per process, so two suites (or two worktrees) running at once never
    # drop each other's database.
    name = f"insolvia_test_{os.getpid()}"
    try:
        admin = psycopg.connect(server, autocommit=True)
    except psycopg.OperationalError as error:
        pytest.fail(
            f"No Postgres for store tests ({error}). Start it: "
            "docker compose -f services/api/docker-compose.yml up -d --wait postgres"
        )
    with admin:
        admin.execute(
            sql.SQL("DROP DATABASE IF EXISTS {}").format(sql.Identifier(name))
        )
        admin.execute(sql.SQL("CREATE DATABASE {}").format(sql.Identifier(name)))
        url = make_conninfo(server, dbname=name)
        migrate(url)
        yield url
        admin.execute(
            sql.SQL("DROP DATABASE {} WITH (FORCE)").format(sql.Identifier(name))
        )


@pytest.fixture
def postgres(postgres_url: str) -> Iterator[psycopg.Connection[DictRow]]:
    with connect(postgres_url, autocommit=True) as conn:
        # Emptied BEFORE the test, so one that crashed mid-way cannot leak
        # rows into the next.
        tables = [
            row["tablename"]
            for row in conn.execute(
                "SELECT tablename FROM pg_tables"
                " WHERE schemaname = 'public' AND tablename <> 'schema_migrations'"
            )
        ]
        if tables:
            conn.execute(
                sql.SQL("TRUNCATE {} CASCADE").format(
                    sql.SQL(", ").join(sql.Identifier(table) for table in tables)
                )
            )
        yield conn
