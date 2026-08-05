"""Where every stored value came from, and the two rules that makes enforceable.

docs/reference/case-data-model.md puts provenance on every case-scoped record,
keyed by field path, and states two invariants that hold "in the core layer's
parse functions, so that every write path inherits them rather than each
endpoint remembering". This module is that enforcement, written once so the
per-entity parsers (debtor, claim, sofa_entry, …) call it rather than each
re-deriving it:

1. Every populated field carries a provenance entry. A record with a value and
   no entry for it is rejected — otherwise rule 2 is evaded by omitting the key.
2. A field whose source is machine-supplied and whose ``confirmed_at`` is null
   cannot exist on a case record. Not "should not": the write is rejected.

Together those make "nothing extracted enters the case until a human confirms
it" a property of the store rather than a promise about the UI.

WHAT THIS MODULE IS NOT. It does not check completeness. Intake is progressive
and a half-finished questionnaire must persist, so absent values are fine
everywhere; whether a given chapter's forms have what they need is a pre-filing
check owned by the forms engine. Conflating the two would make it impossible to
save an intake in progress, which is the one thing the questionnaire must never
fail at.
"""

from __future__ import annotations

import re
from collections.abc import Mapping, Sequence
from dataclasses import dataclass
from typing import Final

from .errors import FieldValidationError

# Who supplied a value.
#
# `imported` sits with `ai_extracted` rather than with `staff_typed`, and the
# data model is explicit about why: machine-supplied is machine-supplied, and
# the source system does not change who is signing the form. Anything in
# MACHINE_SOURCES is subject to the confirmation rule below.
SOURCES: Final = ("staff_typed", "ai_extracted", "imported")
MACHINE_SOURCES: Final = frozenset({"ai_extracted", "imported"})

# A dotted field path, with embedded list elements addressed by their id in
# brackets: `name.surname`, `other_names_used[3f9c…].surname`.
#
# The id form is the reason this is a regex rather than a split on ".". Paths
# address list elements BY ID rather than by position so that reordering a list
# does not silently reattach provenance to the wrong value — which is also why
# embedded list elements carry an id at all. A positional path would make
# "delete the first alias" quietly relabel every alias after it.
_SEGMENT = r"[a-z][a-z0-9_]*(?:\[[A-Za-z0-9_-]+\])?"
_FIELD_PATH_RE: Final = re.compile(rf"^{_SEGMENT}(?:\.{_SEGMENT})*$")

_TIMESTAMP_RE: Final = re.compile(r"^\d{4}-\d{2}-\d{2}T[\d:.]+Z$")


@dataclass(frozen=True)
class ProvenanceEntry:
    """Where one field's value came from.

    `confirmed_by`/`confirmed_at` are one act, not two fields — a human said
    "yes, that is right" at a moment. They are also exactly what the extraction
    review flow writes, which is why the same pair appears on
    `extraction_candidate`: accepting a candidate IS setting these.
    """

    source: str
    confirmed_by: str | None = None
    confirmed_at: str | None = None
    document_id: str | None = None
    locator: Mapping[str, object] | None = None
    extraction_id: str | None = None
    confidence: float | None = None


def _parse_entry(
    path: str, value: object, errors: dict[str, str]
) -> ProvenanceEntry | None:
    if not isinstance(value, Mapping):
        errors[f"provenance.{path}"] = "Provenance must be an object."
        return None

    source = value.get("source")
    if not isinstance(source, str) or source not in SOURCES:
        errors[f"provenance.{path}.source"] = (
            "Source must be one of " + ", ".join(SOURCES) + "."
        )
        return None

    confirmed_at = value.get("confirmed_at")
    if confirmed_at is not None and (
        not isinstance(confirmed_at, str) or not _TIMESTAMP_RE.match(confirmed_at)
    ):
        errors[f"provenance.{path}.confirmed_at"] = (
            "confirmed_at must be a UTC timestamp."
        )
        return None

    confirmed_by = value.get("confirmed_by")
    if confirmed_by is not None and not isinstance(confirmed_by, str):
        errors[f"provenance.{path}.confirmed_by"] = "confirmed_by must be a string."
        return None

    # INVARIANT 2. A machine-supplied value that no human has confirmed does not
    # get to exist on a case record. Unconfirmed extraction output lives in
    # `extraction_candidate`, outside the case, until someone accepts it.
    #
    # Both halves are required: `confirmed_at` is the moment and `confirmed_by`
    # is the person, and a confirmation with no one attached to it cannot be
    # audited, which is the entire point of recording it.
    if source in MACHINE_SOURCES and (confirmed_at is None or confirmed_by is None):
        errors[f"provenance.{path}"] = (
            f"A {source} value must be confirmed by a person before it can be stored."
        )
        return None

    confidence = value.get("confidence")
    if confidence is not None:
        if isinstance(confidence, bool) or not isinstance(confidence, (int, float)):
            errors[f"provenance.{path}.confidence"] = "confidence must be a number."
            return None
        if not 0.0 <= float(confidence) <= 1.0:
            errors[f"provenance.{path}.confidence"] = (
                "confidence must be between 0 and 1."
            )
            return None
        confidence = float(confidence)

    locator = value.get("locator")
    if locator is not None and not isinstance(locator, Mapping):
        errors[f"provenance.{path}.locator"] = "locator must be an object."
        return None

    document_id = value.get("document_id")
    if document_id is not None and not isinstance(document_id, str):
        errors[f"provenance.{path}.document_id"] = "document_id must be a string."
        return None

    extraction_id = value.get("extraction_id")
    if extraction_id is not None and not isinstance(extraction_id, str):
        errors[f"provenance.{path}.extraction_id"] = "extraction_id must be a string."
        return None

    return ProvenanceEntry(
        source=source,
        confirmed_by=confirmed_by,
        confirmed_at=confirmed_at,
        document_id=document_id,
        locator=dict(locator) if locator is not None else None,
        extraction_id=extraction_id,
        confidence=confidence,
    )


