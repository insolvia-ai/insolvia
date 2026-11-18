"""`CaseTableRows` over Postgres (ADR 0030) — ADR 0022's pre-client purge;
`insolvia_admin.core.pre_client_purge` owns the rule.

The port speaks in the case's rows as `PK`/`SK` items, the shape the rule
was written in. This adapter presents exactly the rows the rule reads:

    PK = CASE#<id>   SK = META             one per `cases` row
                     SK = DEBTOR#<role>    one per `debtors` row, + clientId
                     SK = CLIENT#<subject> one per `client_bindings` row
                                           naming the case (the binding's
                                           mirror, for `binding_rows`)

Deleting a DEBTOR# item deletes that debtor only while it names no client
(the item-level fence). Deleting the META item deletes the whole case — every
table keyed by `case_id`, then the root — in ONE transaction, re-checking the
fence under a row lock on the case, so a case is gone whole or not at all.
A CLIENT# item is a view of the binding row, which `delete_binding` owns.

NOT deleted: `case_access_log` (append-only; no foreign key, by design).
"""

from __future__ import annotations

from collections.abc import Iterator, Mapping
from typing import Final

from insolvia_core.adapters.postgres.connection import connect
from psycopg import sql

from insolvia_admin.core.pre_client_purge import (
    BINDING_MIRROR_PREFIX,
    CASE_PREFIX,
    DEBTOR_PREFIX,
    META,
    BindingRow,
)

#: Every table keyed by a case's id, in an order its foreign keys accept.
#: `test_purge_pre_client` fails when a migration adds one this misses.
CASE_TABLES: Final = (
    "filing_approval_pointers",
    "filing_approvals",
    "filings",
    "tax_ids",
    "case_assignments",
    "case_status_changes",
    "debtors",
    "case_entities",
    "candidates",
    "tasks",
    "document_requests",
    "documents",
    "jobs",
    "packets",
    "events",
    "client_role_claims",
)


def _case_id(item: Mapping[str, object]) -> str:
    return str(item["PK"])[len(CASE_PREFIX) :]


class PostgresCaseTableRows:
    def __init__(self, database_url: str) -> None:
        self.database_url = database_url

    def summaries(self) -> Iterator[Mapping[str, object]]:
        with connect(self.database_url) as conn:
            for row in conn.execute("SELECT id FROM cases"):
                yield {"PK": f"{CASE_PREFIX}{row['id']}", "SK": META}
            for row in conn.execute(
                "SELECT case_id, filing_role, client_id FROM debtors"
            ):
                yield _debtor(row)

    def partition(self, case_id: str) -> list[Mapping[str, object]]:
        pk = f"{CASE_PREFIX}{case_id}"
        with connect(self.database_url) as conn:
            items: list[Mapping[str, object]] = [
                {"PK": pk, "SK": META}
                for _ in conn.execute("SELECT 1 FROM cases WHERE id = %s", (case_id,))
            ]
            items += [
                _debtor(row)
                for row in conn.execute(
                    "SELECT case_id, filing_role, client_id FROM debtors"
                    " WHERE case_id = %s",
                    (case_id,),
                )
            ]
            items += [
                {
                    "PK": pk,
                    "SK": f"{BINDING_MIRROR_PREFIX}{row['subject']}",
                    "firmId": row["firm_id"],
                    "subject": row["subject"],
                    "caseId": row["case_id"],
                }
                for row in conn.execute(
                    "SELECT firm_id, subject, case_id FROM client_bindings"
                    " WHERE case_id = %s",
                    (case_id,),
                )
            ]
        return items

    def delete(self, item: Mapping[str, object]) -> bool:
        case_id, sort_key = _case_id(item), str(item["SK"])
        if sort_key.startswith(DEBTOR_PREFIX):
            with connect(self.database_url) as conn:
                conn.execute(
                    "DELETE FROM debtors WHERE case_id = %s AND filing_role = %s"
                    " AND client_id IS NULL",
                    (case_id, sort_key[len(DEBTOR_PREFIX) :]),
                )
                # Refused only if it is still there, naming a client.
                return (
                    conn.execute(
                        "SELECT 1 FROM debtors WHERE case_id = %s AND filing_role = %s",
                        (case_id, sort_key[len(DEBTOR_PREFIX) :]),
                    ).fetchone()
                    is None
                )
        if sort_key != META:
            return True  # a binding mirror: `delete_binding` deletes the row
        with connect(self.database_url) as conn, conn.transaction():
            conn.execute("SELECT 1 FROM cases WHERE id = %s FOR UPDATE", (case_id,))
            if conn.execute(
                "SELECT 1 FROM debtors WHERE case_id = %s AND client_id IS NOT NULL",
                (case_id,),
            ).fetchone():
                return False
            for table in CASE_TABLES:
                conn.execute(
                    sql.SQL("DELETE FROM {} WHERE case_id = %s").format(
                        sql.Identifier(table)
                    ),
                    (case_id,),
                )
            conn.execute("DELETE FROM cases WHERE id = %s", (case_id,))
        return True

    def delete_binding(self, row: BindingRow) -> bool:
        # Only while it still binds the purged case: a client since re-bound
        # to another matter keeps that binding.
        with connect(self.database_url) as conn:
            return (
                conn.execute(
                    "DELETE FROM client_bindings"
                    " WHERE firm_id = %s AND subject = %s AND case_id = %s",
                    (row.firm_id, row.subject, row.case_id),
                ).rowcount
                > 0
            )


def _debtor(row: Mapping[str, object]) -> dict[str, object]:
    item: dict[str, object] = {
        "PK": f"{CASE_PREFIX}{row['case_id']}",
        "SK": f"{DEBTOR_PREFIX}{row['filing_role']}",
    }
    if row["client_id"]:
        item["clientId"] = row["client_id"]
    return item
