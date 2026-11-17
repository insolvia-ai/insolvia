"""DocumentStore over Postgres (ADR 0030): the `documents` table. A document
id only resolves inside its own case — the key is (case_id, id)."""

from __future__ import annotations

from insolvia_core.adapters.postgres.children import ChildTable
from insolvia_core.documents import (
    Document,
    document_from_item,
    document_item,
    list_order,
)


class PostgresDocumentStore:
    def __init__(self, database_url: str) -> None:
        self.rows = ChildTable(database_url, "documents", ("case_id", "id"))

    def create(self, document: Document) -> None:
        self.rows.create(
            {"case_id": document.case_id, "id": document.id}, document_item(document)
        )

    def update(self, document: Document) -> Document | None:
        key = {"case_id": document.case_id, "id": document.id}
        return document if self.rows.replace(key, document_item(document)) else None

    def get(self, case_id: str, document_id: str) -> Document | None:
        item = self.rows.get({"case_id": case_id, "id": document_id})
        return None if item is None else document_from_item(item)

    def list_for_case(self, case_id: str) -> tuple[Document, ...]:
        items = self.rows.list({"case_id": case_id})
        return tuple(
            sorted(map(document_from_item, items), key=list_order, reverse=True)
        )

    def delete(self, case_id: str, document_id: str) -> bool:
        # Conditional, as the route needs: it deletes the bytes only after
        # this answers True, so two concurrent deletes cannot both proceed.
        return self.rows.delete({"case_id": case_id, "id": document_id})
