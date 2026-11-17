"""The in-memory DocumentStore, tested directly (issue 8.6).

Almost everything about this adapter is covered through the routes that use
it, and that is where it belongs. What CANNOT be observed from a route is the
one property this file exists for: the memory store must refuse exactly the
writes DynamoDB's `attribute_not_exists(SK)` refuses. A route never calls
`create` twice with the same Document instance, so a looseness that only shows
up under that call is invisible from above — and a suite running against the
looser of two stores proves nothing about the one that holds the data.

Every identifier below is obviously fake. This repo is public.
"""

from __future__ import annotations

from dataclasses import replace

import pytest
from insolvia_core.adapters.memory.document_store import MemoryDocumentStore
from insolvia_core.documents import Document

CASE_ID = "00000000-0000-4000-8000-0000000000ca"
OTHER_CASE_ID = "00000000-0000-4000-8000-0000000000cb"
DOCUMENT_ID = "00000000-0000-4000-8000-0000000000d0"
ALICE = "00000000-0000-4000-8000-00000000a11c"


def document(
    document_id: str = DOCUMENT_ID, case_id: str = CASE_ID, file_name: str = "a.pdf"
) -> Document:
    return Document(
        id=document_id,
        case_id=case_id,
        kind="pay_stub",
        file_name=file_name,
        content_type="application/pdf",
        byte_size=1024,
        storage_ref=f"cases/{case_id}/{document_id}",
        uploaded_by=ALICE,
        uploaded_at="2026-01-01T00:00:00.000Z",
    )


def test_creating_the_same_document_twice_is_refused():
    """The conditional write, with two DIFFERENT objects for one key.

    DynamoDB refuses this on the key alone. So must this."""
    store = MemoryDocumentStore()
    store.create(document())
    with pytest.raises(RuntimeError):
        store.create(document(file_name="b.pdf"))


def test_creating_the_same_instance_twice_is_refused():
    """THE FAILURE THIS FILE EXISTS FOR.

    The check used to be `setdefault(key, document) is not document`, which
    reads like a conditional write and is not one. Handed the SAME instance
    twice, setdefault returns that very object, the identity comparison is
    false, and the second create succeeds silently — while DynamoDB's
    `attribute_not_exists(SK)`, which knows nothing about Python identity,
    raises. The store was looser than the one holding the data in precisely
    the way its own comment promised it was not, and no route could show it:
    a route builds a fresh Document per request.
    """
    store = MemoryDocumentStore()
    only = document()
    store.create(only)
    with pytest.raises(RuntimeError):
        store.create(only)
    # And the first write is still the one that stands.
    assert store.get(CASE_ID, DOCUMENT_ID) is only


def test_update_refuses_a_row_that_is_not_there():
    """attribute_exists(SK), as a dict.

    This is the window between the confirm route reading a document and
    writing it back: if a delete lands in between, the write must not
    resurrect the row. It would come back as `stored` — a document the case
    lists, pointing at an object the delete already marked, that nobody can
    open. None rather than an exception, so the route can answer the same 404
    a foreign document id gets.
    """
    store = MemoryDocumentStore()
    assert store.update(document()) is None
    assert store.documents == {}


def test_update_replaces_an_existing_row():
    store = MemoryDocumentStore()
    store.create(document())
    confirmed = replace(document(), status="stored", byte_size=99, etag="deadbeef")

    assert store.update(confirmed) == confirmed
    assert store.get(CASE_ID, DOCUMENT_ID) == confirmed


def test_the_same_document_id_in_another_case_is_a_different_row():
    """The key is (case, document), as it is in the table. If it were the
    document id alone, this create would collide — and a document id leaked
    from one firm's case would resolve inside another's."""
    store = MemoryDocumentStore()
    store.create(document())
    store.create(document(case_id=OTHER_CASE_ID))
    assert store.get(CASE_ID, DOCUMENT_ID) is not None
    assert store.get(OTHER_CASE_ID, DOCUMENT_ID) is not None
    assert len(store.list_for_case(CASE_ID)) == 1
