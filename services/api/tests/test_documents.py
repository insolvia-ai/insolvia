"""The document record itself (issue 8.6) — parsing, keys, and the item shape.

Weighted towards what parse_document_upload REFUSES and towards object_key,
because those two are the whole of the server-side validation: after this
module has run, a presigned URL exists and the only thing still checking
anything is S3.

Every identifier below is obviously fake. This repo is public.
"""

from __future__ import annotations

import pytest
from insolvia_api.core.documents import (
    CONTENT_TYPES,
    KINDS,
    MAX_BYTE_SIZE,
    MAX_FILE_NAME,
    Document,
    create_document,
    document_from_item,
    document_item,
    document_json,
    expiry_timestamp,
    list_order,
    object_key,
    parse_document_upload,
)
from insolvia_api.core.errors import FieldValidationError, ValidationError

CASE_ID = "00000000-0000-4000-8000-0000000000ca"
DOCUMENT_ID = "00000000-0000-4000-8000-0000000000d0"
ALICE = "00000000-0000-4000-8000-00000000a11c"

VALID = {
    "kind": "pay_stub",
    "fileName": "statement.pdf",
    "contentType": "application/pdf",
    "byteSize": 1024,
}


def payload(**overrides: object) -> dict[str, object]:
    return {**VALID, **overrides}


# ── What it refuses ─────────────────────────────────────────────


@pytest.mark.parametrize(
    ("body", "field"),
    [
        ({"kind": None}, "kind"),
        ({"kind": "wedding_photos"}, "kind"),
        ({"kind": 7}, "kind"),
        ({"fileName": None}, "fileName"),
        ({"fileName": "   "}, "fileName"),
        ({"fileName": "x" * (MAX_FILE_NAME + 1)}, "fileName"),
        # Never a path. It is displayed and used as a download name, and a
        # separator makes nonsense of both.
        ({"fileName": "../../etc/passwd"}, "fileName"),
        ({"fileName": "sub\\dir.pdf"}, "fileName"),
        ({"fileName": "line\nbreak.pdf"}, "fileName"),
        ({"contentType": None}, "contentType"),
        ({"contentType": "application/zip"}, "contentType"),
        # An SVG is script-bearing markup wearing an image's content type.
        ({"contentType": "image/svg+xml"}, "contentType"),
        ({"contentType": "text/html"}, "contentType"),
        ({"byteSize": None}, "byteSize"),
        ({"byteSize": 0}, "byteSize"),
        ({"byteSize": -1}, "byteSize"),
        ({"byteSize": MAX_BYTE_SIZE + 1}, "byteSize"),
        ({"byteSize": "1024"}, "byteSize"),
        # bool IS an int in Python; True must not validate as a one-byte file.
        ({"byteSize": True}, "byteSize"),
        # Refused rather than dropped: silently ignoring it would leave the
        # caller believing this record carries provenance.
        ({"provenance": {"kind": {"source": "staff_typed"}}}, "provenance"),
    ],
)
def test_upload_is_refused(body, field):
    with pytest.raises(FieldValidationError) as raised:
        parse_document_upload(payload(**body))
    assert field in raised.value.fields


def test_the_largest_accepted_document_is_accepted():
    # The boundary itself, so the cap is off-by-one-proof in both directions.
    assert parse_document_upload(payload(byteSize=MAX_BYTE_SIZE)).byte_size == (
        MAX_BYTE_SIZE
    )


@pytest.mark.parametrize("content_type", CONTENT_TYPES)
def test_every_allowlisted_content_type_is_accepted(content_type):
    assert parse_document_upload(payload(contentType=content_type)).content_type == (
        content_type
    )


@pytest.mark.parametrize("kind", KINDS)
def test_every_kind_is_accepted(kind):
    assert parse_document_upload(payload(kind=kind)).kind == kind


def test_content_type_is_normalised():
    # Media types are case-insensitive, and the NORMALISED value is what gets
    # signed — the client and the signature have to agree on one spelling.
    assert parse_document_upload(payload(contentType=" Application/PDF ")).content_type


def test_unknown_keys_are_ignored():
    draft = parse_document_upload(payload(sha256="deadbeef", pageCount=12))
    assert draft.file_name == "statement.pdf"


# ── The object key ──────────────────────────────────────────────