def parse_provenance(value: object) -> dict[str, ProvenanceEntry]:
    """Validate the `provenance` map of a record. Raises on any bad entry.

    Every key must be a well-formed field path; that is checked here rather than
    only against the record's own fields, because a typo'd path would otherwise
    be stored as provenance for a field nobody ever reads — present in the map,
    absent where it matters, and invisible until an audit.
    """
    if value is None:
        return {}
    if not isinstance(value, Mapping):
        raise FieldValidationError({"provenance": "Provenance must be an object."})

    errors: dict[str, str] = {}
    entries: dict[str, ProvenanceEntry] = {}
    for path, raw in value.items():
        if not isinstance(path, str) or not _FIELD_PATH_RE.match(path):
            errors[f"provenance.{path}"] = "Not a field path."
            continue
        entry = _parse_entry(path, raw, errors)
        if entry is not None:
            entries[path] = entry

    if errors:
        raise FieldValidationError(errors)
    return entries


def populated_paths(record: object, prefix: str = "") -> list[str]:
    """Every field path in `record` that actually holds a value.

    The definition of "populated" is deliberate and worth stating, because
    invariant 1 is only as good as it:

    - ``None`` is absent. Progressive intake means most of a record is None
      most of the time, and requiring provenance for nothing would make an
      empty record unsavable.
    - An empty string, list or map is ALSO absent. A field the user cleared has
      no origin to record, and demanding one would mean writing provenance for
      the act of deleting.
    - ``False`` and ``0`` are PRESENT. They are answers — "no, I do not rent my
      residence" is a fact someone asserted, and it is exactly the kind of
      answer an extraction can get wrong. Treating falsy-as-absent here is the
      classic bug this note exists to prevent.

    Nested maps recurse; lists recurse into their elements, addressed by the
    element's own ``id`` so a reordering does not move provenance. That ``id``
    is NOT itself emitted as a path: it is already the address — the element's
    paths all read ``aliases[n1].…`` — so ``aliases[n1].id`` would be asking
    for the provenance of an address rather than of a fact about the case.
    """
    paths: list[str] = []

    if isinstance(record, Mapping):
        for key, value in record.items():
            if not isinstance(key, str):
                continue
            if key == "id" and prefix.endswith("]"):
                continue
            paths.extend(populated_paths(value, f"{prefix}.{key}" if prefix else key))
        return paths

    if isinstance(record, (str, bytes)):
        return [prefix] if record and prefix else []

    if isinstance(record, Sequence):
        for element in record:
            # An element without an id cannot be addressed stably, so the whole
            # list is attributed to its own path rather than silently indexed.
            element_id = element.get("id") if isinstance(element, Mapping) else None
            if not isinstance(element_id, str) or not element_id:
                return [prefix] if prefix else []
            paths.extend(populated_paths(element, f"{prefix}[{element_id}]"))
        return paths

    if record is None:
        return []
    return [prefix] if prefix else []


def require_provenance(
    record: Mapping[str, object], entries: Mapping[str, ProvenanceEntry]
) -> None:
    """INVARIANT 1: every populated field carries a provenance entry.

    Checked against the record as submitted, so it cannot be satisfied by
    sending provenance for fields that are not there — and cannot be evaded by
    omitting the key, which is the loophole that would make invariant 2
    decorative.

    `id` and `case_id` are exempt: they are assigned by the server, not
    supplied by anyone, so "where did this come from" has one answer and it is
    not a fact about the case.
    """
    missing = [
        path
        for path in populated_paths(record)
        if path not in entries
        and path.split(".", 1)[0].split("[", 1)[0] not in _SERVER_OWNED
    ]
    if missing:
        raise FieldValidationError(
            {
                f"provenance.{path}": "This field has a value but no provenance."
                for path in missing
            }
        )


_SERVER_OWNED: Final = frozenset({"id", "case_id", "created_at", "updated_at"})


def provenance_json(
    entries: Mapping[str, ProvenanceEntry],
) -> dict[str, dict[str, object]]:
    """Serialise for storage and for the API response. Null members are dropped
    rather than stored as nulls — a `staff_typed` entry is three quarters empty,
    and this map is carried on every record."""
    out: dict[str, dict[str, object]] = {}
    for path, entry in entries.items():
        member: dict[str, object] = {"source": entry.source}
        for key, value in (
            ("confirmed_by", entry.confirmed_by),
            ("confirmed_at", entry.confirmed_at),
            ("document_id", entry.document_id),
            ("locator", entry.locator),
            ("extraction_id", entry.extraction_id),
            ("confidence", entry.confidence),
        ):
            if value is not None:
                member[key] = value
        out[path] = member
    return out
