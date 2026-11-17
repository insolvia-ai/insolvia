"""The forward-only migration runner (ADR 0030).

Applies `migrations/NNNN_<name>.sql` in version order, recording each in
`schema_migrations`. There is no down migration: a mistake is corrected by
the next numbered file. A file is never edited once merged — the runner
records versions, not checksums, so an edit would never reach a database
that already applied it.

    DATABASE_URL=postgresql://… python -m insolvia_core.adapters.postgres.migrations
"""

from __future__ import annotations

import os
import re
import sys
from pathlib import Path

from insolvia_core.adapters.postgres.connection import connect

MIGRATIONS = Path(__file__).with_name("migrations")

_FILE = re.compile(r"^(\d{4})_[a-z0-9_]+\.sql$")

# Any fixed number: it only has to be the same in every runner. Two runners
# at once (the compose migrator and a deploy, two test sessions) queue on it
# instead of both applying 0001.
_LOCK = 3_000_030


def pending_files(directory: Path) -> list[tuple[str, Path]]:
    """Every migration file in `directory` as (version, path), in order.

    A missing directory is no migrations, not an error: the runner ships
    before the first table does (#620 → #621).
    """
    found: dict[str, Path] = {}
    for path in sorted(directory.glob("*.sql")) if directory.is_dir() else []:
        match = _FILE.match(path.name)
        if match is None:
            raise ValueError(f"migration file name must be NNNN_name.sql: {path.name}")
        version = match.group(1)
        if version in found:
            raise ValueError(f"two migrations share version {version}")
        found[version] = path
    return sorted(found.items())


def migrate(database_url: str, directory: Path = MIGRATIONS) -> list[str]:
    """Apply every migration not yet recorded; return the versions applied.

    One transaction for the whole run: Postgres DDL is transactional, so a
    failing file leaves the database exactly as it was, never half-migrated.
    """
    files = pending_files(directory)
    applied: list[str] = []
    with connect(database_url) as conn, conn.transaction():
        conn.execute("SELECT pg_advisory_xact_lock(%s)", (_LOCK,))
        conn.execute(
            "CREATE TABLE IF NOT EXISTS schema_migrations ("
            " version text PRIMARY KEY,"
            " name text NOT NULL,"
            " applied_at timestamptz NOT NULL DEFAULT now())"
        )
        done = {
            row["version"]
            for row in conn.execute("SELECT version FROM schema_migrations")
        }
        for version, path in files:
            if version in done:
                continue
            # bytes, not str: a file may hold several statements, which
            # psycopg runs in one call only when there are no parameters.
            conn.execute(path.read_bytes())
            conn.execute(
                "INSERT INTO schema_migrations (version, name) VALUES (%s, %s)",
                (version, path.name),
            )
            applied.append(version)
    return applied


def main() -> int:
    database_url = os.environ.get("DATABASE_URL", "")
    if not database_url:
        print("DATABASE_URL is not set — nothing to migrate.", file=sys.stderr)
        return 1
    applied = migrate(database_url)
    print(f"applied {', '.join(applied)}" if applied else "database is up to date")
    return 0


if __name__ == "__main__":
    sys.exit(main())
