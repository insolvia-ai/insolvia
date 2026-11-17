"""DebtorStore over Postgres (ADR 0030): the `debtors` table, one row per
filing role per case, its `item` the domain's `debtor_item` document."""

from __future__ import annotations

from typing import Any

import psycopg
from psycopg.rows import DictRow

from insolvia_core.adapters.postgres.connection import connect
from insolvia_core.adapters.postgres.firm_store import lock_linkable_client
from insolvia_core.adapters.postgres.queries import document
from insolvia_core.debtors import (
    Debtor,
    LinkOutcome,
    RepointOutcome,
    debtor_from_item,
    debtor_item,
    link_client,
    role_order,
)

_UPSERT = (
    " ON CONFLICT (case_id, filing_role) DO UPDATE SET"
    " client_id = EXCLUDED.client_id,"
    " case_created_at = EXCLUDED.case_created_at, item = EXCLUDED.item"
)


def insert_debtor(
    conn: psycopg.Connection[DictRow], debtor: Debtor, *, replace: bool = False
) -> bool:
    """Write `debtor` under its case's firm. Without `replace`, an existing
    role is left alone and this answers False. A missing case raises."""
    item = debtor_item(debtor)
    # The by-client columns, set together or not at all, as the item's are.
    client_id = item.get("clientId")
    row = conn.execute(
        "INSERT INTO debtors (case_id, filing_role, firm_id, client_id,"
        " case_created_at, item)"
        " SELECT id, %s, firm_id, %s, %s, %s FROM cases WHERE id = %s"
        + (_UPSERT if replace else " ON CONFLICT (case_id, filing_role) DO NOTHING")
        + " RETURNING 1",
        (
            debtor.filing_role,
            client_id,
            item.get("caseCreatedAt") if client_id is not None else None,
            document(item),
            debtor.case_id,
        ),
    ).fetchone()
    if row is not None:
        return True
    case = conn.execute("SELECT 1 FROM cases WHERE id = %s", (debtor.case_id,))
    if replace or case.fetchone() is None:
        raise RuntimeError(f"no case {debtor.case_id} for a debtor")
    return False


def _lock_case(conn: psycopg.Connection[DictRow], case_id: str) -> None:
    """Serialise every link and repoint of one case's debtors: each checks
    the case's other roles and then writes, and a role that does not exist
    yet has no row to lock."""
    conn.execute("SELECT 1 FROM cases WHERE id = %s FOR UPDATE", (case_id,))


def _other_role_names(
    conn: psycopg.Connection[DictRow], case_id: str, filing_role: str, client_id: str
) -> bool:
    return (
        conn.execute(
            "SELECT 1 FROM debtors WHERE case_id = %s AND filing_role <> %s"
            " AND client_id = %s",
            (case_id, filing_role, client_id),
        ).fetchone()
        is not None
    )


class PostgresDebtorStore:
    """DebtorStore backed by Postgres. `link` conditions on the client's
    row in the same database, held for the transaction
    (`lock_linkable_client`)."""

    def __init__(self, database_url: str) -> None:
        self.database_url = database_url

    def create(self, debtor: Debtor) -> bool:
        with connect(self.database_url) as conn:
            return insert_debtor(conn, debtor)

    def put(self, debtor: Debtor) -> None:
        with connect(self.database_url) as conn:
            insert_debtor(conn, debtor, replace=True)

    def link(self, debtor: Debtor, *, create: bool, firm_id: str) -> LinkOutcome:
        if debtor.client_id is None:
            raise ValueError("link needs a debtor that names a client")
        with connect(self.database_url) as conn, conn.transaction():
            _lock_case(conn, debtor.case_id)
            if create and self._row(conn, debtor.case_id, debtor.filing_role):
                return "role_taken"
            if _other_role_names(
                conn, debtor.case_id, debtor.filing_role, debtor.client_id
            ):
                return "client_taken"
            if not lock_linkable_client(conn, firm_id, debtor.client_id):
                return "client_unavailable"
            insert_debtor(conn, debtor, replace=True)
        return "written"

    def roles_for_client(self, client_id: str) -> tuple[tuple[str, str], ...]:
        with connect(self.database_url) as conn:
            rows = conn.execute(
                "SELECT case_id, filing_role FROM debtors WHERE client_id = %s",
                (client_id,),
            ).fetchall()
        return tuple(sorted((row["case_id"], row["filing_role"]) for row in rows))

    def repoint_client(
        self,
        case_id: str,
        filing_role: str,
        *,
        from_client_id: str,
        to_client_id: str,
    ) -> RepointOutcome:
        # Only the link moves: `link_client` keeps every copied field and the
        # provenance exactly as they are.
        with connect(self.database_url) as conn, conn.transaction():
            _lock_case(conn, case_id)
            item = self._row(conn, case_id, filing_role)
            stored = None if item is None else debtor_from_item(item)
            if stored is None or stored.client_id != from_client_id:
                return "absent"
            if _other_role_names(conn, case_id, filing_role, to_client_id):
                return "client_taken"
            if stored.case_created_at is None:
                raise RuntimeError("a debtor naming a client has no case_created_at")
            insert_debtor(
                conn,
                link_client(
                    stored,
                    client_id=to_client_id,
                    case_created_at=stored.case_created_at,
                ),
                replace=True,
            )
        return "written"

    def get(self, case_id: str, *, filing_role: str) -> Debtor | None:
        with connect(self.database_url) as conn:
            item = self._row(conn, case_id, filing_role)
        return None if item is None else debtor_from_item(item)

    def list_for_case(self, case_id: str) -> tuple[Debtor, ...]:
        with connect(self.database_url) as conn:
            rows = conn.execute(
                "SELECT item FROM debtors WHERE case_id = %s", (case_id,)
            ).fetchall()
        return tuple(
            sorted(
                (debtor_from_item(row["item"]) for row in rows),
                key=lambda d: (role_order(d.filing_role), d.filing_role),
            )
        )

    @staticmethod
    def _row(
        conn: psycopg.Connection[DictRow], case_id: str, filing_role: str
    ) -> dict[str, Any] | None:
        row = conn.execute(
            "SELECT item FROM debtors WHERE case_id = %s AND filing_role = %s",
            (case_id, filing_role),
        ).fetchone()
        return None if row is None else dict(row["item"])
