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
from dataclasses import dataclass
from datetime import UTC, datetime
from typing import Final

from insolvia_core.errors import ValidationError

from insolvia_api.core.cases import partition_key

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

    `form_revisions` is the same pin map written onto the case, kept here too
    because the CASE's copy moves on re-assembly while this record describes
    THIS packet forever. `sha256` is the digest of the stored zip — assembly
    is deterministic to the byte (core/form_fill.py), so the digest is what
    lets anyone prove a downloaded packet is the one this record describes.
    `created_by` is the firm user whose job accept produced it.
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
    creditor_count: int
    created_by: str
    created_at: str


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
    creditor_count: int,
    created_by: str,
) -> Packet:
    """Stamp an assembled packet with server-generated identity and its
    storage location. Every argument is a fact the worker just established;
    nothing here comes from a request body."""
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
        creditor_count=creditor_count,
        created_by=created_by,
        created_at=_timestamp(),
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
        "creditorCount": packet.creditor_count,
        "createdBy": packet.created_by,
        "createdAt": packet.created_at,
    }


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
            creditor_count=int(creditor_count),
            created_by=str(item["createdBy"]),
            created_at=str(item["createdAt"]),
        )
    except (KeyError, ValueError) as error:
        raise ValidationError(f"stored packet item is malformed: {error}") from error


def packet_json(packet: Packet) -> dict[str, object]:
    """The API representation.

    `storageRef` is absent for the document record's reason — the object key
    is this service's business, and a client can only depend on a layout it
    can see. `createdBy` is present so the firm directory resolves it to a
    name; `sha256` is present because it is the one thing a reviewer can
    check a downloaded file against.
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
        "creditorCount": packet.creditor_count,
        "createdBy": packet.created_by,
        "createdAt": packet.created_at,
    }
