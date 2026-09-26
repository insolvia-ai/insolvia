"""Free-text notes, case-scoped and optionally anchored to a form (issue
14.5 / #357): one line a paralegal leaves on the case, or on Schedule G while
they are looking at it.

A note is the same KIND of thing case_entities.py already generalises — a
many-cardinality, uuid-keyed record living in its case's partition — so it
reuses that machinery (`EntityKind`, `CaseEntity`, `CaseEntityStore`, the
create/replace helpers) rather than inventing a second storage shape. It is
NOT registered in `case_collections.COLLECTIONS`, though, and has its own
route module (`insolvia_api.api.routes.notes`) rather than riding the generic
`/v1/cases/<id>/<collection>` dispatch, for two reasons neither the registry
nor the generic routes can express:

1. **The author is server-stamped, never client-supplied.** "the caller's
   subject and display name captured at write" (the issue's words) has to
   come from the resolved `Accessor`, the same way a document's `uploaded_by`
   does — and the generic create/replace routes never see one, by design
   (case_entities.py: "Ownership is NOT a parameter here"). `stamp_author`
   below is the seam: the note route calls it AFTER `NOTE.parse_body` and
   BEFORE storage, unconditionally, on every create and every edit — see
   `parse_note_body`'s docstring for why the parser itself still reads these
   two fields rather than refusing them outright. An edit re-stamps the
   ORIGINAL author rather than the editor, so an admin fixing a colleague's
   typo cannot reassign authorship as a side effect.
2. **A note is editable by its author or a firm admin — the generic routes
   have no per-record ownership at all.** `may_edit_note` below is the
   smallest seam that adds it: one function, consulted by the note routes
   before a PUT or DELETE, leaving every other collection's routes (and their
   tests) untouched.

**Provenance is not carried.** docs/reference/case-data-model.md's per-field
provenance map exists for CASE DATA — the facts a schedule prints and an
extraction pipeline can propose values for. A note is neither: it is not
printed on any form (`form_series` is a cross-reference to the form the
preparer was looking at, not a value that lands on one) and nothing ever
extracts one. So a note carries no provenance map, the same exemption
`core/documents.py` states for a document row ("provenance's object rather
than its subject") — `NOTE.parse_body` never reads a `provenance` key, and
the note routes never call `require_provenance`.
"""

from __future__ import annotations

from collections.abc import Mapping
from dataclasses import dataclass, replace

from insolvia_core.errors import FieldValidationError

from .case_entities import CaseEntity, EntityKind, entity_body
from .fields import prune_body, text


@dataclass(frozen=True)
class NoteBody:
    """A note's case data, including the author snapshot — captured at write,
    per the issue, so it rides in the body rather than being derived from the
    firm's user list on every read (a colleague who is later renamed or
    removed must not change what an old note says)."""

    text: str | None = None
    # A form series id (`form_summary.series` on the forms hub, e.g.
    # "form/b106g") — the form the note is anchored to, or None for a
    # case-level note. Not validated against the case's current packet: the
    # set of series a case files depends on its chapter and changes as intake
    # proceeds, and a note anchored to a form that later drops out of the
    # packet should still say what it was about rather than become invalid.
    form_series: str | None = None
    author_subject: str | None = None
    author_name: str | None = None


def parse_note_body(payload: Mapping[str, object]) -> NoteBody:
    """Validate a note body — SHAPE AND TYPE ONLY, the same storage-validation
    contract every entity parser follows.

    `author_subject`/`author_name` ARE read here, even though a create or edit
    REQUEST never legitimately carries them — the reason is `entity_from_item`
    (case_entities.py): reconstructing a stored note from its item calls this
    same function, and an author who cannot survive a reload is not "captured
    at write", it is dropped on the first read. Client-supplied authorship is
    still never trusted: `insolvia_api.api.routes.notes` calls `stamp_author`
    unconditionally after this returns, on every create AND every edit, which
    overwrites whatever these two fields hold — a real caller's subject and
    display name on create, the STORED note's original author on edit — before
    anything is saved. A spoofed value here would be a spoofed value that
    never reaches storage.
    """
    errors: dict[str, str] = {}
    body = NoteBody(
        text=text(payload.get("text"), "text", errors, limit=2000),
        form_series=text(payload.get("form_series"), "form_series", errors),
        author_subject=text(payload.get("author_subject"), "author_subject", errors),
        author_name=text(payload.get("author_name"), "author_name", errors),
    )
    if errors:
        raise FieldValidationError(errors)
    return body


NOTE: EntityKind[NoteBody] = EntityKind(
    name="note",
    collection="notes",
    sk_prefix="NOTE",
    parse_body=parse_note_body,
)


def stamp_author(body: NoteBody, *, subject: str, author_name: str) -> NoteBody:
    """A copy of `body` carrying who wrote it. The ONE place authorship is
    assigned — called on create with the caller's own identity, and never
    called again on edit (the edit route keeps the stored `CaseEntity`'s
    existing body fields for these two, so a firm admin fixing someone else's
    note cannot relabel it as their own)."""
    return replace(body, author_subject=subject, author_name=author_name)


def may_edit_note(note: CaseEntity[NoteBody], *, subject: str, is_admin: bool) -> bool:
    """Whether `subject` may PUT or DELETE this note: its author, or a firm
    admin. THE SMALLEST SEAM the generic case-collection routes lack — see
    the module docstring — consulted by `insolvia_api.api.routes.notes`
    before either write, in addition to the ordinary `@requires(NOTES, ...)`
    feature check."""
    return is_admin or note.body.author_subject == subject


def note_json(note: CaseEntity[NoteBody]) -> dict[str, object]:
    """The API representation. Deliberately NOT `entity_json` — that always
    emits a `provenance` key, and a note carries none at all (see the module
    docstring), the same omission `document_json` makes for a document row."""
    return {
        "id": note.id,
        "case_id": note.case_id,
        "created_at": note.created_at,
        "updated_at": note.updated_at,
        **prune_body(entity_body(note)),
    }
