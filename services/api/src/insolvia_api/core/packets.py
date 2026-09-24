"""Assembled filing packets: the record, and the object key its bytes live
under (issue #96).

A packet is the OUTPUT of the packet-assembly pipeline worker
(core/packet_assembly.py): every form of the Chapter 7 individual set plus
the creditor matrix, rendered deterministically and zipped into one
filed-ready download. This module owns the record; the bytes live in the same
S3 bucket as the case's source documents (infra/modules/case_documents),
under a key of their own.

A packet is deliberately NOT a `document` row (core/documents.py). A document
is source material the client uploaded and extraction reads — its record
tracks an upload the server merely authorised. A packet is the server's own
work product: the worker writes the bytes itself, so there is no pending
state, no presigned PUT, no unconfirmed-upload tag, and no reaper to dodge.
Conflating the two would put generated output on extraction's reading list.

Immutability: a packet record is never updated. Re-assembly produces a NEW
packet (and re-pins the case — effective-dating.md); the old record and its
bytes stay, because a packet an attorney reviewed yesterday must still be the
packet they reviewed. The provenance question "what did this filing use" is
answered by `form_revisions` stored here AND pinned on the case in the same
write (core/ports.PacketStore.create).

Everything here is pure: no Flask, no boto3, no clock beyond datetime.now.
"""

from __future__ import annotations

import re
import uuid
from collections.abc import Mapping
from dataclasses import dataclass, field
from datetime import UTC, datetime
from typing import Final

from insolvia_core.cases import partition_key
from insolvia_core.errors import ValidationError

from .form_overlay import (
    DEFAULT_OUTPUT_OPTIONS,
    SIGNATURE_PAGES_MODES,
    OutputOptions,
    output_options_json,
)

# The download name a browser saves the packet under. One name for every
# packet on purpose: the object key carries the identity (two server-minted
# uuids), and the record's created_at answers "which assembly was this".
PACKET_FILE_NAME: Final = "chapter7-packet.zip"

# What a packet zip is, on the wire and in the download.
PACKET_CONTENT_TYPE: Final = "application/zip"

# Same uuid4 pattern — and the same \Z-not-$ reasoning — as
# core/documents.object_key: a key is built from server-minted uuids only,
# structurally, so no human-typed value can ever reach a key.
_UUID_RE: Final = re.compile(
    r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\Z"
)


@dataclass(frozen=True)
class Packet:
    """One assembled packet of a case — metadata only. The bytes are in the
    bucket.

    `form_revisions` and `constants_set_id` are the same pins written onto
    the case, kept here too because the CASE's copies move on re-assembly
    while this record describes THIS packet forever.
    `sha256` is the digest of the stored zip — assembly
    is deterministic to the byte (core/form_fill.py), so the digest is what
    lets anyone prove a downloaded packet is the one this record describes.
    `created_by` is the firm user whose job accept produced it.
    `options` is the output options (issue 13.11) this packet was rendered
    with — a plain `OutputOptions()` for the ordinary filing set, something
    else for a draft, a signature-pages-only print, or a `/s/` set. Recording
    it here (never re-derived) is what makes a draft distinguishable from a
    filing set after the fact, the issue's own done-when.
    """

    id: str
    case_id: str
    job_id: str
    file_name: str
    content_type: str
    byte_size: int
    sha256: str
    storage_ref: str
    form_revisions: Mapping[str, str]
    constants_set_id: str
    creditor_count: int
    created_by: str
    created_at: str
    options: OutputOptions = field(default_factory=lambda: DEFAULT_OUTPUT_OPTIONS)


def _timestamp() -> str:
    """Millisecond UTC with a literal Z — the document record's format, for
    the document record's reason: nothing sorts on this value in a store."""
    return datetime.now(UTC).isoformat(timespec="milliseconds").replace("+00:00", "Z")


def packet_object_key(case_id: str, packet_id: str) -> str:
    """Where the bytes live: cases/<case_id>/packets/<packet_id>.

    Under the case's own prefix, so every bucket-policy statement written
    against `cases/*` covers packets without an edit — and distinguished from
    document objects by the `packets/` segment, so the two kinds of bytes can
    never collide however ids fall. NO PII IN AN OBJECT KEY, ever, enforced
    structurally exactly as core/documents.object_key does.
    """
    if not _UUID_RE.match(case_id) or not _UUID_RE.match(packet_id):
        raise ValidationError("object keys are built from server-minted uuids only")
    return f"cases/{case_id}/packets/{packet_id}"


def new_packet(
    *,
    case_id: str,
    job_id: str,
    byte_size: int,
    sha256: str,
    form_revisions: Mapping[str, str],
    constants_set_id: str,
    creditor_count: int,
    created_by: str,
    options: OutputOptions = DEFAULT_OUTPUT_OPTIONS,
) -> Packet:
    """Stamp an assembled packet with server-generated identity and its
    storage location. Every argument is a fact the worker just established;
    nothing here comes from a request body — `options` is the ONE exception,
    and even that arrived as a validated job-acceptance field long before
    this worker ran, never read from a request here."""
    packet_id = str(uuid.uuid4())
    return Packet(
        id=packet_id,
        case_id=case_id,
        job_id=job_id,
        file_name=PACKET_FILE_NAME,
        content_type=PACKET_CONTENT_TYPE,
        byte_size=byte_size,
        sha256=sha256,
        # Derived once and stored, not re-derived on read — the document
        # record's argument: the scheme can change without stranding objects.
        storage_ref=packet_object_key(case_id, packet_id),
        form_revisions=dict(form_revisions),
        constants_set_id=constants_set_id,
        creditor_count=creditor_count,
        created_by=created_by,
        created_at=_timestamp(),
        options=options,
    )


