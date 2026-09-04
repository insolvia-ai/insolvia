"""Case documents: the metadata record, and the object key its bytes live under.

The `document` entity of docs/reference/case-data-model.md — the source
material extraction (8.7/8.8) reads, and the most sensitive bytes this system
holds. This module owns the record; the bytes live in the S3 bucket
infra/modules/case_documents creates, and how access to them is brokered is
api/routes/documents.py's docstring.

Everything here is pure: no Flask, no boto3. The item shape lives here rather
than in an adapter so the DynamoDB and in-memory stores cannot drift apart,
exactly as core/cases.py does for the case root.

WHY THERE IS NO PROVENANCE ON THIS RECORD, since every other case-scoped entity
carries one and the omission would otherwise look like an oversight.

Provenance answers "who asserted this value, and has a human confirmed it",
and it exists to keep unconfirmed machine output from entering a case as fact.
A document record makes no assertion about the debtor's affairs. Its fields are
facts about a transfer this server itself observed and stamped — the id, the
case, the moment the upload was authorised, the object key, the declared size
and content type it validated. The data model's own provenance rules skip
server-owned paths for exactly that reason, and here that is nearly the whole
record.

The two values the caller supplies — `kind` and `file_name` — are the
uploader's own claim about a file they are handing us, and `uploaded_by`
already records who made it. A per-field provenance map would be a second,
weaker copy of that one fact.

Where provenance genuinely applies is the values extracted FROM a document, and
the model already routes those through `extraction_candidate` and provenance's
own `document_id` + `locator` fields. A document is provenance's OBJECT, not
its subject; giving it a provenance map would point the relationship at itself.
"""

from __future__ import annotations

import re
import uuid
from collections.abc import Mapping
from dataclasses import dataclass, replace
from datetime import UTC, datetime, timedelta
from typing import Final

from insolvia_core.cases import partition_key
from insolvia_core.errors import FieldValidationError, ValidationError

# What the uploader says this is. Required rather than defaulted, because
# extraction dispatches on it: a record whose kind is unknown is one somebody
# has to open the document to classify, which is the work this field exists to
# avoid. A caller that genuinely does not know sends "other" — an explicit
# admission beats a silent default that looks like an answer.
#
# It is a CLAIM, not a verified fact. Nothing here reads the bytes, so a PDF
# labelled `pay_stub` may be anything at all; extraction is what finds out.
KINDS: Final = (
    "credit_report",
    "pay_stub",
    "bank_statement",
    "tax_return",
    "identification",
    "court_notice",
    "other",
)

# The allowlist, and it is an allowlist because the alternative — a denylist of
# dangerous types — is a list somebody has to keep ahead of attackers forever.
#
# Five types, one per way a debtor's paperwork actually reaches us: a statement
# downloaded from a bank (PDF), a phone photo (JPEG, or HEIC on any recent
# iPhone), and a flatbed or fax-era scan (PNG, TIFF).
#
# Deliberately absent, each for a reason rather than for tidiness:
#   - Office formats and ZIP: macro and archive carriers, and nothing
#     downstream reads them. A client with a .docx can print it to PDF.
#   - image/svg+xml: an SVG is script-bearing markup wearing an image's
#     content type. It would be served back through a presigned GET and
#     rendered by a browser, and no part of this feature needs vectors.
#   - text/* and text/html: same rendering problem, no document value.
CONTENT_TYPES: Final = (
    "application/pdf",
    "image/jpeg",
    "image/png",
    "image/heic",
    "image/tiff",
)

# 50 MiB, checked before an upload URL is minted and then bound into the
# signature (adapters/aws/document_blobs.py), so it is a limit rather than a
# request.
#
# The number is sized off the largest thing a real case produces: a scanned
# credit report can run to a hundred pages, and at 300 dpi colour that lands in
# the tens of megabytes. Phone photos are 3-8 MB and bank statement PDFs are a
# few hundred KB, so this is roughly an order of magnitude of headroom over the
# common case and still small enough that one abusive account cannot fill a
# bucket in an afternoon.
#
# It is also comfortably under S3's 5 GiB single-PUT ceiling, which matters:
# the capability we hand out is exactly one PUT, and a document large enough to
# need a multipart upload would need a different capability entirely.
MAX_BYTE_SIZE: Final = 50 * 1024 * 1024

