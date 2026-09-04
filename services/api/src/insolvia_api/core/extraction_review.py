"""Extraction review — the human confirmation that turns candidates into
case data (issue #89 / 8.9).

The safeguard that makes the AI posture honest: NOTHING a machine proposed
enters the case until a person accepts it, and this module is the one place
that acceptance is built. It serves BOTH candidate streams — extraction's
(8.7/8.8, origin channel `extraction`) and the MCP surface's agent proposals
(origin channel `mcp`) — through one queue, one status vocabulary, one
confirmation act, exactly as docs/reference/case-data-model.md specifies.

What acceptance IS: the reviewed payload (the human's corrected version when
they changed anything) goes through the SAME `parse_entity` every staff-typed
write uses, carrying provenance this module mints — machine source
(`ai_extracted` for extraction, `imported` for MCP; the data model's rule
that machine-supplied is machine-supplied), the confirming human and moment
on every entry, and the source pointers (document, page locator, confidence,
the candidate id as `extraction_id`). A field the human CORRECTED is
`staff_typed` — they authored that value, and provenance must say so. The
store's invariants then hold by construction: every populated field carries
an entry, and every machine-source entry is confirmed.

WHY THE CANDIDATE IS RESOLVED BEFORE THE RECORD IS WRITTEN: two reviewers
racing to accept one candidate must produce one record, and the store's
compare-and-swap on the candidate's status is the only arbiter there is. The
loser's CAS fails and they get a conflict; only the winner reaches the
entity write. The reverse order would let both write a record and then fight
over the candidate row — a duplicate schedule line the queue can no longer
explain.

CANDIDATE-ID INDIRECTION (core/extraction.py's link rule) resolves here: a
claim whose `creditor_id` names a sibling candidate is rewritten to that
candidate's accepted record id at acceptance — and refuses, with a message
telling the reviewer to confirm the creditor first, while the reference is
still unreviewed. A reference that names no candidate passes through
untouched; it may be a real record id, and dangling references are the
completeness gate's business, as everywhere else.

DELIBERATELY ABSENT: a bulk-accept. The issue's own warning — "bulk-accept
must not become blind-accept" — is enforced structurally: the API reviews
one candidate per call, so however cheap the UI makes the gesture, each
acceptance is its own request, its own CAS, its own audit row.

Pure: no Flask, no boto3; the route composes the stores.
"""

from __future__ import annotations

import re
from collections.abc import Mapping
from dataclasses import dataclass, replace
from typing import TYPE_CHECKING, Final

from insolvia_core.candidates import (
    PENDING,
    Candidate,
)
from insolvia_core.candidates import (
    STATUSES as CANDIDATE_STATUSES,
)
from insolvia_core.case_collections import COLLECTIONS
from insolvia_core.case_entities import EntityDraft, entity_body, parse_entity
from insolvia_core.errors import FieldValidationError, ValidationError
from insolvia_core.fields import timestamp
from insolvia_core.provenance import populated_paths

if TYPE_CHECKING:
    from typing import Any

    from insolvia_core.case_entities import EntityKind

# What a reviewer may do with one pending candidate. Withdrawal is NOT here:
# it is the PROPOSER'S act, owned by the MCP surface.
REVIEW_ACTIONS: Final = ("accept", "reject")

# The provenance source each origin channel confirms into — the data model's
# vocabulary, followed exactly: extraction output is `ai_extracted`, an
# agent's PMS-sourced proposal is `imported`, and both are machine sources
# subject to the same confirmation rule.
SOURCE_FOR_CHANNEL: Final = {"extraction": "ai_extracted", "mcp": "imported"}

# Which reference field may carry candidate-id indirection, per entity type
# (core/extraction.py writes these; nothing else does).
LINK_FIELDS: Final = {"claims": "creditor_id", "pay_period_records": "employment_id"}

