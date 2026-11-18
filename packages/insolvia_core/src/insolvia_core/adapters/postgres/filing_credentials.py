"""The credential vault and the signed authorizations over Postgres (ADR
0030; insolvia_core.filing_credentials owns the design).

ISOLATION IS TWO LAYERS. KMS: only the filing worker's role can unwrap a
data key (adapters/aws/filing_credentials.KmsCredentialOpener). The grants
(migration 0003): the api role may INSERT and DELETE a credential and read
its status columns, never the envelope; the filing role alone reads the
whole row. So the store is composed in one of two ways — `opens=True` in the
filing worker only, which selects the envelope; everywhere else the envelope
is never selected and the record carries `WITHHELD` in its place.
"""

from __future__ import annotations

from collections.abc import Mapping
from dataclasses import replace
from typing import Any, Final

import psycopg
from psycopg.rows import DictRow
from psycopg.types.json import Jsonb

from insolvia_core.adapters.postgres.connection import connect
from insolvia_core.adapters.postgres.queries import document
from insolvia_core.adapters.postgres.sealed import (
    ENVELOPE_COLUMNS,
    envelope_from_row,
    envelope_values,
)
from insolvia_core.errors import ConflictError
from insolvia_core.filing_authorization import (
    CURRENT_SORT_KEY,
    FilingAuthorization,
    authorization_from_item,
    authorization_item,
    history_sort_key,
)
from insolvia_core.filing_credentials import FilingCredential
from insolvia_core.tax_ids import Envelope

# What a credential read without the envelope carries instead. Its scheme is
# no cipher's, so `open_with_data_key` refuses it before any key is touched.
WITHHELD: Final = Envelope(scheme="withheld", ciphertext="", nonce="", wrapped_key="")

_STATUS_COLUMNS: Final = (
    "firm_id",
    "attorney_id",
    "credential_id",
    "login",
    "courts",
    "status",
    "authorization_ref",
    "created_at",
    "updated_at",
)


class PostgresFilingCredentialStore:
    """FilingCredentialStore over `filing_credentials`, keyed by the
    attorney: a credential id that belongs to somebody else is not found."""

    def __init__(self, database_url: str, *, opens: bool = False) -> None:
        self.database_url = database_url
        columns = _STATUS_COLUMNS + (ENVELOPE_COLUMNS if opens else ())
        self._select = f"SELECT {', '.join(columns)} FROM filing_credentials"

    def create(self, credential: FilingCredential) -> None:
        # Never a replace: rotation is revoke-then-enrol.
        try:
            with connect(self.database_url) as conn:
                conn.execute(
                    "INSERT INTO filing_credentials (firm_id, attorney_id,"
                    " credential_id, login, courts, status, authorization_ref,"
                    " created_at, updated_at, scheme, ciphertext, nonce, wrapped_key)"
                    " VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s)",
                    (
                        credential.firm_id,
                        credential.attorney_id,
                        credential.credential_id,
                        credential.login,
                        Jsonb(list(credential.courts)),
                        credential.status,
                        credential.authorization_ref,
                        credential.created_at,
                        credential.updated_at,
                        *envelope_values(credential.envelope),
                    ),
                )
        except psycopg.errors.UniqueViolation as error:
            raise RuntimeError("credential id already exists") from error

    def get(
        self, firm_id: str, attorney_id: str, credential_id: str
    ) -> FilingCredential | None:
        with connect(self.database_url) as conn:
            row = conn.execute(
                self._select
                + " WHERE firm_id = %s AND attorney_id = %s AND credential_id = %s",
                (firm_id, attorney_id, credential_id),
            ).fetchone()
        return None if row is None else _credential(row)

    def list_for_attorney(
        self, firm_id: str, attorney_id: str
    ) -> tuple[FilingCredential, ...]:
        with connect(self.database_url) as conn:
            rows = conn.execute(
                self._select + " WHERE firm_id = %s AND attorney_id = %s"
                " ORDER BY created_at, credential_id",
                (firm_id, attorney_id),
            ).fetchall()
        return tuple(_credential(row) for row in rows)

    def delete(self, firm_id: str, attorney_id: str, credential_id: str) -> bool:
        # The row is DESTROYED, envelope and all (ADR 0024): revocation is not
        # a flag the worker might fail to read.
        with connect(self.database_url) as conn:
            deleted = conn.execute(
                "DELETE FROM filing_credentials"
                " WHERE firm_id = %s AND attorney_id = %s AND credential_id = %s",
                (firm_id, attorney_id, credential_id),
            ).rowcount
        return deleted > 0


