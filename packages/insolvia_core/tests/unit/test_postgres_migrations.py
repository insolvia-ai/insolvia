import psycopg
import pytest
from insolvia_core.adapters.postgres.connection import connect
from insolvia_core.adapters.postgres.migrations import (
    MIGRATIONS,
    migrate,
    pending_files,
)
from psycopg.conninfo import make_conninfo


@pytest.fixture
def scratch(postgres_url, postgres):
    """A schema of its own, so these tests leave the shared one alone."""
    postgres.execute("DROP SCHEMA IF EXISTS runner CASCADE")
    postgres.execute("CREATE SCHEMA runner")
    url = make_conninfo(postgres_url, options="-c search_path=runner")
    yield url
    postgres.execute("DROP SCHEMA runner CASCADE")


def _versions(url):
    with connect(url) as conn:
        return [
            r["version"]
            for r in conn.execute(
                "SELECT version FROM schema_migrations ORDER BY version"
            )
        ]


def test_migrations_apply_in_order_and_are_recorded(scratch, tmp_path):
    (tmp_path / "0002_notes.sql").write_text("ALTER TABLE things ADD COLUMN note text;")
    (tmp_path / "0001_things.sql").write_text(
        "CREATE TABLE things (id text PRIMARY KEY); INSERT INTO things VALUES ('a');"
    )

    assert migrate(scratch, tmp_path) == ["0001", "0002"]
    assert _versions(scratch) == ["0001", "0002"]


def test_a_second_run_is_a_no_op(scratch, tmp_path):
    (tmp_path / "0001_things.sql").write_text("CREATE TABLE things (id text);")
    migrate(scratch, tmp_path)

    assert migrate(scratch, tmp_path) == []


def test_only_new_files_apply(scratch, tmp_path):
    (tmp_path / "0001_things.sql").write_text("CREATE TABLE things (id text);")
    migrate(scratch, tmp_path)
    (tmp_path / "0002_more.sql").write_text("CREATE TABLE more (id text);")

    assert migrate(scratch, tmp_path) == ["0002"]


def test_a_failing_file_leaves_the_database_untouched(scratch, tmp_path):
    (tmp_path / "0001_things.sql").write_text("CREATE TABLE things (id text);")
    (tmp_path / "0002_broken.sql").write_text("CREATE TABLE nope (;")

    with pytest.raises(psycopg.errors.SyntaxError):
        migrate(scratch, tmp_path)

    with connect(scratch) as conn:
        assert conn.execute("SELECT to_regclass('things') AS t").fetchone()["t"] is None


@pytest.mark.parametrize(
    "names",
    [["1_short.sql"], ["0001_Upper.sql"], ["0001_a.sql", "0001_b.sql"]],
)
def test_badly_named_or_duplicate_files_are_refused(tmp_path, names):
    for name in names:
        (tmp_path / name).write_text("SELECT 1;")

    with pytest.raises(ValueError, match=r"NNNN_name|share version"):
        pending_files(tmp_path)


def test_a_missing_directory_is_no_migrations(tmp_path):
    assert pending_files(tmp_path / "absent") == []


def test_the_shipped_migrations_are_applied_to_the_test_database(postgres):
    shipped = [version for version, _ in pending_files(MIGRATIONS)]
    recorded = [
        r["version"]
        for r in postgres.execute(
            "SELECT version FROM schema_migrations ORDER BY version"
        )
    ]

    assert recorded == shipped
