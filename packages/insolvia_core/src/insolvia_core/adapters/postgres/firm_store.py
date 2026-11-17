"""FirmStore over Postgres (ADR 0030, #621): migration 0001's firm tables.

Each row's `item` is the domain's stored shape (`firm_item`, …) minus the
DynamoDB key attributes, read back through the same `*_from_item` the memory
twin's shapes come from. Each DynamoDB condition is an `UPDATE … WHERE`, and
each multi-item transaction is one database transaction.
"""

from __future__ import annotations

from collections.abc import Mapping
from typing import Any

import psycopg
from psycopg.rows import DictRow
from psycopg.types.json import Jsonb

from insolvia_core.adapters.postgres.connection import connect
from insolvia_core.adapters.postgres.queries import require_rows
from insolvia_core.document_requests import (
    DocumentChecklist,
    checklist_from_item,
    checklist_item,
)
from insolvia_core.fields import timestamp
from insolvia_core.firm_clients import (
    ACTIVE,
    ARCHIVED,
    MERGED_INTO,
    MERGING_FROM,
    MERGING_INTO,
    PROSPECT_STAGE,
    SERVER_OWNED_ATTRIBUTES,
    FirmClient,
    firm_client_from_item,
    firm_client_item,
    sorted_firm_clients,
)
from insolvia_core.firms import (
    Firm,
    FirmUser,
    firm_from_item,
    firm_item,
    firm_user_from_item,
    firm_user_item,
)
from insolvia_core.library_creditors import (
    LibraryCreditor,
    library_creditor_from_item,
    library_creditor_item,
)
from insolvia_core.questionnaire import (
    QuestionnaireConfig,
    questionnaire_from_item,
    questionnaire_item,
)

_KEYS = ("PK", "SK", "GSI1PK", "GSI1SK")


def stored(item: Mapping[str, Any]) -> Jsonb:
    """A domain item as the `item` column holds it: without the DynamoDB keys,
    which the table's own columns replace."""
    return Jsonb({key: value for key, value in item.items() if key not in _KEYS})


class _RefusedError(Exception):
    """A condition inside a transaction refused it: roll back, answer "no"."""


def lock_linkable_client(
    conn: psycopg.Connection[Any], firm_id: str, client_id: str
) -> bool:
    """Whether a debtor may be linked to this client, holding the row so a
    merge cannot claim it before the caller's transaction commits.

    Run it inside the transaction that writes the link: the Postgres case
    store's `create` and debtor store's `link` do.
    `FOR SHARE` lets two links proceed together and makes a merge's claim
    (an UPDATE of this row) wait for them, or them for it — whichever is
    second sees the other's outcome. The rule is the domain's own:
    `FirmClient.refusal_for_new_case`.
    """
    row = conn.execute(
        "SELECT item FROM firm_clients WHERE firm_id = %s AND id = %s FOR SHARE",
        (firm_id, client_id),
    ).fetchone()
    return row is not None and (
        firm_client_from_item(row["item"]).refusal_for_new_case() is None
    )


