"""DocumentRequestStore over Postgres (ADR 0030): the `document_requests`
table."""

from __future__ import annotations

from insolvia_core.adapters.postgres.children import ChildTable
from insolvia_core.document_requests import (
    DocumentRequest,
    list_order,
    request_from_item,
    request_item,
)


class PostgresDocumentRequestStore:
    def __init__(self, database_url: str) -> None:
        self.rows = ChildTable(database_url, "document_requests", ("case_id", "id"))

    def create(self, request: DocumentRequest) -> None:
        self.rows.create(
            {"case_id": request.case_id, "id": request.id}, request_item(request)
        )

    def get(self, case_id: str, request_id: str) -> DocumentRequest | None:
        item = self.rows.get({"case_id": case_id, "id": request_id})
        return None if item is None else request_from_item(item)

    def update(self, request: DocumentRequest) -> DocumentRequest | None:
        key = {"case_id": request.case_id, "id": request.id}
        return request if self.rows.replace(key, request_item(request)) else None

    def list_for_case(self, case_id: str) -> tuple[DocumentRequest, ...]:
        items = self.rows.list({"case_id": case_id})
        return tuple(sorted(map(request_from_item, items), key=list_order))

    def delete(self, case_id: str, request_id: str) -> bool:
        return self.rows.delete({"case_id": case_id, "id": request_id})