# Long enough for anything a file system will produce, short enough that the
# field cannot be used as free storage.
MAX_FILE_NAME: Final = 255

# THE TAG EVERY PRESIGNED PUT CARRIES, and the only handle anything has on
# bytes this service can no longer name.
#
# A presigned PUT is valid for its whole window and its payload is not signed,
# so the URL can be replayed — and it outlives the record that authorised it.
# POST a document, DELETE it, then PUT through the still-valid URL, and the
# object lands under a key no row names: `list_for_case` reads the case store
# so it never appears, a second DELETE has nothing to delete, and the API holds
# no s3:ListBucket, so nothing in this system can even find it. Replaying the
# PUT parks versions with the same property.
#
# So the capability itself tags what it writes, and the bucket's
# `expire-unconfirmed-uploads` lifecycle rule filters on exactly this pair
# (infra/modules/case_documents/main.tf). It lives in `core` rather than in the
# adapter because two layers need it and they may not import each other: the
# adapter binds it into the signature, and the route has to tell the client
# which header to send. A drifted copy would be an upload S3 refuses, or worse,
# one it accepts and nothing ever reaps.
#
# READ THE LIFECYCLE RULE'S COMMENT BEFORE THIS SHIPS. Nothing clears the tag
# yet — 8.6 has no upload-completion callback (api/routes/documents.py names
# that gap) — so today a confirmed document wears it exactly as an orphan does.
UPLOAD_TAG_KEY: Final = "upload"
UPLOAD_TAG_VALUE: Final = "unconfirmed"

# WHAT A DOCUMENT ROW MEANS, which was the ambiguity the tag exposed.
#
#   pending  the server authorised an upload and minted a capability. Nothing
#            here knows whether the bytes ever landed. `byte_size` is the
#            client's DECLARED size and there is no etag.
#   stored   the server did a HeadObject and saw the object. `byte_size` and
#            `etag` are what S3 reports, and the object no longer carries the
#            unconfirmed tag, so the bucket's reaper will not touch it.
#
# Both are listed. A pending row is not a half-written record to be hidden —
# it is the case's own account of an upload that did not finish, and a UI that
# dropped it would leave the user with a document they cannot see and cannot
# retry. That is why this is a status on the record rather than a filter in
# the store.
STATUS_PENDING: Final = "pending"
STATUS_STORED: Final = "stored"
STATUSES: Final = (STATUS_PENDING, STATUS_STORED)


@dataclass(frozen=True)
class StoredBlob:
    """What the object store reports about an object that exists.

    The FACTS, as against the claims on a pending record. `byte_size` is what
    S3 counted, not what the client said it would send, and `etag` identifies
    the bytes it counted them from.
    """

    byte_size: int
    etag: str


# The `x-amz-tagging` header value: a URL-encoded query string, which is the
# wire format S3 defines for tags on a PUT. Neither half contains a character
# that needs escaping, and the test asserts that rather than trusting it.
UPLOAD_TAG: Final = f"{UPLOAD_TAG_KEY}={UPLOAD_TAG_VALUE}"

# What may appear in an object key, and the reason it is a UUID pattern rather
# than a sanitiser. See object_key.
#
# `\Z` AND NOT `$`, WHICH IS THE WHOLE POINT OF THE PATTERN. In Python `$`
# matches at the end of the string OR immediately before a trailing newline, so
# `"<uuid>\n"` satisfies a `$`-anchored match — and object_key would then build
# a key with a newline in it, contradicting the docstring three functions down
# that says a key holds nothing but two server-minted uuids. `\Z` matches only
# at the true end of the string.
_UUID_RE: Final = re.compile(
    r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\Z"
)