class PostgresFirmStore:
    """FirmStore backed by Postgres.

    shortcut: one connection per call; a pooled or per-invocation connection
    belongs with the hosting work (#625), when a Lambda's connect cost is real.
    """

    def __init__(self, database_url: str) -> None:
        self.database_url = database_url

    def _connect(self) -> psycopg.Connection[DictRow]:
        return connect(self.database_url)

    def _one(self, query: str, params: tuple[Any, ...]) -> dict[str, Any] | None:
        with self._connect() as conn:
            row = conn.execute(query, params).fetchone()
        return None if row is None else row["item"]

    def _all(self, query: str, params: tuple[Any, ...]) -> list[dict[str, Any]]:
        with self._connect() as conn:
            return [row["item"] for row in conn.execute(query, params)]

    def _write(self, query: str, params: tuple[Any, ...]) -> int:
        with self._connect() as conn:
            return conn.execute(query, params).rowcount

    def _insert(self, query: str, params: tuple[Any, ...], what: str) -> None:
        # ON CONFLICT DO NOTHING + zero rows is attribute_not_exists: a second
        # create raises rather than overwriting, as the memory twin does.
        with self._connect() as conn:
            require_rows(
                conn.execute(query, params), RuntimeError(f"{what} already exists")
            )

    # ── Firms ───────────────────────────────────────────────────────

    def create_firm(self, firm: Firm) -> None:
        self._insert(
            "INSERT INTO firms (id, item) VALUES (%s, %s) ON CONFLICT DO NOTHING",
            (firm.id, stored(firm_item(firm))),
            f"firm {firm.id}",
        )

    def get_firm(self, firm_id: str) -> Firm | None:
        item = self._one("SELECT item FROM firms WHERE id = %s", (firm_id,))
        return None if item is None else firm_from_item(item)

    def list_firms(self) -> tuple[Firm, ...]:
        firms = [
            firm_from_item(item) for item in self._all("SELECT item FROM firms", ())
        ]
        # Sorted in Python, not by ORDER BY: the database's collation would
        # order names differently from the memory twin.
        return tuple(sorted(firms, key=lambda firm: (firm.name, firm.id)))

    def update_firm(self, firm: Firm) -> Firm | None:
        written = self._write(
            "UPDATE firms SET item = %s WHERE id = %s",
            (stored(firm_item(firm)), firm.id),
        )
        return firm if written else None

    # ── Firm users ──────────────────────────────────────────────────

    def add_user(self, user: FirmUser) -> None:
        self._insert(
            "INSERT INTO firm_users (firm_id, subject, item) VALUES (%s, %s, %s)"
            " ON CONFLICT DO NOTHING",
            (user.firm_id, user.subject, stored(firm_user_item(user))),
            "firm user",
        )

    def get_user(self, firm_id: str, subject: str) -> FirmUser | None:
        item = self._one(
            "SELECT item FROM firm_users WHERE firm_id = %s AND subject = %s",
            (firm_id, subject),
        )
        return None if item is None else firm_user_from_item(item)

    def find_user(self, subject: str) -> FirmUser | None:
        # LIMIT 2: one row is the answer, two the invariant violation the
        # port requires a raise on.
        items = self._all(
            "SELECT item FROM firm_users WHERE subject = %s LIMIT 2", (subject,)
        )
        if len(items) > 1:
            # No subject in the message: a Cognito sub identifies a person.
            raise RuntimeError(
                "a firm user resolves to more than one firm; refusing to guess"
            )
        return firm_user_from_item(items[0]) if items else None

    def list_users(self, firm_id: str) -> tuple[FirmUser, ...]:
        users = [
            firm_user_from_item(item)
            for item in self._all(
                "SELECT item FROM firm_users WHERE firm_id = %s", (firm_id,)
            )
        ]
        return tuple(
            sorted(
                users, key=lambda user: (user.last_name, user.first_name, user.subject)
            )
        )

    def update_user(self, user: FirmUser) -> FirmUser | None:
        written = self._write(
            "UPDATE firm_users SET item = %s WHERE firm_id = %s AND subject = %s",
            (stored(firm_user_item(user)), user.firm_id, user.subject),
        )
        return user if written else None

    def remove_user(self, firm_id: str, subject: str) -> bool:
        return bool(
            self._write(
                "DELETE FROM firm_users WHERE firm_id = %s AND subject = %s",
                (firm_id, subject),
            )
        )

    # ── Library creditors ───────────────────────────────────────────

    def create_library_creditor(self, creditor: LibraryCreditor) -> None:
        self._insert(
            "INSERT INTO library_creditors (firm_id, id, item) VALUES (%s, %s, %s)"
            " ON CONFLICT DO NOTHING",
            (creditor.firm_id, creditor.id, stored(library_creditor_item(creditor))),
            f"library creditor {creditor.id}",
        )

    def get_library_creditor(
        self, firm_id: str, creditor_id: str
    ) -> LibraryCreditor | None:
        item = self._one(
            "SELECT item FROM library_creditors WHERE firm_id = %s AND id = %s",
            (firm_id, creditor_id),
        )
        return None if item is None else library_creditor_from_item(item)

    def list_library_creditors(self, firm_id: str) -> tuple[LibraryCreditor, ...]:
        creditors = [
            library_creditor_from_item(item)
            for item in self._all(
                "SELECT item FROM library_creditors WHERE firm_id = %s", (firm_id,)
            )
        ]
        return tuple(
            sorted(creditors, key=lambda creditor: (creditor.name, creditor.id))
        )

    def update_library_creditor(
        self, creditor: LibraryCreditor
    ) -> LibraryCreditor | None:
        written = self._write(
            "UPDATE library_creditors SET item = %s WHERE firm_id = %s AND id = %s",
            (stored(library_creditor_item(creditor)), creditor.firm_id, creditor.id),
        )
        return creditor if written else None

    def delete_library_creditor(self, firm_id: str, creditor_id: str) -> bool:
        return bool(
            self._write(
                "DELETE FROM library_creditors WHERE firm_id = %s AND id = %s",
                (firm_id, creditor_id),
            )
        )

    # ── Firm clients (ADR 0022) ─────────────────────────────────────

    def create_client(self, client: FirmClient) -> None:
        self._insert(
            "INSERT INTO firm_clients (firm_id, id, item) VALUES (%s, %s, %s)"
            " ON CONFLICT DO NOTHING",
            (client.firm_id, client.id, stored(firm_client_item(client))),
            f"client {client.id}",
        )

    def get_client(self, firm_id: str, client_id: str) -> FirmClient | None:
        item = self._one(
            "SELECT item FROM firm_clients WHERE firm_id = %s AND id = %s",
            (firm_id, client_id),
        )
        return None if item is None else firm_client_from_item(item)

    def list_clients(self, firm_id: str) -> tuple[FirmClient, ...]:
        return sorted_firm_clients(
            firm_client_from_item(item)
            for item in self._all(
                "SELECT item FROM firm_clients WHERE firm_id = %s", (firm_id,)
            )
        )

    def _client_update(
        self, set_item: str, condition: str, params: dict[str, Any]
    ) -> FirmClient | None:
        """One conditional UPDATE of a client row, answered with the row as
        written, or None when the row is missing or the condition refused."""
        item = self._one(
            f"UPDATE firm_clients SET item = {set_item}"
            f" WHERE firm_id = %(firm)s AND id = %(id)s AND {condition}"
            " RETURNING item",
            params,  # type: ignore[arg-type]
        )
        return None if item is None else firm_client_from_item(item)

    def update_client(self, client: FirmClient) -> FirmClient | None:
        # The whole record the caller owns, keeping what the row already
        # holds of the server-owned attributes (the merge claim, the funnel
        # stage), so a save built from a stale read cannot erase them. A
        # merged client is terminal (the port says why).
        owned = {
            key: value
            for key, value in firm_client_item(client).items()
            if key not in SERVER_OWNED_ATTRIBUTES
        }
        return self._client_update(
            "%(owned)s || COALESCE((SELECT jsonb_object_agg(key, value)"
            " FROM jsonb_each(item) WHERE key = ANY(%(kept)s)), '{}'::jsonb)",
            f"NOT item ? '{MERGED_INTO}'",
            {
                "firm": client.firm_id,
                "id": client.id,
                "owned": stored(owned),
                "kept": list(SERVER_OWNED_ATTRIBUTES),
            },
        )

    # ── The prospect funnel (issue 14.3 / #355) ─────────────────────

    def set_client_prospect_stage(
        self, firm_id: str, client_id: str, stage: str | None
    ) -> FirmClient | None:
        # Conditioned on the client still being a prospect, so it cannot
        # interleave with `mark_client_retained` into a retained client with
        # a funnel position.
        change: dict[str, Any] = {"updatedAt": timestamp()}
        if stage is not None:
            change[PROSPECT_STAGE] = stage
        return self._client_update(
            f"(item - '{PROSPECT_STAGE}') || %(change)s",
            f"NOT item ? '{MERGED_INTO}'"
            " AND item -> 'body' ->> 'first_retained_at' IS NULL",
            {"firm": firm_id, "id": client_id, "change": Jsonb(change)},
        )

    def mark_client_retained(
        self, firm_id: str, client_id: str, *, retained_on: str
    ) -> FirmClient | None:
        # Keeps a date already recorded, and leaves the funnel, in one write.
        return self._client_update(
            "jsonb_set(item - %(stage)s::text, '{body,first_retained_at}',"
            " CASE WHEN item -> 'body' ->> 'first_retained_at' IS NULL"
            " THEN to_jsonb(%(date)s::text)"
            " ELSE item -> 'body' -> 'first_retained_at' END)"
            " || jsonb_build_object('updatedAt', %(now)s::text)",
            f"NOT item ? '{MERGED_INTO}'",
            {
                "firm": firm_id,
                "id": client_id,
                "stage": PROSPECT_STAGE,
                "date": retained_on,
                "now": timestamp(),
            },
        )

    # ── Merging two clients (ADR 0022's PR 7) ───────────────────────
    #
    # Each step that touches both rows is ONE transaction: the claim is
    # taken, finished or released on both or on neither.

    def _both(self, *updates: tuple[str, tuple[Any, ...]]) -> bool:
        """Run conditional UPDATEs in one transaction; False (and nothing
        written) when any of them touched no row."""
        try:
            with self._connect() as conn, conn.transaction():
                for query, params in updates:
                    require_rows(conn.execute(query, params), _RefusedError())
        except _RefusedError:
            return False
        return True

    def claim_client_merge(
        self, firm_id: str, *, merged_id: str, survivor_id: str
    ) -> bool:
        if merged_id == survivor_id:
            return False
        active = (
            f"firm_id = %s AND id = %s AND item ->> 'status' = '{ACTIVE}'"
            f" AND NOT item ? '{MERGED_INTO}'"
        )
        return self._both(
            (
                "UPDATE firm_clients"
                " SET item = item || jsonb_build_object(%s::text, %s::text)"
                f" WHERE {active} AND NOT item ? '{MERGING_FROM}'"
                f" AND COALESCE(item ->> '{MERGING_INTO}', %s) = %s",
                (
                    MERGING_INTO,
                    survivor_id,
                    firm_id,
                    merged_id,
                    survivor_id,
                    survivor_id,
                ),
            ),
            (
                "UPDATE firm_clients"
                " SET item = item || jsonb_build_object(%s::text, %s::text)"
                f" WHERE {active} AND NOT item ? '{MERGING_INTO}'"
                f" AND COALESCE(item ->> '{MERGING_FROM}', %s) = %s",
                (MERGING_FROM, merged_id, firm_id, survivor_id, merged_id, merged_id),
            ),
        )

    def finish_client_merge(
        self, firm_id: str, *, merged_id: str, survivor_id: str, merged_at: str
    ) -> FirmClient | None:
        finished = self._both(
            (
                "UPDATE firm_clients SET item = (item - %s::text)"
                " || jsonb_build_object('status', %s::text, %s::text, %s::text,"
                " 'updatedAt', %s::text)"
                " WHERE firm_id = %s AND id = %s AND item ->> %s::text = %s",
                (
                    MERGING_INTO,
                    ARCHIVED,
                    MERGED_INTO,
                    survivor_id,
                    merged_at,
                    firm_id,
                    merged_id,
                    MERGING_INTO,
                    survivor_id,
                ),
            ),
            (
                "UPDATE firm_clients SET item = item - %s::text"
                " WHERE firm_id = %s AND id = %s AND item ->> %s::text = %s",
                (MERGING_FROM, firm_id, survivor_id, MERGING_FROM, merged_id),
            ),
        )
        return self.get_client(firm_id, merged_id) if finished else None

    def release_client_merge(
        self, firm_id: str, *, merged_id: str, survivor_id: str
    ) -> None:
        # Two independent conditional removals, not one transaction: half of
        # the claim already gone must not stop the other half going.
        for client_id, attribute, other in (
            (merged_id, MERGING_INTO, survivor_id),
            (survivor_id, MERGING_FROM, merged_id),
        ):
            self._write(
                "UPDATE firm_clients SET item = item - %s::text"
                " WHERE firm_id = %s AND id = %s AND item ->> %s::text = %s",
                (attribute, firm_id, client_id, attribute, other),
            )

    # ── The client questionnaire's config ───────────────────────────

    def get_questionnaire(self, firm_id: str) -> QuestionnaireConfig | None:
        item = self._one(
            "SELECT item FROM firm_questionnaires WHERE firm_id = %s", (firm_id,)
        )
        return None if item is None else questionnaire_from_item(item)

    def put_questionnaire(self, config: QuestionnaireConfig) -> None:
        # Last writer wins (the port's whole-record save).
        self._write(
            "INSERT INTO firm_questionnaires (firm_id, item) VALUES (%s, %s)"
            " ON CONFLICT (firm_id) DO UPDATE SET item = EXCLUDED.item",
            (config.firm_id, stored(questionnaire_item(config))),
        )

    def delete_questionnaire(self, firm_id: str) -> bool:
        return bool(
            self._write(
                "DELETE FROM firm_questionnaires WHERE firm_id = %s", (firm_id,)
            )
        )

    # ── The document request checklist ──────────────────────────────

    def get_document_checklist(self, firm_id: str) -> DocumentChecklist | None:
        item = self._one(
            "SELECT item FROM firm_document_checklists WHERE firm_id = %s", (firm_id,)
        )
        return None if item is None else checklist_from_item(item)

    def put_document_checklist(self, checklist: DocumentChecklist) -> None:
        self._write(
            "INSERT INTO firm_document_checklists (firm_id, item) VALUES (%s, %s)"
            " ON CONFLICT (firm_id) DO UPDATE SET item = EXCLUDED.item",
            (checklist.firm_id, stored(checklist_item(checklist))),
        )

    def delete_document_checklist(self, firm_id: str) -> bool:
        return bool(
            self._write(
                "DELETE FROM firm_document_checklists WHERE firm_id = %s", (firm_id,)
            )
        )
