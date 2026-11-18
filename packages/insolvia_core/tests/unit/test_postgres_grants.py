"""The per-service roles' grants (ADR 0030; migration 0003), proven by acting
as each role: append-only logs, a vault the api role may fill and empty but
never read the secret of, and the filing role alone reading it.

Each statement runs under `SET LOCAL ROLE <role>` in a transaction that is
always rolled back, so a permitted write leaves nothing behind. Locally the
roles have no password, so nothing logs in as one (a deployed environment
logs them in with IAM auth, infra/modules/database); `SET ROLE` takes
exactly their privileges, which is what is tested.
"""

from __future__ import annotations

from typing import Any

import psycopg
import pytest
from insolvia_core.access_log import access_item, record_access
from insolvia_core.adapters.postgres.access_log import PostgresAccessLog
from psycopg import sql

APP_ROLES = ("api", "worker", "admin", "mcp", "filing")

# Tables whose grants are narrower than "every service reads and writes".
NARROW = {
    "schema_migrations",
    "tax_ids",
    "filing_credentials",
    "filing_authorizations",
    "filing_authorization_current",
    "case_access_log",
    "admin_audit_log",
}

ACCESS_ROW = (
    "INSERT INTO case_access_log (event_id, subject_key, recorded_at, item)"
    " VALUES ('e1', 'CASE#c1', now(), '{}')"
)
AUDIT_ROW = (
    "INSERT INTO admin_audit_log (event_id, firm_id, recorded_at, item)"
    " VALUES ('e1', 'f1', now(), '{}')"
)
CREDENTIAL_ROW = (
    "INSERT INTO filing_credentials (firm_id, attorney_id, credential_id, login,"
    " courts, status, created_at, updated_at, scheme, ciphertext, nonce, wrapped_key)"
    " VALUES ('f1', 'a1', 'c1', 'L', '[]', 'active', 't', 't', 's', '\\x00',"
    " '\\x00', '\\x00')"
)
STATUS_COLUMNS = (
    "SELECT firm_id, attorney_id, credential_id, login, courts, status,"
    " authorization_ref, created_at, updated_at FROM filing_credentials"
)


def may(conn: psycopg.Connection[Any], role: str, statement: str) -> bool:
    """Whether `role` may run `statement` — run for real, then rolled back."""
    try:
        with conn.transaction(force_rollback=True):
            conn.execute(sql.SQL("SET LOCAL ROLE {}").format(sql.Identifier(role)))
            conn.execute(statement.encode())
    except psycopg.errors.InsufficientPrivilege:
        return False
    return True


@pytest.mark.parametrize("role", APP_ROLES)
def test_the_access_log_is_insert_only(postgres: Any, role: str) -> None:
    assert may(postgres, role, ACCESS_ROW)
    for statement in (
        "SELECT * FROM case_access_log",
        "UPDATE case_access_log SET item = '{}'",
        "DELETE FROM case_access_log",
        "TRUNCATE case_access_log",
    ):
        assert not may(postgres, role, statement), statement


@pytest.mark.parametrize("role", APP_ROLES)
def test_the_admin_audit_log_is_insert_only_and_the_admins_alone(
    postgres: Any, role: str
) -> None:
    assert may(postgres, role, AUDIT_ROW) == (role == "admin")
    for statement in (
        "SELECT * FROM admin_audit_log",
        "UPDATE admin_audit_log SET item = '{}'",
        "DELETE FROM admin_audit_log",
        "TRUNCATE admin_audit_log",
    ):
        assert not may(postgres, role, statement), statement


def test_the_api_fills_and_empties_the_vault_but_never_reads_the_secret(
    postgres: Any,
) -> None:
    assert may(postgres, "api", CREDENTIAL_ROW)
    assert may(postgres, "api", STATUS_COLUMNS)
    assert may(
        postgres,
        "api",
        "DELETE FROM filing_credentials"
        " WHERE firm_id = 'f1' AND attorney_id = 'a1' AND credential_id = 'c1'",
    )
    for statement in (
        "SELECT * FROM filing_credentials",
        "SELECT ciphertext FROM filing_credentials",
        "SELECT nonce FROM filing_credentials",
        "SELECT wrapped_key FROM filing_credentials",
        "SELECT scheme FROM filing_credentials",
        # Reading the secret back through a write is reading it too.
        "DELETE FROM filing_credentials RETURNING ciphertext",
        "UPDATE filing_credentials SET status = 'x'",
    ):
        assert not may(postgres, "api", statement), statement


