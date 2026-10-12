from __future__ import annotations

from insolvia_core.document_requests import (
    DocumentRequest,
    list_order,
    request_from_item,
    request_item,
)


class MemoryDocumentRequestStore:
    """Ephemeral DocumentRequestStore for tests and the plain development
    server.

    Keyed by (case_id, request_id) — the DynamoDB adapter's PK and SK — and
    holding the STORED ITEM rather than the dataclass, as MemoryFirmStore
    does for the questionnaire: a suite running here round-trips the shape
    DynamoDB writes, list-valued `documentIds` included.
    """

    def __init__(self) -> None:
        self.items: dict[tuple[str, str], dict[str, object]] = {}

    def create(self, request: DocumentRequest) -> None:
        key = (request.case_id, request.id)
        if key in self.items:
            raise RuntimeError(f"document request {key} already exists")
        self.items[key] = request_item(request)

    def get(self, case_id: str, request_id: str) -> DocumentRequest | None:
        item = self.items.get((case_id, request_id))
        return None if item is None else request_from_item(item)

    def update(self, request: DocumentRequest) -> DocumentRequest | None:
        key = (request.case_id, request.id)
        if key not in self.items:
            return None
        self.items[key] = request_item(request)
        return request

    def list_for_case(self, case_id: str) -> tuple[DocumentRequest, ...]:
        return tuple(
            sorted(
                (
                    request_from_item(item)
                    for (stored_case_id, _), item in self.items.items()
                    if stored_case_id == case_id
                ),
                key=list_order,
            )
        )

    def delete(self, case_id: str, request_id: str) -> bool:
        return self.items.pop((case_id, request_id), None) is not None