def _credential(row: Mapping[str, Any]) -> FilingCredential:
    return FilingCredential(
        credential_id=row["credential_id"],
        firm_id=row["firm_id"],
        attorney_id=row["attorney_id"],
        login=row["login"],
        courts=tuple(str(court) for court in row["courts"]),
        status=row["status"],
        envelope=envelope_from_row(row) if "ciphertext" in row else WITHHELD,
        created_at=row["created_at"],
        updated_at=row["updated_at"],
        authorization_ref=row["authorization_ref"],
    )


class PostgresFilingAuthorizationStore:
    """FilingAuthorizationStore: `filing_authorization_current` (the one the
    worker reads) and `filing_authorizations` (one record per signature).

    One transaction per write, so the current row and the history never
    disagree — stronger than the DynamoDB adapter's write ordering."""

    def __init__(self, database_url: str) -> None:
        self.database_url = database_url

    def get_current(self, firm_id: str, attorney_id: str) -> FilingAuthorization | None:
        with connect(self.database_url) as conn:
            row = conn.execute(
                "SELECT item FROM filing_authorization_current"
                " WHERE firm_id = %s AND attorney_id = %s",
                (firm_id, attorney_id),
            ).fetchone()
        return None if row is None else authorization_from_item(row["item"])

    def put_current(
        self,
        authorization: FilingAuthorization,
        *,
        replacing: FilingAuthorization | None,
    ) -> None:
        current = document(authorization_item(authorization, sort_key=CURRENT_SORT_KEY))
        key = (authorization.firm_id, authorization.attorney_id)
        with connect(self.database_url) as conn, conn.transaction():
            if replacing is None:
                written = conn.execute(
                    "INSERT INTO filing_authorization_current"
                    " (firm_id, attorney_id, authorization_id, item)"
                    " VALUES (%s, %s, %s, %s) ON CONFLICT DO NOTHING",
                    (*key, authorization.authorization_id, current),
                ).rowcount
            else:
                written = conn.execute(
                    "UPDATE filing_authorization_current"
                    " SET authorization_id = %s, item = %s"
                    " WHERE firm_id = %s AND attorney_id = %s"
                    " AND authorization_id = %s",
                    (
                        authorization.authorization_id,
                        current,
                        *key,
                        replacing.authorization_id,
                    ),
                ).rowcount
            if written == 0:
                raise ConflictError("the authorization changed")
            _put_history(conn, authorization, new=True)
            if replacing is not None:
                _put_history(conn, replace(replacing, status="superseded"))

    def withdraw(
        self, authorization: FilingAuthorization, *, withdrawn_at: str
    ) -> bool:
        with connect(self.database_url) as conn, conn.transaction():
            deleted = conn.execute(
                "DELETE FROM filing_authorization_current"
                " WHERE firm_id = %s AND attorney_id = %s AND authorization_id = %s",
                (
                    authorization.firm_id,
                    authorization.attorney_id,
                    authorization.authorization_id,
                ),
            ).rowcount
            if deleted == 0:
                return False
            _put_history(
                conn,
                replace(authorization, status="withdrawn", withdrawn_at=withdrawn_at),
            )
        return True


def _put_history(
    conn: psycopg.Connection[DictRow],
    authorization: FilingAuthorization,
    *,
    new: bool = False,
) -> None:
    """Write the signature's history record. `new` refuses an existing one
    (ids are server-minted); otherwise the record is updated in place."""
    item = document(
        authorization_item(
            authorization, sort_key=history_sort_key(authorization.authorization_id)
        )
    )
    conflict = "NOTHING" if new else "UPDATE SET item = EXCLUDED.item"
    written = conn.execute(
        "INSERT INTO filing_authorizations"
        " (firm_id, attorney_id, authorization_id, item) VALUES (%s, %s, %s, %s)"
        f" ON CONFLICT (firm_id, attorney_id, authorization_id) DO {conflict}",
        (
            authorization.firm_id,
            authorization.attorney_id,
            authorization.authorization_id,
            item,
        ),
    ).rowcount
    if written == 0:
        raise RuntimeError("authorization id already exists")
