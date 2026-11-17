"""PacketStore over Postgres (ADR 0030): the `packets` table, and the pinned
case written in the same transaction."""

from __future__ import annotations

import psycopg
from insolvia_core.adapters.postgres.case_store import write_case
from insolvia_core.adapters.postgres.children import ChildTable
from insolvia_core.adapters.postgres.connection import connect
from insolvia_core.adapters.postgres.queries import document
from insolvia_core.cases import FILED_STATUSES, Case
from psycopg import sql

from insolvia_api.core.packets import (
    Packet,
    list_order,
    packet_from_item,
    packet_item,
)


class _RefusedError(Exception):
    """The case condition failed: roll the packet back, answer False."""


class PostgresPacketStore:
    def __init__(self, database_url: str) -> None:
        self.database_url = database_url
        self.rows = ChildTable(database_url, "packets", ("case_id", "id"))

    def create(
        self,
        packet: Packet,
        *,
        pinned_case: Case,
        expected_updated_at: str,
        allow_filed: bool = False,
    ) -> bool:
        """ONE TRANSACTION: the packet and the pinned case (core/ports owns
        the argument). The case write carries the whole condition: the row
        still exists, has not moved since the worker read it (the
        `updatedAt` lock), and — unless `allow_filed` (#370's amendedOnly
        exception) — is not filed, closing the window where filing lands
        mid-assembly."""
        condition = sql.SQL("updated_at = %s")
        params: list[object] = [expected_updated_at]
        if not allow_filed:
            condition = sql.SQL("updated_at = %s AND status <> ALL(%s)")
            params.append(sorted(FILED_STATUSES))
        try:
            with connect(self.database_url) as conn, conn.transaction():
                try:
                    conn.execute(
                        "INSERT INTO packets (case_id, id, firm_id, item)"
                        " SELECT id, %s, firm_id, %s FROM cases WHERE id = %s",
                        (packet.id, document(packet_item(packet)), packet.case_id),
                    )
                except psycopg.errors.UniqueViolation as error:
                    raise RuntimeError(
                        "packet id already exists in this case"
                    ) from error
                if write_case(conn, pinned_case, condition, params) == 0:
                    raise _RefusedError
        except _RefusedError:
            return False
        return True

    def get(self, case_id: str, packet_id: str) -> Packet | None:
        item = self.rows.get({"case_id": case_id, "id": packet_id})
        return None if item is None else packet_from_item(item)

    def list_for_case(self, case_id: str) -> tuple[Packet, ...]:
        items = self.rows.list({"case_id": case_id})
        return tuple(sorted(map(packet_from_item, items), key=list_order, reverse=True))
