"""CaseStore over Postgres (ADR 0030): the `cases`, `case_assignments` and
`case_status_changes` tables, and the debtors a case is opened with."""

from __future__ import annotations

from collections.abc import Mapping, Sequence
from datetime import date, datetime
from typing import Any, Final

import psycopg
from psycopg import sql
from psycopg.rows import DictRow
from psycopg.types.json import Jsonb

from insolvia_core.access import Accessor, ClientAccessor, may_see_case
from insolvia_core.adapters.postgres.connection import connect
from insolvia_core.adapters.postgres.debtor_store import insert_debtor
from insolvia_core.adapters.postgres.firm_store import lock_linkable_client
from insolvia_core.adapters.postgres.queries import keyset_after, next_key, stamp
from insolvia_core.cases import (
    INDEX_BY_ASSIGNEE,
    INDEX_BY_FIRM,
    STATUS_MOVED,
    Case,
    CaseAssignment,
    CasePage,
    ClientCase,
    StatusChange,
    case_from_item,
    case_item,
    decode_cursor,
    encode_cursor,
    listing_cursor_tag,
)
from insolvia_core.clients import (
    PUBLIC_STATUS_ATTRIBUTES,
    CasePublicStatus,
    public_status_from_case_item,
)
from insolvia_core.debtors import Debtor
from insolvia_core.errors import ClientUnavailableError, ConflictError

# `case_item`'s attribute -> its column. The item stays the one owner of what
# a stored case says (`case_item` writes it, `case_from_item` parses it); this
# table only says where each attribute lives.
CASE_COLUMNS: Final[Mapping[str, str]] = {
    "id": "id",
    "firmId": "firm_id",
    "createdBy": "created_by",
    "chapter": "chapter",
    "district": "district",
    "status": "status",
    "createdAt": "created_at",
    "updatedAt": "updated_at",
    "court": "court",
    "division": "division",
    "formRevisions": "form_revisions",
    "constantsSetId": "constants_set_id",
    "exemptionSet": "exemption_set",
    "filedAt": "filed_at",
    "meeting341At": "meeting_341_at",
    "caseNumber": "case_number",
    "judge": "judge",
    "trustee": "trustee",
    "officeFileNumber": "office_file_number",
    "archivedAt": "archived_at",
    "archivedBy": "archived_by",
    "deletedAt": "deleted_at",
    "deletedBy": "deleted_by",
    "caseNumberKey": "case_number_key",
}

_LISTING: Final = ("created_at", "id")


def case_values(case: Case) -> dict[str, object]:
    """Every case column's value for `case` — absent attributes are NULL, so
    a whole-record write clears what the case no longer carries."""
    item = case_item(case)
    values = {column: item.get(attribute) for attribute, column in CASE_COLUMNS.items()}
    if values["form_revisions"] is not None:
        values["form_revisions"] = Jsonb(values["form_revisions"])
    return values


def _text(value: object) -> object:
    if isinstance(value, datetime):
        return stamp(value)
    if isinstance(value, date):
        return value.isoformat()
    return value


def case_from_row(row: Mapping[str, Any]) -> Case:
    return case_from_item(
        {
            attribute: _text(row[column])
            for attribute, column in CASE_COLUMNS.items()
            if row.get(column) is not None
        }
    )


def write_case(
    conn: psycopg.Connection[DictRow],
    case: Case,
    condition: sql.Composable,
    params: Sequence[object],
) -> int:
    """UPDATE the case's whole row WHERE `condition`; the rows it touched."""
    values = case_values(case)
    del values["id"]
    statement = sql.SQL("UPDATE cases SET {} WHERE id = %s AND {}").format(
        sql.SQL(", ").join(
            sql.SQL("{} = %s").format(sql.Identifier(column)) for column in values
        ),
        condition,
    )
    return conn.execute(statement, [*values.values(), case.id, *params]).rowcount