# A dotted provenance path, split into (name, optional [id]) segments — the
# grammar core/provenance.py owns; this walks values by it.
_SEGMENT_RE: Final = re.compile(r"([a-z][a-z0-9_]*)(?:\[([A-Za-z0-9_-]+)\])?")


@dataclass(frozen=True)
class ReviewDecision:
    """One validated review request."""

    action: str
    corrected_payload: Mapping[str, object] | None = None


def parse_review(payload: Mapping[str, object]) -> ReviewDecision:
    """Validate POST .../review. Unknown keys are ignored.

    `correctedPayload` — the record as the human fixed it — rides only on an
    accept: "reject, but here is what it should have said" is just typing
    the record through intake, where staff-typed writes already live.
    """
    action = payload.get("action")
    if not isinstance(action, str) or action not in REVIEW_ACTIONS:
        raise FieldValidationError(
            {"action": "Action must be one of " + ", ".join(REVIEW_ACTIONS) + "."}
        )
    corrected = payload.get("correctedPayload")
    if corrected is not None:
        if action != "accept":
            raise FieldValidationError(
                {"correctedPayload": "Corrections ride on an accept."}
            )
        if not isinstance(corrected, Mapping):
            raise FieldValidationError(
                {"correctedPayload": "correctedPayload must be an object."}
            )
    return ReviewDecision(action=action, corrected_payload=corrected)


def parse_status_filter(value: object) -> str | None:
    if value is None:
        return None
    if not isinstance(value, str) or value not in CANDIDATE_STATUSES:
        raise ValidationError("status must be one of " + ", ".join(CANDIDATE_STATUSES))
    return value


def reviewable_kind(candidate: Candidate) -> EntityKind[Any]:
    """The entity kind an accept would write, or a refusal.

    `debtors` proposals (an MCP-only shape — extraction never writes one)
    are not acceptable in-app yet: a debtor is keyed by filing role and
    written through its own store, and wiring that path is real work this
    review deliberately does not fake. The refusal names the workaround.
    """
    if candidate.entity_type == "debtors":
        raise ValidationError(
            "debtor proposals cannot be accepted in-app yet — enter the"
            " debtor through intake and reject this candidate"
        )
    kind = COLLECTIONS.get(candidate.entity_type)
    if kind is None:
        raise ValidationError(
            f"candidates of type {candidate.entity_type!r} are not reviewable"
        )
    return kind


def resolve_candidate_references(
    entity_type: str,
    payload: Mapping[str, object],
    *,
    siblings: Mapping[str, Candidate],
) -> Mapping[str, object]:
    """Rewrite candidate-id indirection to the accepted record's id.

    Only the one known link field per entity type is inspected. A value that
    names no sibling candidate passes through untouched (it may be a real
    record id); one that names a sibling still awaiting review refuses with
    the instruction that makes the queue self-explaining.
    """
    field = LINK_FIELDS.get(entity_type)
    if field is None:
        return payload
    value = payload.get(field)
    if not isinstance(value, str):
        return payload
    referenced = siblings.get(value)
    if referenced is None:
        return payload
    if referenced.resulting_record_id is None:
        raise FieldValidationError(
            {
                field: "This record references another extracted candidate"
                f" ({referenced.entity_type}) that has not been accepted yet"
                " — review that one first."
            }
        )
    return {**payload, field: referenced.resulting_record_id}


def _value_at(record: object, path: str) -> object:
    """The value a provenance path addresses, or None — the same grammar
    populated_paths emits, walked in reverse."""
    current = record
    for name, element_id in _SEGMENT_RE.findall(path):
        if isinstance(current, Mapping):
            current = current.get(name)
        else:
            return None
        if element_id:
            if not isinstance(current, list | tuple):
                return None
            current = next(
                (
                    element
                    for element in current
                    if isinstance(element, Mapping) and element.get("id") == element_id
                ),
                None,
            )
    return current


