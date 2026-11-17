"""ClientBindingStore over Postgres (ADR 0030, #621).

`client_bindings` is the authoritative row (one per subject per firm, its
case a column) and `client_role_claims` says who answers for a role on a
case. A bind or a revoke is one transaction over both; the DynamoDB
case-partition mirror has no counterpart, because a case's bindings are one
indexed query here.
"""

from __future__ import annotations

from typing import Any

from insolvia_core.adapters.postgres.connection import connect
from insolvia_core.adapters.postgres.firm_store import stored
from insolvia_core.adapters.postgres.queries import require_rows
from insolvia_core.clients import (
    ACTIVE,
    INVITED,
    REVOKED,
    ClientBinding,
    binding_from_item,
    binding_item,
)
from insolvia_core.errors import ConflictError


def _refused() -> ConflictError:
    # No subject, email or role in the message: the caller learns the state
    # refused the write, which is all it can act on.
    return ConflictError(
        "another invitation on this case changed while this one was "
        "written; reload and try again"
    )


class PostgresClientBindingStore:
    """ClientBindingStore backed by Postgres (one connection per call, as
    `PostgresFirmStore`)."""

    def __init__(self, database_url: str) -> None:
        self.database_url = database_url

    # ── writes ──────────────────────────────────────────────────────

    def bind(
        self, binding: ClientBinding, *, narrowed: tuple[ClientBinding, ...]
    ) -> None:
        holders = [binding.subject, *(other.subject for other in narrowed)]
        with connect(self.database_url) as conn, conn.transaction():
            # One live binding per subject per firm: a revoked row may be
            # re-bound (a refiled case), a live one may not.
            require_rows(
                conn.execute(
                    "INSERT INTO client_bindings (firm_id, subject, case_id, item)"
                    " VALUES (%s, %s, %s, %s)"
                    " ON CONFLICT (firm_id, subject) DO UPDATE"
                    " SET case_id = EXCLUDED.case_id, item = EXCLUDED.item"
                    " WHERE client_bindings.item ->> 'status' = %s",
                    (
                        binding.firm_id,
                        binding.subject,
                        binding.case_id,
                        stored(binding_item(binding)),
                        REVOKED,
                    ),
                ),
                _refused(),
            )
            for other in narrowed:
                # Still live, or the narrowing is stale.
                require_rows(
                    conn.execute(
                        "UPDATE client_bindings SET case_id = %s, item = %s"
                        " WHERE firm_id = %s AND subject = %s"
                        " AND item ->> 'status' IN (%s, %s)",
                        (
                            other.case_id,
                            stored(binding_item(other)),
                            other.firm_id,
                            other.subject,
                            INVITED,
                            ACTIVE,
                        ),
                    ),
                    _refused(),
                )
            # Each role may be taken only if nobody holds it, or its holder
            # is the new subject or one this transaction narrows.
            for role in binding.roles:
                require_rows(
                    conn.execute(
                        "INSERT INTO client_role_claims (case_id, role, subject)"
                        " VALUES (%s, %s, %s)"
                        " ON CONFLICT (case_id, role) DO UPDATE"
                        " SET subject = EXCLUDED.subject"
                        " WHERE client_role_claims.subject = ANY(%s)",
                        (binding.case_id, role, binding.subject, holders),
                    ),
                    _refused(),
                )

    def update(self, binding: ClientBinding) -> ClientBinding | None:
        with connect(self.database_url) as conn, conn.transaction():
            written = conn.execute(
                "UPDATE client_bindings SET case_id = %s, item = %s"
                " WHERE firm_id = %s AND subject = %s",
                (
                    binding.case_id,
                    stored(binding_item(binding)),
                    binding.firm_id,
                    binding.subject,
                ),
            ).rowcount
            if not written:
                return None
            if binding.status == REVOKED:
                # Release only what this subject holds.
                conn.execute(
                    "DELETE FROM client_role_claims"
                    " WHERE case_id = %s AND role = ANY(%s) AND subject = %s",
                    (binding.case_id, list(binding.roles), binding.subject),
                )
        return binding

    # ── reads ───────────────────────────────────────────────────────

    def _items(self, query: str, params: tuple[Any, ...]) -> list[ClientBinding]:
        with connect(self.database_url) as conn:
            return [
                binding_from_item(row["item"]) for row in conn.execute(query, params)
            ]

    def get(self, firm_id: str, subject: str) -> ClientBinding | None:
        found = self._items(
            "SELECT item FROM client_bindings WHERE firm_id = %s AND subject = %s",
            (firm_id, subject),
        )
        return found[0] if found else None

    def find(self, subject: str) -> ClientBinding | None:
        # LIMIT 2 for FirmStore.find_user's reason.
        found = self._items(
            "SELECT item FROM client_bindings WHERE subject = %s LIMIT 2", (subject,)
        )
        if len(found) > 1:
            raise RuntimeError(
                "a client resolves to more than one firm; refusing to guess"
            )
        return found[0] if found else None

    def find_by_email(self, firm_id: str, email: str) -> ClientBinding | None:
        found = self._items(
            "SELECT item FROM client_bindings WHERE firm_id = %s"
            " AND item ->> 'email' = %s ORDER BY subject LIMIT 1",
            (firm_id, email.lower()),
        )
        return found[0] if found else None

    def list_for_case(self, case_id: str) -> tuple[ClientBinding, ...]:
        found = self._items(
            "SELECT item FROM client_bindings WHERE case_id = %s", (case_id,)
        )
        return tuple(sorted(found, key=lambda b: (b.created_at, b.subject)))