# A file name is displayed and used as a download name, never as a path and
# never as a key. Separators and control characters are still refused: they
# make a nonsense of both uses, and refusing them here costs nothing.
#
# The three additions past the obvious set are each a rendering attack rather
# than a parsing one, which is why a control-character class alone missed them:
#
#   U+202A-U+202E, U+2066-U+2069  the bidirectional overrides and isolates.
#     `invoice‮fdp.exe` RENDERS as `invoiceexe.pdf` — in the document
#     list, and in whatever name the browser saves it under. The bytes say one
#     thing and every human who looks says another, which is the definition of
#     a name we must not accept.
#   U+200B-U+200F, U+2060, U+FEFF  zero-width space/joiner/non-joiner, the
#     left-to-right and right-to-left marks, word joiner, BOM. Invisible, so
#     two visually identical names can differ, and a name can carry padding
#     nobody can see or select.
#   U+00AD  soft hyphen — invisible in most renderers, same problem.
#
# Written as escapes rather than as the characters themselves for the reason
# the characters exist: pasted literally, this class would be an unreadable
# blank stretch in the middle of a security check.
_FILE_NAME_FORBIDDEN: Final = re.compile(
    r"[/\\"
    r"\x00-\x1f\x7f"  # C0 controls and DEL
    r"\u00ad"  # soft hyphen
    r"\u200b-\u200f"  # zero-width space/joiner/non-joiner, LRM, RLM
    r"\u202a-\u202e"  # bidi embeddings and overrides
    r"\u2060"  # word joiner
    r"\u2066-\u2069"  # bidi isolates
    r"\ufeff"  # BOM / zero-width no-break space
    r"]"
)

# `.` and `..` are not characters, so the class above cannot see them, and they
# are the two names a path-shaped check is actually for. Neither is a file
# name: as a download name a browser resolves them against the download
# directory, and as a displayed name they are meaningless. Refused whole rather
# than by character, since every character in them is legitimate on its own.
_FILE_NAME_RESERVED: Final = frozenset({".", ".."})


@dataclass(frozen=True)
class Document:
    """One document of a case — metadata only. The bytes are in the bucket."""

    id: str
    case_id: str
    kind: str
    file_name: str
    content_type: str
    byte_size: int
    storage_ref: str
    uploaded_by: str
    uploaded_at: str
    # Defaulted, and both defaults describe a row that has just been created:
    # an authorised upload nobody has confirmed, whose size is still the
    # client's own claim. A record only leaves this state by going through
    # `confirm_document`, which is the one place that has seen the object.
    status: str = STATUS_PENDING
    etag: str | None = None


@dataclass(frozen=True)
class DocumentDraft:
    """A validated upload request, before server-generated identity."""

    kind: str
    file_name: str
    content_type: str
    byte_size: int


def _timestamp() -> str:
    """Millisecond-precision UTC with a literal Z.

    Milliseconds rather than core/cases.py's microseconds: nothing sorts on
    this value in a store — documents are ordered in the service (see
    `list_order`) because the sort key is a random uuid — so the extra
    precision would buy only a longer string.
    """
    return datetime.now(UTC).isoformat(timespec="milliseconds").replace("+00:00", "Z")


def expiry_timestamp(seconds: int) -> str:
    """When a capability minted now stops working, in the same format.

    Returned to the client so it can tell a URL that expired from one that was
    refused, rather than retrying a dead link and getting an S3 error document
    it cannot interpret.
    """
    expires = datetime.now(UTC) + timedelta(seconds=seconds)
    return expires.isoformat(timespec="milliseconds").replace("+00:00", "Z")


def _parse_kind(value: object, errors: dict[str, str]) -> str | None:
    if not isinstance(value, str) or value not in KINDS:
        errors["kind"] = "Kind must be one of " + ", ".join(KINDS) + "."
        return None
    return value


