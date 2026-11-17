"""Case and form notes (issue 14.5 / #357): parsing, authorship, ownership.

Weighted towards the three things `core/notes.py` adds beyond the generic
case-entity machinery it reuses: authorship is never read from the payload,
`stamp_author` is the only way it gets set, and `may_edit_note` is the whole
ownership rule. Every identifier below is obviously fake; this repo is public.
"""

from __future__ import annotations

import pytest
from insolvia_core.case_entities import (
    EntityDraft,
    create_entity,
    entity_from_item,
    entity_item,
    replace_entity,
)
from insolvia_core.errors import FieldValidationError
from insolvia_core.notes import (
    NOTE,
    may_edit_note,
    note_json,
    parse_note_body,
    stamp_author,
)

CASE = "case-0001"
ALICE = "00000000-0000-4000-8000-00000000a11c"
BOB = "00000000-0000-4000-8000-00000000b0b0"


def test_parse_note_body_reads_text_and_form_series() -> None:
    body = parse_note_body(
        {"text": "Client called about the car loan.", "form_series": "form/b106g"}
    )
    assert body.text == "Client called about the car loan."
    assert body.form_series == "form/b106g"
    assert body.author_subject is None
    assert body.author_name is None


def test_parse_note_body_reads_author_fields_when_present() -> None:
    """`entity_from_item` reconstructs a stored note through this same
    parser, so it must read `author_subject`/`author_name` back — the safety
    property is that the ROUTE always overwrites them with `stamp_author`
    before anything is saved, not that the parser refuses to see them."""
    body = parse_note_body(
        {"text": "note", "author_subject": ALICE, "author_name": "Alice Attorney"}
    )
    assert body.author_subject == ALICE
    assert body.author_name == "Alice Attorney"


def test_stamp_author_overwrites_whatever_the_parser_saw() -> None:
    """The actual safety property: `stamp_author` always wins, so a request
    that tried to set authorship never reaches storage with it."""
    spoofed = parse_note_body(
        {"text": "note", "author_subject": BOB, "author_name": "Bob Impersonator"}
    )
    stamped = stamp_author(spoofed, subject=ALICE, author_name="Alice Attorney")
    assert stamped.author_subject == ALICE
    assert stamped.author_name == "Alice Attorney"


def test_parse_note_body_allows_an_absent_form_series() -> None:
    body = parse_note_body({"text": "A case-level note."})
    assert body.form_series is None


def test_parse_note_body_rejects_non_text_value() -> None:
    with pytest.raises(FieldValidationError) as excinfo:
        parse_note_body({"text": 12345})
    assert "text" in excinfo.value.fields


def test_stamp_author_fills_in_the_caller() -> None:
    body = parse_note_body({"text": "note"})
    stamped = stamp_author(body, subject=ALICE, author_name="Alice Attorney")
    assert stamped.author_subject == ALICE
    assert stamped.author_name == "Alice Attorney"
    # Everything else survives unchanged.
    assert stamped.text == "note"


def test_note_carries_no_provenance_map() -> None:
    """docs/reference/case-data-model.md exempts documents from provenance;
    core/notes.py's module docstring makes the same argument for notes. The
    entity round-trips through the generic machinery with an always-empty
    map, and `note_json` — unlike the generic `entity_json` — omits the key
    entirely rather than emitting an empty object."""
    body = stamp_author(
        parse_note_body({"text": "note"}), subject=ALICE, author_name="Alice Attorney"
    )
    entity = create_entity(NOTE, EntityDraft(body=body, provenance={}), case_id=CASE)
    assert entity.provenance == {}
    assert "provenance" not in note_json(entity)


def test_note_json_shape() -> None:
    body = stamp_author(
        parse_note_body({"text": "note", "form_series": "form/b106g"}),
        subject=ALICE,
        author_name="Alice Attorney",
    )
    entity = create_entity(NOTE, EntityDraft(body=body, provenance={}), case_id=CASE)
    assert note_json(entity) == {
        "id": entity.id,
        "case_id": CASE,
        "created_at": entity.created_at,
        "updated_at": entity.updated_at,
        "text": "note",
        "form_series": "form/b106g",
        "author_subject": ALICE,
        "author_name": "Alice Attorney",
    }


def test_note_round_trips_through_the_stored_item_shape() -> None:
    body = stamp_author(
        parse_note_body({"text": "note", "form_series": "form/b106g"}),
        subject=ALICE,
        author_name="Alice Attorney",
    )
    entity = create_entity(NOTE, EntityDraft(body=body, provenance={}), case_id=CASE)
    item = entity_item(entity)
    assert item["SK"].startswith("NOTE#")

    restored = entity_from_item(NOTE, item)
    assert restored.body == entity.body
    assert restored.id == entity.id


def test_may_edit_note_permits_the_author() -> None:
    body = stamp_author(
        parse_note_body({"text": "note"}), subject=ALICE, author_name="Alice Attorney"
    )
    entity = create_entity(NOTE, EntityDraft(body=body, provenance={}), case_id=CASE)
    assert may_edit_note(entity, subject=ALICE, is_admin=False) is True


def test_may_edit_note_permits_a_firm_admin() -> None:
    body = stamp_author(
        parse_note_body({"text": "note"}), subject=ALICE, author_name="Alice Attorney"
    )
    entity = create_entity(NOTE, EntityDraft(body=body, provenance={}), case_id=CASE)
    assert may_edit_note(entity, subject=BOB, is_admin=True) is True


def test_may_edit_note_refuses_a_non_author_non_admin() -> None:
    body = stamp_author(
        parse_note_body({"text": "note"}), subject=ALICE, author_name="Alice Attorney"
    )
    entity = create_entity(NOTE, EntityDraft(body=body, provenance={}), case_id=CASE)
    assert may_edit_note(entity, subject=BOB, is_admin=False) is False


def test_editing_a_note_can_change_the_text_without_changing_the_author() -> None:
    """The edit route's own responsibility (re-stamping with the STORED
    author) lives in insolvia_api.api.routes.notes — this only pins that
    `replace_entity` preserves whatever body it is given, which is what that
    route depends on."""
    original = stamp_author(
        parse_note_body({"text": "first draft"}),
        subject=ALICE,
        author_name="Alice Attorney",
    )
    entity = create_entity(
        NOTE, EntityDraft(body=original, provenance={}), case_id=CASE
    )

    edited_by_admin = stamp_author(
        parse_note_body({"text": "corrected draft"}),
        subject=entity.body.author_subject or BOB,
        author_name=entity.body.author_name or "someone else",
    )
    replaced = replace_entity(entity, EntityDraft(body=edited_by_admin, provenance={}))
    assert replaced.body.text == "corrected draft"
    assert replaced.body.author_subject == ALICE
    assert replaced.body.author_name == "Alice Attorney"
    assert replaced.id == entity.id
    assert replaced.created_at == entity.created_at
