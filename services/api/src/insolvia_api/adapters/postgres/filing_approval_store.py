"""FilingApprovalStore over Postgres (ADR 0030): `filing_approvals`, its
`item` the domain's `approval_item`, and `filing_approval_pointers`, the
case's current approval. Composed by the API (create / current / void) and
the filing worker (get / consume / void).

Every write is conditional and the conditions are core/ports' contract:
one transaction for create (new row, pointer move, supersede), and one
`UPDATE … WHERE` each for consume and void."""

from __future__ import annotations

from typing import Final

import psycopg
from insolvia_core.adapters.postgres.connection import connect
from insolvia_core.adapters.postgres.queries import document
from insolvia_core.errors import ConflictError
from psycopg.types.json import Jsonb

from insolvia_api.core.filing_approval import (
    FilingApproval,
    approval_from_item,
    approval_item,
)

_LOST: Final = "Another approval was made or used first — review the filing set again."

# Merged into a pending row's item: a status move and its stamps, nothing else.
_SET_WHERE_PENDING: Final = (
    "UPDATE filing_approvals SET item = item || %s"
    " WHERE case_id = %s AND approval_id = %s AND item ->> 'status' = 'pending'"
)


class PostgresFilingApprovalStore:
    def __init__(self, database_url: str) -> None:
        self.database_url = database_url

    def current(self, case_id: str) -> FilingApproval | None:
        with connect(self.database_url) as conn:
            row = conn.execute(
                "SELECT a.item FROM filing_approval_pointers p"
                " JOIN filing_approvals a USING (case_id, approval_id)"
                " WHERE p.case_id = %s",
                (case_id,),
            ).fetchone()
        return None if row is None else approval_from_item(row["item"])

    def get(self, case_id: str, approval_id: str) -> FilingApproval | None:
        with connect(self.database_url) as conn:
            row = conn.execute(
                "SELECT item FROM filing_approvals"
                " WHERE case_id = %s AND approval_id = %s",
                (case_id, approval_id),
            ).fetchone()
        return None if row is None else approval_from_item(row["item"])

    def create(
        self,
        approval: FilingApproval,
        *,
        replacing: FilingApproval | None,
        voided_at: str,
    ) -> None:
        with connect(self.database_url) as conn, conn.transaction():
            try:
                conn.execute(
                    "INSERT INTO filing_approvals (case_id, approval_id, firm_id, item)"
                    " VALUES (%s, %s, %s, %s)",
                    (
                        approval.case_id,
                        approval.approval_id,
                        approval.firm_id,
                        document(approval_item(approval)),
                    ),
                )
            except psycopg.errors.UniqueViolation as error:
                # Ids are server-minted uuid4s; an existing one means the
                # minting broke.
                raise RuntimeError("approval id already exists in this case") from error
            if replacing is None:
                moved = conn.execute(
                    "INSERT INTO filing_approval_pointers (case_id, approval_id)"
                    " VALUES (%s, %s) ON CONFLICT DO NOTHING",
                    (approval.case_id, approval.approval_id),
                ).rowcount
            else:
                moved = conn.execute(
                    "UPDATE filing_approval_pointers SET approval_id = %s"
                    " WHERE case_id = %s AND approval_id = %s",
                    (approval.approval_id, approval.case_id, replacing.approval_id),
                ).rowcount
            if moved == 0:
                raise ConflictError(_LOST)
            if replacing is not None and replacing.status == "pending":
                voided = conn.execute(
                    _SET_WHERE_PENDING,
                    (
                        Jsonb(
                            {
                                "status": "voided",
                                "voidedAt": voided_at,
                                "voidReason": "superseded",
                            }
                        ),
                        approval.case_id,
                        replacing.approval_id,
                    ),
                ).rowcount
                if voided == 0:
                    raise ConflictError(_LOST)

    def consume(
        self,
        case_id: str,
        approval_id: str,
        *,
        digest: str,
        now: int,
        consumed_at: str,
    ) -> bool:
        # THE SINGLE USE: still pending, still the digest approved, not yet
        # expired. Of two callers exactly one passes.
        with connect(self.database_url) as conn:
            consumed = conn.execute(
                _SET_WHERE_PENDING
                + " AND item ->> 'digest' = %s AND (item ->> 'expiresAt')::bigint > %s",
                (
                    Jsonb({"status": "consumed", "consumedAt": consumed_at}),
                    case_id,
                    approval_id,
                    digest,
                    now,
                ),
            ).rowcount
        return consumed > 0

    def void(
        self, case_id: str, approval_id: str, *, reason: str, voided_at: str
    ) -> bool:
        with connect(self.database_url) as conn:
            voided = conn.execute(
                _SET_WHERE_PENDING,
                (
                    Jsonb(
                        {
                            "status": "voided",
                            "voidedAt": voided_at,
                            "voidReason": reason,
                        }
                    ),
                    case_id,
                    approval_id,
                ),
            ).rowcount
        return voided > 0