def _parse_content_type(value: object, errors: dict[str, str]) -> str | None:
    if not isinstance(value, str):
        errors["contentType"] = "A content type is required."
        return None
    # Lowercased because media types are case-insensitive and a client sending
    # "Application/PDF" is not making a mistake. The NORMALISED value is what
    # gets signed and handed back, so the client and the signature agree on one
    # spelling — a mismatch there is an opaque SignatureDoesNotMatch.
    content_type = value.strip().lower()
    if content_type not in CONTENT_TYPES:
        errors["contentType"] = (
            "That file type is not accepted. Allowed: " + ", ".join(CONTENT_TYPES) + "."
        )
        return None
    return content_type


def _parse_byte_size(value: object, errors: dict[str, str]) -> int | None:
    # bool before int, because in Python `True` IS an int and would validate as
    # a one-byte document.
    if isinstance(value, bool) or not isinstance(value, int):
        errors["byteSize"] = "A size in bytes is required."
        return None
    if value < 1:
        errors["byteSize"] = "A document must have at least one byte."
        return None
    if value > MAX_BYTE_SIZE:
        errors["byteSize"] = (
            f"A document must be {MAX_BYTE_SIZE // (1024 * 1024)} MB or smaller."
        )
        return None
    return value


def _parse_file_name(value: object, errors: dict[str, str]) -> str | None:
    if not isinstance(value, str) or not value.strip():
        errors["fileName"] = "A file name is required."
        return None
    file_name = value.strip()
    if len(file_name) > MAX_FILE_NAME:
        errors["fileName"] = f"Must be {MAX_FILE_NAME} characters or fewer."
        return None
    if _FILE_NAME_FORBIDDEN.search(file_name):
        # One message for the whole class, deliberately. Naming the offending
        # codepoint would be the helpful thing for a legitimate caller and a
        # confirmation for the other kind, and the legitimate caller cannot see
        # the character anyway — that is what it was chosen for.
        errors["fileName"] = (
            "Must not contain path separators, control characters, or "
            "invisible or direction-changing characters."
        )
        return None
    if file_name in _FILE_NAME_RESERVED:
        errors["fileName"] = "Must be a file name."
        return None
    return file_name


def parse_document_upload(payload: Mapping[str, object]) -> DocumentDraft:
    """Validate POST /v1/cases/<case_id>/documents. Unknown keys are ignored.

    Every check here runs BEFORE an upload URL exists. That ordering is the
    point of the endpoint: once a presigned PUT is in a client's hands the only
    thing still refusing anything is S3, and S3 does not know what a pay stub
    is. Content type and size are validated here and then bound into the
    signature, so the URL that comes back cannot be used to store something
    else.
    """
    errors: dict[str, str] = {}

    # Refused rather than ignored, exactly as an unencryptable tax id is
    # elsewhere: silently dropping it would leave the caller believing this
    # record carries provenance when it does not. It never will — see the
    # module docstring for why a document is provenance's object rather than
    # its subject.
    if "provenance" in payload and payload["provenance"] is not None:
        errors["provenance"] = (
            "A document record carries no provenance. Provenance describes "
            "values extracted FROM a document, not the document itself."
        )

    kind = _parse_kind(payload.get("kind"), errors)
    file_name = _parse_file_name(payload.get("fileName"), errors)
    content_type = _parse_content_type(payload.get("contentType"), errors)
    byte_size = _parse_byte_size(payload.get("byteSize"), errors)

    # The None checks are redundant with `errors` but they are what narrows the
    # types, and a redundant guard beats an assert that a future -O strips.
    if (
        errors
        or kind is None
        or file_name is None
        or content_type is None
        or byte_size is None
    ):
        raise FieldValidationError(errors)
    return DocumentDraft(
        kind=kind,
        file_name=file_name,
        content_type=content_type,
        byte_size=byte_size,
    )