def sort_key(packet_id: str) -> str:
    return f"PACKET#{packet_id}"


def list_order(packet: Packet) -> tuple[str, str]:
    """Creation order, tie-broken by id — callers reverse for newest-first.
    The SK embeds a random uuid, so neither store gets ordering for free
    (the note every sibling collection carries)."""
    return (packet.created_at, packet.id)


def packet_item(packet: Packet) -> dict[str, object]:
    """The exact stored item shape, shared by both PacketStore
    implementations.

    PK  CASE#<case_id>      the case's own partition — a packet is a child
    SK  PACKET#<id>         item like DOCUMENT#/JOB#, so there is no second
                            table and the existing table grants cover it.
    """
    return {
        "PK": partition_key(packet.case_id),
        "SK": sort_key(packet.id),
        "id": packet.id,
        "caseId": packet.case_id,
        "jobId": packet.job_id,
        "fileName": packet.file_name,
        "contentType": packet.content_type,
        "byteSize": packet.byte_size,
        "sha256": packet.sha256,
        "storageRef": packet.storage_ref,
        "formRevisions": dict(packet.form_revisions),
        "constantsSetId": packet.constants_set_id,
        "creditorCount": packet.creditor_count,
        "createdBy": packet.created_by,
        "createdAt": packet.created_at,
        "options": output_options_json(packet.options),
    }


def _options_from_item(raw: object) -> OutputOptions:
    """Inverse of `output_options_json`, the stored-item's own tolerant
    style (a malformed shape is a ValueError the caller's try/except already
    catches) rather than `form_overlay.parse_output_options`'s
    request-validation one (FieldValidationError, per-field messages meant
    for a client). Absent entirely — every packet this service wrote before
    issue 13.11 — reads as the plain filing set, exactly what those packets
    always were."""
    if raw is None:
        return OutputOptions()
    if not isinstance(raw, Mapping):
        raise ValueError("options is not a map")
    signature_pages = raw.get("signaturePages", "all")
    if signature_pages not in SIGNATURE_PAGES_MODES:
        raise ValueError(f"options.signaturePages {signature_pages!r} is invalid")
    raw_forms = raw.get("forms")
    forms = (
        tuple(str(f) for f in raw_forms)
        if isinstance(raw_forms, list) and raw_forms
        else None
    )
    return OutputOptions(
        draft_watermark=bool(raw.get("draftWatermark", False)),
        print_date=bool(raw.get("printDate", False)),
        signature_pages=signature_pages,
        sign_electronically=bool(raw.get("signElectronically", False)),
        forms=forms,
    )


def packet_from_item(item: Mapping[str, object]) -> Packet:
    """Inverse of packet_item. Raises ValidationError on a row this service
    did not write — loud beats a half-populated record reaching a caller."""
    try:
        raw_revisions = item["formRevisions"]
        if not isinstance(raw_revisions, Mapping):
            raise ValueError("formRevisions is not a map")
        byte_size = item["byteSize"]
        creditor_count = item["creditorCount"]
        if not isinstance(byte_size, (int, str)) or not isinstance(
            creditor_count, (int, str)
        ):
            raise ValueError("numeric attribute has a non-numeric type")
        return Packet(
            id=str(item["id"]),
            case_id=str(item["caseId"]),
            job_id=str(item["jobId"]),
            file_name=str(item["fileName"]),
            content_type=str(item["contentType"]),
            byte_size=int(byte_size),
            sha256=str(item["sha256"]),
            storage_ref=str(item["storageRef"]),
            form_revisions={
                str(series): str(pin) for series, pin in raw_revisions.items()
            },
            constants_set_id=str(item["constantsSetId"]),
            creditor_count=int(creditor_count),
            created_by=str(item["createdBy"]),
            created_at=str(item["createdAt"]),
            options=_options_from_item(item.get("options")),
        )
    except (KeyError, ValueError) as error:
        raise ValidationError(f"stored packet item is malformed: {error}") from error


def packet_json(packet: Packet) -> dict[str, object]:
    """The API representation.

    `storageRef` is absent for the document record's reason — the object key
    is this service's business, and a client can only depend on a layout it
    can see. `createdBy` is present so the firm directory resolves it to a
    name; `sha256` is present because it is the one thing a reviewer can
    check a downloaded file against. `options` is issue 13.11's own
    done-when: a client tells a draft from a filing set after the fact by
    reading this, never by re-deriving it from the bytes.
    """
    return {
        "id": packet.id,
        "caseId": packet.case_id,
        "jobId": packet.job_id,
        "fileName": packet.file_name,
        "contentType": packet.content_type,
        "byteSize": packet.byte_size,
        "sha256": packet.sha256,
        "formRevisions": dict(packet.form_revisions),
        "constantsSetId": packet.constants_set_id,
        "creditorCount": packet.creditor_count,
        "createdBy": packet.created_by,
        "createdAt": packet.created_at,
        "options": output_options_json(packet.options),
    }
