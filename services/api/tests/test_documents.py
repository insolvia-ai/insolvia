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
    STATUS_PENDING,
    STATUS_STORED,
    Document,
    StoredBlob,
    confirm_document,
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
        # ── Names that lie about themselves ─────────────────────
        # A control-character class does not see any of these. Each is
        # accepted by every parser and misread by every human. Written as
        # escapes rather than as the characters themselves for exactly the
        # reason they are refused: pasted literally they are invisible, and a
        # test nobody can read is a test nobody can check.
        #
        # U+202E, the right-to-left override. This is `invoice<RLO>fdp.exe`,
        # and it RENDERS as `invoiceexe.pdf` — in the document list, and as
        # the name a browser saves it under. The bytes say .exe and the screen
        # says .pdf.
        ({"fileName": "invoice\u202efdp.exe"}, "fileName"),
        ({"fileName": "report\u202dsomething.pdf"}, "fileName"),
        # The bidi ISOLATES are a separate block doing the same job, so a
        # check that stopped at U+202E would miss them.
        ({"fileName": "invoice\u2067fdp.exe"}, "fileName"),
        # Zero-width characters: invisible, so two names can look identical
        # and differ, and a name can carry padding nobody can select.
        ({"fileName": "state\u200bment.pdf"}, "fileName"),
        ({"fileName": "state\u200dment.pdf"}, "fileName"),
        ({"fileName": "\ufeffstatement.pdf"}, "fileName"),
        ({"fileName": "soft\u00adhyphen.pdf"}, "fileName"),
        # Not characters, so no character class can catch them, and they are
        # the two names a path-shaped check is actually for: a browser
        # resolves either against the download directory rather than saving a
        # file. `..` survived the separator check because it contains no
        # separator.
        ({"fileName": "."}, "fileName"),
        ({"fileName": ".."}, "fileName"),
        # Whitespace is stripped before the check, so a padded one must fail
        # for the same reason rather than sneaking through.
        ({"fileName": "  ..  "}, "fileName"),
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
    [
        "../other-case",
        "smith-jane.pdf",
        "",
        "CASE#" + CASE_ID,
        CASE_ID + "/x",
        # A TRAILING NEWLINE, which the `$`-anchored pattern accepted.
        #
        # In Python `$` matches at the end of the string OR immediately before
        # a final newline, so `"<uuid>\n"` passed a check whose entire purpose
        # is that a key contains nothing but two server-minted uuids — and
        # object_key would have returned `cases/<uuid>\n/<uuid>`. `\Z` is the
        # anchor that means what this pattern meant all along.
        CASE_ID + "\n",
        CASE_ID + "\r\n",
    ],
)
def test_a_key_is_refused_for_anything_that_is_not_a_uuid(case_id):
    with pytest.raises(ValidationError):
        object_key(case_id, DOCUMENT_ID)


@pytest.mark.parametrize("suffix", ["\n", "\r\n", "\n\n"])
def test_neither_half_of_a_key_may_end_in_a_newline(suffix):
    """Both halves, because the anchor bug was in one pattern used twice and a
    test covering only the case id would have left the document id open."""
    with pytest.raises(ValidationError):
        object_key(CASE_ID, DOCUMENT_ID + suffix)


def test_no_accepted_key_can_contain_a_newline():
    """The property the docstring claims, asserted rather than inferred from
    the pattern — a key with a newline in it breaks every log line, inventory
    row and CloudTrail event that key ever appears in."""
    assert "\n" not in object_key(CASE_ID, DOCUMENT_ID)


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


# ── Pending and stored ──────────────────────────────────────────


def test_a_new_record_is_pending_and_has_no_etag():
    """Because nothing has looked at the object. The row records that an
    upload was AUTHORISED, and until confirmation that is all it records."""
    document = create_document(
        parse_document_upload(payload()), case_id=CASE_ID, uploaded_by=ALICE
    )
    assert document.status == STATUS_PENDING
    assert document.etag is None


def test_confirming_replaces_the_claimed_size_with_the_real_one():
    """The declared size is what the client asked to be allowed to send; this
    is what arrived. Keeping the claim would leave the record asserting a
    number nothing ever checked."""
    document = create_document(
        parse_document_upload(payload(byteSize=2048)),
        case_id=CASE_ID,
        uploaded_by=ALICE,
    )
    confirmed = confirm_document(document, StoredBlob(byte_size=1999, etag="abc"))
    assert (confirmed.status, confirmed.byte_size, confirmed.etag) == (
        STATUS_STORED,
        1999,
        "abc",
    )
    # Everything the server stamped at create time is untouched.
    assert confirmed.id == document.id
    assert confirmed.storage_ref == document.storage_ref
    assert confirmed.uploaded_at == document.uploaded_at


def test_confirming_twice_produces_the_same_record():
    """The route is idempotent for a client that retries a lost response, and
    it can only be if this is."""
    document = create_document(
        parse_document_upload(payload()), case_id=CASE_ID, uploaded_by=ALICE
    )
    blob = StoredBlob(byte_size=1999, etag="abc")
    assert confirm_document(confirm_document(document, blob), blob) == confirm_document(
        document, blob
    )


def test_a_pending_item_carries_no_etag_attribute():
    """Absent rather than empty — "nothing has looked at this object" is what
    a missing attribute already means."""
    document = create_document(
        parse_document_upload(payload()), case_id=CASE_ID, uploaded_by=ALICE
    )
    assert "etag" not in document_item(document)
    assert document_item(document)["status"] == STATUS_PENDING


def test_a_confirmed_item_round_trips():
    document = confirm_document(
        create_document(
            parse_document_upload(payload()), case_id=CASE_ID, uploaded_by=ALICE
        ),
        StoredBlob(byte_size=7, etag="cafe"),
    )
    assert document_from_item(document_item(document)) == document


def test_a_row_written_before_confirmation_existed_reads_as_pending():
    """A dev table has rows from the version of this service that could not
    confirm anything. `pending` is not a fallback for them, it is the true
    reading: nothing ever checked that their bytes arrived."""
    document = create_document(
        parse_document_upload(payload()), case_id=CASE_ID, uploaded_by=ALICE
    )
    older = {k: v for k, v in document_item(document).items() if k != "status"}
    assert document_from_item(older).status == STATUS_PENDING


def test_json_exposes_the_status_and_not_the_etag():
    """`status` is the one field a client must branch on — it is the
    difference between a file it can open and a file that is not there.
    `etag` joins storageRef as the storage layer's business."""
    document = confirm_document(
        create_document(
            parse_document_upload(payload()), case_id=CASE_ID, uploaded_by=ALICE
        ),
        StoredBlob(byte_size=7, etag="cafe"),
    )
    body = document_json(document)
    assert body["status"] == STATUS_STORED
    assert "etag" not in body


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