def object_key(case_id: str, document_id: str) -> str:
    """Where the bytes live: cases/<case_id>/<document_id>.

    NO PII IN AN OBJECT KEY, EVER. The uploader's file name is stored on the
    record as metadata and never appears here — a key is not access-controlled
    the way a record is: it shows up in bucket inventories, in S3 access logs,
    in CloudTrail data events, in the audit trail the case bucket feeds, and in
    every presigned URL. "smith-jane-2024-tax-return.pdf" in any of those is a
    disclosure, and the same rule the data model states for identifiers applies
    here in as many words.

    Both halves are checked against the uuid4 pattern rather than sanitised,
    which is what makes that structural rather than a habit: a value that could
    carry a name — or a "../" — cannot match, so there is no sanitiser to get
    wrong and no path this function has that produces a key holding anything a
    human typed. Both ids are server-minted, so a failure here is a bug in this
    service, not a request anyone can make.
    """
    if not _UUID_RE.match(case_id) or not _UUID_RE.match(document_id):
        raise ValidationError("object keys are built from server-minted uuids only")
    return f"cases/{case_id}/{document_id}"


def create_document(
    draft: DocumentDraft, *, case_id: str, uploaded_by: str
) -> Document:
    """Stamp a draft with server-generated identity and its storage location.

    `uploaded_by` comes from the verified token and `case_id` from a case the
    caller was just shown to own; neither is ever read from the request body.

    `uploaded_at` records when the upload was AUTHORISED, which is the only
    moment this server observes. There is no completion callback in this issue,
    so nothing here knows when — or whether — the bytes actually landed. See
    api/routes/documents.py for what that costs and why it is still the right
    trade for now.
    """
    document_id = str(uuid.uuid4())
    return Document(
        id=document_id,
        case_id=case_id,
        kind=draft.kind,
        file_name=draft.file_name,
        content_type=draft.content_type,
        byte_size=draft.byte_size,
        # Derived once, here, and then STORED rather than re-derived on every
        # read. The scheme above can change for documents written after the
        # change without stranding the objects written before it — a derived
        # key would silently start pointing somewhere empty.
        #
        # The bucket is deliberately not part of it: which bucket is
        # environment configuration (CASE_DOCUMENT_BUCKET), and a row never
        # crosses from staging to prod.
        storage_ref=object_key(case_id, document_id),
        uploaded_by=uploaded_by,
        uploaded_at=_timestamp(),
    )


def confirm_document(document: Document, blob: StoredBlob) -> Document:
    """Move a record to `stored`, recording what S3 reports rather than what
    the client claimed.

    THE SIZE IS REPLACED, NOT COMPARED. The number on a pending record is what
    the caller asked to be allowed to upload — an input to minting a
    capability. This one is what arrived. Keeping the claim after the fact
    would leave the record asserting something no part of this system ever
    checked.

    In practice the two agree, and they agree for a reason worth stating so
    this does not read as the primary control: `content-length` is bound into
    the presigned signature (adapters/aws/document_blobs.py), so S3 refuses a
    PUT whose body is any other length. A mismatch here would mean the
    signature stopped constraining the request, which is exactly the silent
    failure the signed-header test guards against. This is defence in depth,
    and it is also the only place a size becomes a FACT rather than a claim.

    Idempotent: confirming an already-stored record re-reads the same object
    and produces the same row. The client that retries a dropped response must
    not be punished for it.
    """
    return replace(
        document, status=STATUS_STORED, byte_size=blob.byte_size, etag=blob.etag
    )


def sort_key(document_id: str) -> str:
    return f"DOCUMENT#{document_id}"


def list_order(document: Document) -> tuple[str, str]:
    """Newest first, tie-broken by id — the ONE definition of it, because both
    stores have to agree.

    They cannot get it for free the way the case list does. A case's documents
    are queried by partition and come back in sort-key order, and the sort key
    is DOCUMENT#<uuid4>: random. So the order a DynamoDB query returns carries
    no meaning at all, and both implementations sort explicitly against this
    function instead. The data model says the same thing from the other side —
    ordering comes from the timestamp, never from the id.

    Callers reverse this; the id tiebreak keeps two documents uploaded in the
    same millisecond in a stable order rather than an arbitrary one.
    """
    return (document.uploaded_at, document.id)


