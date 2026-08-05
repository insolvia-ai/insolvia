from __future__ import annotations

from insolvia_api.core.documents import Document, list_order


class MemoryDocumentStore:
    """Ephemeral DocumentStore for tests and the plain development server.

    Keyed by (case_id, document_id) — the DynamoDB adapter's PK and SK — so
    "a document id only resolves inside its own case" is a property of this
    dict rather than something every caller has to remember, exactly as it is a
    property of the table's key schema on the other side. A store keyed by
    document id alone would pass every test in the suite and would hand one
    firm's document to another the moment an id leaked.
    """

    def __init__(self) -> None:
        self.documents: dict[tuple[str, str], Document] = {}

    def create(self, document: Document) -> None:
        # setdefault is the conditional write: the dict equivalent of
        # attribute_not_exists(SK). It refuses the same overwrite the real
        # store refuses rather than being quietly looser — a suite running
        # against the looser of the two proves nothing about the one that
        # holds the data.
        key = (document.case_id, document.id)
        if self.documents.setdefault(key, document) is not document:
            raise RuntimeError(f"document {key} already exists")

    def get(self, case_id: str, document_id: str) -> Document | None:
        return self.documents.get((case_id, document_id))

    def list_for_case(self, case_id: str) -> tuple[Document, ...]:
        return tuple(
            sorted(
                (
                    document
                    for (stored_case_id, _), document in self.documents.items()
                    if stored_case_id == case_id
                ),
                key=list_order,
                reverse=True,
            )
        )

    def delete(self, case_id: str, document_id: str) -> bool:
        return self.documents.pop((case_id, document_id), None) is not None