def test_the_filing_worker_alone_reads_the_vault(postgres: Any) -> None:
    assert may(postgres, "filing", "SELECT * FROM filing_credentials")
    for statement in (
        CREDENTIAL_ROW,
        "UPDATE filing_credentials SET status = 'x'",
        "DELETE FROM filing_credentials",
    ):
        assert not may(postgres, "filing", statement), statement
    for role in ("worker", "admin", "mcp"):
        for statement in (
            "SELECT * FROM filing_credentials",
            STATUS_COLUMNS,
            CREDENTIAL_ROW,
            "DELETE FROM filing_credentials",
        ):
            assert not may(postgres, role, statement), (role, statement)


@pytest.mark.parametrize(
    "table", ["filing_authorizations", "filing_authorization_current"]
)
def test_authorizations_are_the_apis_and_the_filing_worker_reads_them(
    postgres: Any, table: str
) -> None:
    select = f"SELECT * FROM {table}"
    delete = f"DELETE FROM {table}"
    assert may(postgres, "api", select)
    assert may(postgres, "api", delete)
    assert may(postgres, "filing", select)
    assert not may(postgres, "filing", delete)
    for role in ("worker", "admin", "mcp"):
        assert not may(postgres, role, select), role


def test_tax_ids_are_written_by_the_api_and_admin_and_never_reach_mcp(
    postgres: Any,
) -> None:
    for role in ("api", "admin"):
        assert may(postgres, role, "DELETE FROM tax_ids"), role
    for role in ("worker", "filing"):
        assert may(postgres, role, "SELECT * FROM tax_ids"), role
        assert not may(postgres, role, "DELETE FROM tax_ids"), role
    assert not may(postgres, "mcp", "SELECT * FROM tax_ids")


def test_every_other_table_is_granted_to_every_service(postgres: Any) -> None:
    """A table a later migration adds without grants fails here, rather than
    as a permission error in a deployed service."""
    tables = [
        row["tablename"]
        for row in postgres.execute(
            "SELECT tablename FROM pg_tables WHERE schemaname = 'public'"
        )
    ]
    assert set(tables) >= NARROW
    # One privilege per call: given a list, has_table_privilege answers ANY.
    missing = [
        (role, table, privilege)
        for table in tables
        if table not in NARROW
        for role in APP_ROLES
        for privilege in ("SELECT", "INSERT", "UPDATE", "DELETE")
        if not postgres.execute(
            "SELECT has_table_privilege(%s, %s, %s) AS ok", (role, table, privilege)
        ).fetchone()["ok"]
    ]
    assert missing == []


@pytest.mark.parametrize("role", APP_ROLES)
def test_no_service_touches_the_migration_record(postgres: Any, role: str) -> None:
    assert not may(postgres, role, "SELECT * FROM schema_migrations")


def test_the_access_log_store_appends_one_row_per_event(
    postgres: Any, postgres_url: str
) -> None:
    log = PostgresAccessLog(postgres_url)
    event = record_access(case_id="c1", principal="p1", action="case.read")
    log.record(event)
    log.record(record_access(case_id="c1", principal="p1", action="case.read"))
    rows = postgres.execute(
        "SELECT subject_key, item FROM case_access_log ORDER BY recorded_at"
    ).fetchall()
    assert len(rows) == 2
    assert rows[0]["subject_key"] == "CASE#c1"
    assert rows[0]["item"] == {
        k: v for k, v in access_item(event).items() if k not in ("PK", "SK")
    }


def test_the_service_roles_may_log_in_and_the_migrator_role_exists(
    postgres: Any,
) -> None:
    """IAM database authentication logs each service in as its own role
    (infra/modules/database), so each must be LOGIN."""
    roles = {
        row["rolname"]: row["rolcanlogin"]
        for row in postgres.execute("SELECT rolname, rolcanlogin FROM pg_roles")
    }
    assert all(roles.get(role) for role in APP_ROLES)
    assert "migrator" in roles