class PostgresCaseStore:
    """CaseStore backed by Postgres.

    `create` holds each linked client's row (`lock_linkable_client`, the
    condition `DebtorStore.link` writes under too) in the case's own
    transaction, so a merge cannot claim it in between.

    shortcut: one connection per call; a pool arrives with hosting (#625).
    """

    def __init__(self, database_url: str) -> None:
        self.database_url = database_url

    def create(
        self,
        case: Case,
        assignment: CaseAssignment,
        debtors: Sequence[Debtor] = (),
    ) -> None:
        for debtor in debtors:
            if debtor.case_id != case.id:
                raise RuntimeError("a debtor written with a case must belong to it")
        values = case_values(case)
        # ONE TRANSACTION: the case, its creator's assignment and its debtors
        # land together or not at all (the port says why).
        with connect(self.database_url) as conn, conn.transaction():
            try:
                conn.execute(
                    sql.SQL("INSERT INTO cases ({}) VALUES ({})").format(
                        sql.SQL(", ").join(map(sql.Identifier, values)),
                        sql.SQL(", ").join([sql.Placeholder()] * len(values)),
                    ),
                    list(values.values()),
                )
            except psycopg.errors.UniqueViolation as error:
                raise RuntimeError(f"case {case.id} already exists") from error
            _insert_assignment(conn, assignment)
            for debtor in debtors:
                if debtor.client_id is not None and not lock_linkable_client(
                    conn, case.firm_id, debtor.client_id
                ):
                    raise ClientUnavailableError(debtor.client_id)
                if not insert_debtor(conn, debtor):
                    raise RuntimeError(f"debtor {debtor.filing_role} already exists")

    def list_for_client(
        self, client_id: str, *, accessor: Accessor
    ) -> tuple[ClientCase, ...]:
        with connect(self.database_url) as conn:
            rows = conn.execute(
                "SELECT c.*, d.filing_role, EXISTS ("
                "  SELECT 1 FROM case_assignments a"
                "  WHERE a.case_id = c.id AND a.subject = %s) AS assigned"
                " FROM debtors d JOIN cases c ON c.id = d.case_id"
                " WHERE d.client_id = %s AND d.case_created_at IS NOT NULL"
                " ORDER BY d.case_created_at DESC, d.case_id DESC",
                (accessor.subject, client_id),
            ).fetchall()
        found: list[ClientCase] = []
        for row in rows:
            case = case_from_row(row)
            if may_see_case(accessor, case, assigned=row["assigned"]):
                found.append(ClientCase(case=case, filing_role=row["filing_role"]))
        return tuple(found)

    def get(self, case_id: str, *, accessor: Accessor) -> Case | None:
        # The case and "am I linked to it" in one query: the access rule's
        # two halves, decided by `may_see_case` here rather than in the route.
        with connect(self.database_url) as conn:
            row = conn.execute(
                "SELECT c.*, EXISTS ("
                "  SELECT 1 FROM case_assignments a"
                "  WHERE a.case_id = c.id AND a.subject = %s) AS assigned"
                " FROM cases c WHERE c.id = %s",
                (accessor.subject, case_id),
            ).fetchone()
        if row is None:
            return None
        case = case_from_row(row)
        return case if may_see_case(accessor, case, assigned=row["assigned"]) else None

    def read_for_worker(self, case_id: str) -> Case | None:
        # No access rule: the pipeline worker's authority is the accepted job
        # (the port), and only worker entrypoints compose this path.
        with connect(self.database_url) as conn:
            row = conn.execute(
                "SELECT * FROM cases WHERE id = %s", (case_id,)
            ).fetchone()
        return None if row is None else case_from_row(row)

    def public_status(self, client: ClientAccessor) -> CasePublicStatus | None:
        # Exactly the public attributes' columns: the rest of the row never
        # leaves the database on a portal request.
        wanted = [CASE_COLUMNS[attribute] for attribute in PUBLIC_STATUS_ATTRIBUTES]
        with connect(self.database_url) as conn:
            row = conn.execute(
                sql.SQL("SELECT {} FROM cases WHERE id = %s").format(
                    sql.SQL(", ").join(map(sql.Identifier, wanted))
                ),
                (client.case_id,),
            ).fetchone()
        if row is None:
            return None
        item = {
            attribute: _text(row[CASE_COLUMNS[attribute]])
            for attribute in PUBLIC_STATUS_ATTRIBUTES
            if row[CASE_COLUMNS[attribute]] is not None
        }
        return public_status_from_case_item(item, firm_id=client.firm_id)

    def list_for_accessor(
        self,
        accessor: Accessor,
        *,
        limit: int,
        cursor: str | None,
        archived: bool = False,
    ) -> CasePage:
        # Which listing depends on the caller, and the cursor carries it (and
        # the view), so a cursor minted against the other is refused.
        if accessor.sees_every_case:
            index = INDEX_BY_FIRM
            source = sql.SQL("cases c")
            params: list[object] = []
        else:
            index = INDEX_BY_ASSIGNEE
            source = sql.SQL(
                "cases c JOIN case_assignments a ON a.case_id = c.id AND a.subject = %s"
            )
            params = [accessor.subject]
        tag = listing_cursor_tag(index, archived=archived)
        after = decode_cursor(cursor, index=tag) if cursor is not None else None
        resume, resume_params = keyset_after(_LISTING, after, descending=True)
        with connect(self.database_url) as conn:
            rows = conn.execute(
                sql.SQL(
                    "SELECT c.* FROM {} WHERE c.firm_id = %s"
                    " AND c.deleted_at IS NULL AND (c.archived_at IS NOT NULL) = %s"
                    " AND {} ORDER BY c.created_at DESC, c.id DESC LIMIT %s"
                ).format(source, resume),
                [*params, accessor.firm_id, archived, *resume_params, limit + 1],
            ).fetchall()
        key = next_key(rows, limit, _LISTING)
        return CasePage(
            cases=tuple(case_from_row(row) for row in rows[:limit]),
            next_cursor=None if key is None else encode_cursor(key, index=tag),
        )

    def update(
        self,
        case: Case,
        *,
        expected_status: str | None = None,
        status_change: StatusChange | None = None,
    ) -> Case | None:
        # The firm check closes the window between the route's read and this
        # write; the status check (#355) stops a whole-record write read
        # before a status move from putting the old status back.
        condition = sql.SQL("firm_id = %s")
        params: list[object] = [case.firm_id]
        if expected_status is not None:
            condition = sql.SQL("firm_id = %s AND status = %s")
            params.append(expected_status)
        with connect(self.database_url) as conn, conn.transaction():
            if write_case(conn, case, condition, params) == 0:
                stored = conn.execute(
                    "SELECT firm_id, status FROM cases WHERE id = %s", (case.id,)
                ).fetchone()
                if stored is None or stored["firm_id"] != case.firm_id:
                    return None
                raise ConflictError(STATUS_MOVED)
            if status_change is not None:
                # Same transaction: the history cannot describe a move that
                # did not land.
                conn.execute(
                    "INSERT INTO case_status_changes (case_id, changed_at, firm_id,"
                    " changed_by, from_status, to_status, filing_id)"
                    " VALUES (%s, %s, %s, %s, %s, %s, %s)",
                    (
                        status_change.case_id,
                        status_change.changed_at,
                        case.firm_id,
                        status_change.changed_by,
                        status_change.from_status,
                        status_change.to_status,
                        status_change.filing_id,
                    ),
                )
        return case

    def status_history(self, case_id: str) -> tuple[StatusChange, ...]:
        with connect(self.database_url) as conn:
            rows = conn.execute(
                "SELECT * FROM case_status_changes WHERE case_id = %s"
                " ORDER BY changed_at",
                (case_id,),
            ).fetchall()
        return tuple(
            StatusChange(
                case_id=row["case_id"],
                changed_at=stamp(row["changed_at"]),
                changed_by=row["changed_by"],
                from_status=row["from_status"],
                to_status=row["to_status"],
                filing_id=row["filing_id"],
            )
            for row in rows
        )

    def assign(self, assignment: CaseAssignment) -> None:
        # Idempotent, as the port says: a replay rewrites who and when.
        with connect(self.database_url) as conn:
            _insert_assignment(conn, assignment)

    def unassign(self, case_id: str, subject: str) -> bool:
        with connect(self.database_url) as conn:
            deleted = conn.execute(
                "DELETE FROM case_assignments WHERE case_id = %s AND subject = %s",
                (case_id, subject),
            ).rowcount
        return deleted > 0

    def assignees(self, case_id: str) -> tuple[CaseAssignment, ...]:
        with connect(self.database_url) as conn:
            rows = conn.execute(
                "SELECT * FROM case_assignments WHERE case_id = %s"
                " ORDER BY assigned_at, subject",
                (case_id,),
            ).fetchall()
        return tuple(
            CaseAssignment(
                case_id=row["case_id"],
                subject=row["subject"],
                case_created_at=stamp(row["case_created_at"]),
                assigned_at=stamp(row["assigned_at"]),
                assigned_by=row["assigned_by"],
            )
            for row in rows
        )


def _insert_assignment(
    conn: psycopg.Connection[DictRow], assignment: CaseAssignment
) -> None:
    inserted = conn.execute(
        "INSERT INTO case_assignments (case_id, subject, firm_id, case_created_at,"
        " assigned_at, assigned_by)"
        " SELECT id, %s, firm_id, %s, %s, %s FROM cases WHERE id = %s"
        " ON CONFLICT (case_id, subject) DO UPDATE SET"
        " case_created_at = EXCLUDED.case_created_at,"
        " assigned_at = EXCLUDED.assigned_at, assigned_by = EXCLUDED.assigned_by",
        (
            assignment.subject,
            assignment.case_created_at,
            assignment.assigned_at,
            assignment.assigned_by,
            assignment.case_id,
        ),
    ).rowcount
    if inserted == 0:
        raise RuntimeError(f"no case {assignment.case_id} to assign to")