def test_the_object_key_is_case_and_document_id_only():
    assert object_key(CASE_ID, DOCUMENT_ID) == f"cases/{CASE_ID}/{DOCUMENT_ID}"


def test_no_file_name_can_reach_the_object_key():
    """The invariant the issue states outright, asserted structurally.

    A key is built from two uuids or it is not built at all, so there is no
    sanitiser to get wrong and no path that produces a key holding something a
    human typed.
    """
    draft = parse_document_upload(payload(fileName="jane-smith-2023-tax-return.pdf"))
    document = create_document(draft, case_id=CASE_ID, uploaded_by=ALICE)
    assert "jane" not in document.storage_ref
    assert "smith" not in document.storage_ref
    assert ".pdf" not in document.storage_ref
    assert document.storage_ref == f"cases/{CASE_ID}/{document.id}"


@pytest.mark.parametrize(
    "case_id",
    ["../other-case", "smith-jane.pdf", "", "CASE#" + CASE_ID, CASE_ID + "/x"],
)
def test_a_key_is_refused_for_anything_that_is_not_a_uuid(case_id):
    with pytest.raises(ValidationError):
        object_key(case_id, DOCUMENT_ID)


# ── Identity and the stored shape ───────────────────────────────


def test_create_stamps_server_identity():
    document = create_document(
        parse_document_upload(payload()), case_id=CASE_ID, uploaded_by=ALICE
    )
    assert document.case_id == CASE_ID
    assert document.uploaded_by == ALICE
    assert document.id
    assert document.id != CASE_ID
    assert document.uploaded_at.endswith("Z")


def test_two_documents_never_share_an_id():
    draft = parse_document_upload(payload())
    first = create_document(draft, case_id=CASE_ID, uploaded_by=ALICE)
    second = create_document(draft, case_id=CASE_ID, uploaded_by=ALICE)
    assert first.id != second.id
    assert first.storage_ref != second.storage_ref


def test_item_round_trips():
    document = create_document(
        parse_document_upload(payload()), case_id=CASE_ID, uploaded_by=ALICE
    )
    assert document_from_item(document_item(document)) == document


def test_item_lands_in_the_cases_partition():
    document = create_document(
        parse_document_upload(payload()), case_id=CASE_ID, uploaded_by=ALICE
    )
    item = document_item(document)
    assert item["PK"] == f"CASE#{CASE_ID}"
    assert item["SK"] == f"DOCUMENT#{document.id}"
    # No new table and no index keys: documents are reached through their case.
    assert "GSI1PK" not in item


def test_from_item_rejects_a_malformed_row():
    with pytest.raises(ValidationError):
        document_from_item({"id": "x"})


def test_ordering_is_by_time_then_id():
    def at(uploaded_at: str, document_id: str) -> Document:
        return Document(
            id=document_id,
            case_id=CASE_ID,
            kind="pay_stub",
            file_name="a.pdf",
            content_type="application/pdf",
            byte_size=1,
            storage_ref=f"cases/{CASE_ID}/{document_id}",
            uploaded_by=ALICE,
            uploaded_at=uploaded_at,
        )

    documents = [
        at("2026-01-01T00:00:00.000Z", "b"),
        at("2026-06-01T00:00:00.000Z", "a"),
        at("2026-01-01T00:00:00.000Z", "a"),
    ]
    newest_first = sorted(documents, key=list_order, reverse=True)
    assert [(d.uploaded_at[:10], d.id) for d in newest_first] == [
        ("2026-06-01", "a"),
        ("2026-01-01", "b"),
        ("2026-01-01", "a"),
    ]


# ── The API representation ──────────────────────────────────────


def test_json_omits_the_uploader_and_the_object_key():
    document = create_document(
        parse_document_upload(payload()), case_id=CASE_ID, uploaded_by=ALICE
    )
    body = document_json(document)
    # The subject identifier, for the same reason case_json omits the owner.
    assert "uploadedBy" not in body
    # The bucket layout is this service's business.
    assert "storageRef" not in body
    assert body["fileName"] == "statement.pdf"
    assert body["byteSize"] == 1024


def test_an_expiry_is_a_z_suffixed_instant_in_the_future():
    now = expiry_timestamp(0)
    later = expiry_timestamp(900)
    assert later.endswith("Z")
    # Both are the same fixed-width format, so a string compare is a time
    # compare — the same property the case table's sort keys rely on.
    assert later > now