def document_item(document: Document) -> dict[str, str | int]:
    """The exact stored item shape, shared by both DocumentStore implementations.

    PK  CASE#<case_id>        the same partition as the case root and every
    SK  DOCUMENT#<id>         other case-scoped entity, so a document is read
                              with the case it belongs to

    No new table and no GSI keys: documents are always reached through their
    case, never listed across cases, and the sparse by-owner index on the case
    table stays one entry per case.
    """
    item: dict[str, str | int] = {
        "PK": partition_key(document.case_id),
        "SK": sort_key(document.id),
        "id": document.id,
        "caseId": document.case_id,
        "kind": document.kind,
        "fileName": document.file_name,
        "contentType": document.content_type,
        "byteSize": document.byte_size,
        "storageRef": document.storage_ref,
        "uploadedBy": document.uploaded_by,
        "uploadedAt": document.uploaded_at,
        "status": document.status,
    }
    # Omitted rather than written empty. DynamoDB has no empty-string type
    # worth using here, and "the attribute is absent" is the honest encoding of
    # "nothing has looked at this object yet" — which is exactly what a pending
    # row means.
    if document.etag is not None:
        item["etag"] = document.etag
    return item


def document_from_item(item: Mapping[str, str | int]) -> Document:
    """Inverse of document_item. Raises ValidationError on an item this service
    did not write — a corrupt row should fail loudly here rather than become a
    half-populated Document whose storage_ref points at nothing."""
    try:
        return Document(
            id=str(item["id"]),
            case_id=str(item["caseId"]),
            kind=str(item["kind"]),
            file_name=str(item["fileName"]),
            content_type=str(item["contentType"]),
            byte_size=int(item["byteSize"]),
            storage_ref=str(item["storageRef"]),
            uploaded_by=str(item["uploadedBy"]),
            uploaded_at=str(item["uploadedAt"]),
            # `.get` for these two, where every field above is required, and
            # the asymmetry is deliberate. A row written before this service
            # knew about confirmation is not a corrupt row — it is a row from
            # a version that could not have confirmed anything, so `pending`
            # is not a fallback, it is the true reading of it. A developer's
            # dev table has such rows today.
            status=str(item.get("status", STATUS_PENDING)),
            etag=str(item["etag"]) if "etag" in item else None,
        )
    except (KeyError, ValueError) as error:
        raise ValidationError(f"stored document item is malformed: {error}") from error


def document_json(document: Document) -> dict[str, object]:
    """The API representation.

    Two fields are deliberately absent. `uploadedBy` for the reason `case_json`
    omits `ownerPrincipal` — ownership is a single principal today, so the
    caller is the uploader by construction and echoing a subject identifier
    into a body buys nothing. `storageRef` because the object key is this
    service's business: a client that knew the layout would be a client that
    could come to depend on it, and it has no use for a key it cannot sign.

    `pageCount` and `sha256` are in the data model and are not here yet.
    Neither can be known without reading the bytes, which nothing does until
    extraction (8.7/8.8) — inventing them from the client's word would put two
    unverified numbers on a record whose other fields the server checked.

    `etag` is stored and not exposed, joining `storageRef` for the same reason:
    it identifies the object inside the bucket, and a client has nothing to do
    with it that does not amount to depending on the storage layout.

    `status` IS exposed, and it is the one field here a client must branch on.
    Without it a document whose upload never finished is indistinguishable in
    the list from one that did, and the difference is the difference between a
    file the user can open and a file that is not there. `byteSize` changes
    meaning with it: on a pending record it is what the client said it would
    send, on a stored one it is what S3 counted.
    """
    return {
        "id": document.id,
        "caseId": document.case_id,
        "kind": document.kind,
        "fileName": document.file_name,
        "contentType": document.content_type,
        "byteSize": document.byte_size,
        "uploadedAt": document.uploaded_at,
        "status": document.status,
    }
