from __future__ import annotations

from insolvia_core.documents import Document, list_order


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
        # The dict equivalent of attribute_not_exists(SK): a key that is
        # already present is refused, whatever it maps to. It refuses the same
        # overwrite the real store refuses rather than being quietly looser — a
        # suite running against the looser of the two proves nothing about the
        # one that holds the data.
        #
        # `setdefault(...) is not document` looked like the same check and was
        # not. Creating twice with the SAME Document instance made setdefault
        # return that very object, the identity test passed, and the second
        # create succeeded silently — while DynamoDB's condition, which knows
        # nothing about Python identity, would have raised. The looseness was
        # exactly the one the comment promised was absent, and it was invisible
        # because it needed the same instance twice to show up.
        key = (document.case_id, document.id)
        if key in self.documents:
            raise RuntimeError(f"document {key} already exists")
        self.documents[key] = document

    def update(self, document: Document) -> Document | None:
        # attribute_exists(SK), as a dict: a row that is not there is not
        # written. Returning None rather than raising, matching the real store
        # and MemoryCaseStore.update — the route turns it into a 404, because
        # the document was deleted while this request was in flight.
        key = (document.case_id, document.id)
        if key not in self.documents:
            return None
        self.documents[key] = document
        return document

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