def build_accepted_draft(
    candidate: Candidate,
    payload: Mapping[str, object],
    *,
    confirmed_by: str,
    confirmed_at: str,
) -> EntityDraft[Any]:
    """The record acceptance writes: the reviewed payload plus minted
    provenance, validated through the same parse every write path uses.

    `payload` is the final, reference-resolved body. Which source each field
    gets is decided against the CANDIDATE'S original payload: a value the
    human changed (or added) is `staff_typed` — they authored it — and an
    unchanged one carries the machine source with the full audit trail
    (document, locator, confidence, the candidate id as extraction_id).
    """
    kind = reviewable_kind(candidate)
    machine_source = SOURCE_FOR_CHANNEL.get(candidate.origin.channel)
    if machine_source is None:
        raise ValidationError(
            f"candidates from channel {candidate.origin.channel!r} are not reviewable"
        )

    # Parse once without provenance to learn the populated paths of the
    # record AS IT WILL BE STORED (the field parsers collapse blanks).
    body = entity_body(parse_entity(kind, payload, enforce_provenance=False))
    # The original through the SAME parser, so canonicalisation ("1200.5" vs
    # "1200.50") never reads as a human correction. A stored payload the
    # current parser refuses (a rule tightened since it was written) falls
    # back to the raw mapping — the comparison degrades to "everything looks
    # corrected", which errs toward staff_typed, the safe direction.
    try:
        original: Mapping[str, object] = entity_body(
            parse_entity(kind, candidate.payload, enforce_provenance=False)
        )
    except FieldValidationError:
        original = dict(candidate.payload)

    provenance: dict[str, dict[str, object]] = {}
    for path in populated_paths(body):
        unchanged = _value_at(body, path) == _value_at(original, path)
        # The link rewrite is mechanical, not authorship: a reference the
        # RESOLVER changed still carries the machine source.
        link_field = LINK_FIELDS.get(candidate.entity_type)
        if path == link_field:
            unchanged = True
        entry: dict[str, object] = {
            "source": machine_source if unchanged else "staff_typed",
            "confirmed_by": confirmed_by,
            "confirmed_at": confirmed_at,
            "extraction_id": candidate.id,
        }
        if unchanged:
            if candidate.document_id is not None:
                entry["document_id"] = candidate.document_id
            if candidate.locator is not None:
                entry["locator"] = dict(candidate.locator)
            if candidate.confidence is not None:
                entry["confidence"] = candidate.confidence
        provenance[path] = entry

    return parse_entity(kind, {**payload, "provenance": provenance})


def accept(
    candidate: Candidate,
    *,
    corrected_payload: Mapping[str, object] | None,
    resulting_record_id: str,
    confirmed_by: str,
    confirmed_at: str,
) -> Candidate:
    """The reviewed candidate row: `accepted` when taken as proposed,
    `corrected` when the human changed it — retained either way, because
    corrections are the only extraction-quality measurement there is."""
    return replace(
        candidate,
        status="corrected" if corrected_payload is not None else "accepted",
        corrected_payload=corrected_payload,
        resulting_record_id=resulting_record_id,
        confirmed_by=confirmed_by,
        confirmed_at=confirmed_at,
        updated_at=timestamp(),
    )


def reject(candidate: Candidate, *, confirmed_by: str, confirmed_at: str) -> Candidate:
    """The rejected row — retained, same reason corrections are."""
    return replace(
        candidate,
        status="rejected",
        confirmed_by=confirmed_by,
        confirmed_at=confirmed_at,
        updated_at=timestamp(),
    )


def review_moment() -> str:
    """One confirmation instant, minted once per review so the candidate row
    and every provenance entry carry the same value — they record one act."""
    return timestamp()


__all__ = [
    "LINK_FIELDS",
    "PENDING",
    "REVIEW_ACTIONS",
    "SOURCE_FOR_CHANNEL",
    "ReviewDecision",
    "accept",
    "build_accepted_draft",
    "parse_review",
    "parse_status_filter",
    "reject",
    "resolve_candidate_references",
    "review_moment",
    "reviewable_kind",
]
