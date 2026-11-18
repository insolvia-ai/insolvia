"""TaxIdStore over Postgres (ADR 0030): the `tax_ids` table, the envelope
as `bytea` (`sealed.py`). Keyed by the case the identifier is stored under;
the ref is its identity (insolvia_core.tax_ids)."""

from __future__ import annotations

from insolvia_core.adapters.postgres.connection import connect
from insolvia_core.adapters.postgres.sealed import envelope_from_row, envelope_values
from insolvia_core.tax_ids import SealedTaxId


class PostgresTaxIdStore:
    def __init__(self, database_url: str) -> None:
        self.database_url = database_url

    def put(self, case_id: str, sealed: SealedTaxId) -> None:
        # A write under an existing ref REPLACES it (a corrected identifier):
        # the port's contract, so there is no race worth guarding.
        with connect(self.database_url) as conn:
            conn.execute(
                "INSERT INTO tax_ids (case_id, ref, firm_id, kind, last_four,"
                " scheme, ciphertext, nonce, wrapped_key, created_at, updated_at)"
                " VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s)"
                " ON CONFLICT (case_id, ref) DO UPDATE SET"
                " firm_id = EXCLUDED.firm_id, kind = EXCLUDED.kind,"
                " last_four = EXCLUDED.last_four, scheme = EXCLUDED.scheme,"
                " ciphertext = EXCLUDED.ciphertext, nonce = EXCLUDED.nonce,"
                " wrapped_key = EXCLUDED.wrapped_key,"
                " created_at = EXCLUDED.created_at, updated_at = EXCLUDED.updated_at",
                (
                    case_id,
                    sealed.ref,
                    sealed.firm_id,
                    sealed.kind,
                    sealed.last_four,
                    *envelope_values(sealed.envelope),
                    sealed.created_at,
                    sealed.updated_at,
                ),
            )

    def get(self, case_id: str, ref: str) -> SealedTaxId | None:
        with connect(self.database_url) as conn:
            row = conn.execute(
                "SELECT * FROM tax_ids WHERE case_id = %s AND ref = %s",
                (case_id, ref),
            ).fetchone()
        if row is None:
            return None
        return SealedTaxId(
            ref=row["ref"],
            firm_id=row["firm_id"],
            kind=row["kind"],
            last_four=row["last_four"],
            envelope=envelope_from_row(row),
            created_at=row["created_at"],
            updated_at=row["updated_at"],
        )
