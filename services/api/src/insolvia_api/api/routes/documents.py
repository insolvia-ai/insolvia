"""Case documents: upload, list, download and delete (issue 8.6).

WHY THE CLIENT PUTS BYTES INTO S3 ITSELF, WHICH LOOKS LIKE A VIOLATION OF
ADR 0001 AND IS NOT.

ADR 0001 says no client ever holds AWS credentials or calls an AWS service
directly, and every read and write of Insolvia data is brokered by this API.
Documents are the one place that reads as though it has been bent, so the
reasoning is here rather than in a commit message.

The first half is that an API-mediated upload is not slower — it is
IMPOSSIBLE. API Gateway caps a request body at 10 MB and Lambda at 6 MB for a
synchronous invocation. A scanned credit report or a year of bank statements
routinely exceeds both. An endpoint that accepted the bytes would work for the
small documents and fail for exactly the documents this feature exists to
carry, and it would fail at the gateway, before any code here could explain
why.

The second half is what a presigned URL actually is, because "the client talks
to S3" describes it badly. It is a CAPABILITY THIS SERVER MINTS. The server
chooses the bucket, the exact object key, the single HTTP verb, the expiry, the
content type, the byte count and the encryption header, and signs that
combination with its own credentials — after checking that the caller owns the
case. What the client receives is not a credential and not authority to name
anything: it is a ticket to one object, for one verb, for a few minutes, and it
cannot be edited without invalidating the signature.

The thing ADR 0001 refuses is a client holding standing authority over a data
store — a Cognito identity pool, a broad IAM grant, anything the client can
re-aim. This holds none of that. Every property of the request was decided
server-side; the client's only power is to use the ticket or not. The ADR's
own words for the alternative it rejected are "a credential we no longer
control", and this is the opposite: authority that expires on its own, names
one object, and was never delegated in the first place.

What the trade genuinely costs, stated rather than discovered: the bytes leave
the client without passing through code we wrote, so nothing validates the
CONTENT of a document. The declared content type and size are bound into the
signature, so a client cannot upload something larger or of a different
declared type — but a PDF that is really a photograph of a cat is a PDF, and
finding that out belongs to extraction (8.7/8.8), which reads the bytes anyway.

ONE MORE GAP, NAMED BECAUSE IT IS THE OBVIOUS FOLLOW-UP. There is no upload
completion callback in this issue, so the record is written when the upload is
AUTHORISED and nothing here ever learns whether the bytes landed. A document
row therefore means "somebody was told to upload this", not "this exists". The
bucket takes the same position from the other side — no ListBucket for this
service, because the case store is the record of what SHOULD be there — and
the failure mode is a row whose download URL 404s, which is visible and
harmless. The alternatives were worse: a `pending` status nothing ever clears
would be a lie in the schema, and a completion endpoint the client may simply
not call is the same gap with more code. Extraction needs to know the bytes
arrived and will have to ask S3; that is where the answer belongs.
"""

from __future__ import annotations

import logging

from flask import Blueprint, jsonify, request
from flask.typing import ResponseReturnValue

from insolvia_api.api.auth import current_accessor, require_auth, requires
from insolvia_api.api.dependencies import dependencies
from insolvia_api.core.access import Accessor
from insolvia_api.core.access_log import record_access
from insolvia_api.core.documents import (
    Document,
    create_document,
    document_json,
    expiry_timestamp,
    parse_document_upload,
)
from insolvia_api.core.errors import NotFoundError, ValidationError
from insolvia_api.core.firms import ADD_EDIT, DOCUMENTS, VIEW_ONLY
from insolvia_api.core.ports import (
    AccessLog,
    CaseStore,
    DocumentBlobStore,
    DocumentStore,
)

logger = logging.getLogger(__name__)

blueprint = Blueprint("documents", __name__)

# Four short fields, one of them a file name capped at 255 characters. Smaller
# than the case root's 64 KiB because there is nothing here that grows.
MAX_REQUEST_BYTES = 8 * 1024

# MINUTES, NOT HOURS, for both, and the reason is what the URL is: a bearer
# capability carried in a query string. Query strings are copied into browser
# history, shell history, proxy and CDN logs, screenshots and bug reports, and
# nothing about a leaked one looks alarming. The only real control over that is
# how long it stays useful.
#
# Fifteen for an upload because the window has to cover a user picking a file
# and the transfer STARTING — S3 checks the expiry when it receives the
# request, so a large upload that begins in time is not cut off mid-flight.
# Five for a download because the app asks for one at the moment it needs it
# and uses it immediately; there is no user interaction in between.
#
# Neither costs anything to be short. The client is already authenticated to an
# API that will mint another on request, so an expired URL is one round trip,
# not a failure.
UPLOAD_URL_TTL_SECONDS = 15 * 60
DOWNLOAD_URL_TTL_SECONDS = 5 * 60

# The headers the client MUST send with its PUT, because the adapter signs them
# and S3 checks every one. Content-Type comes from the validated record;
# `x-amz-server-side-encryption` is the header the bucket policy's
# DenyEncryptionDowngrade statement is written against, so a PUT without it is
# refused by the bucket even with a valid signature.
#
# Content-Length is signed too and is deliberately absent from what we send
# back: every HTTP client sets it from the body it is about to send, and
# browsers refuse to let JavaScript set it at all. It is listed here in words
# so nobody adds it in code.
SSE_HEADER = "x-amz-server-side-encryption"
SSE_VALUE = "aws:kms"


def _stores() -> tuple[CaseStore, DocumentStore, DocumentBlobStore, AccessLog]:
    """The four collaborators, or a loud failure.

    All are Optional on ApiDependencies so the existing public-route tests can
    build one without them. In a deployed environment they are always present:
    entrypoints/api_lambda.py refuses to boot without CASE_DOCUMENT_BUCKET.
    Reaching this branch is a composition bug, so it raises rather than
    degrading — a document endpoint that quietly stopped recording access would
    be worse than one that stopped working.
    """
    deps = dependencies()
    if (
        deps.case_store is None
        or deps.document_store is None
        or deps.document_blobs is None
        or deps.access_log is None
    ):
        raise RuntimeError("the document stores and access log are not composed")
    return deps.case_store, deps.document_store, deps.document_blobs, deps.access_log


def _json_body() -> dict[str, object]:
    if request.content_length and request.content_length > MAX_REQUEST_BYTES:
        raise ValidationError("request body exceeds 8 KiB")
    payload = request.get_json(silent=True)
    if not isinstance(payload, dict):
        raise ValidationError("request body must be a JSON object")
    return payload


def _reachable_case_or_404(accessor: Accessor, case_id: str, action: str) -> None:
    """Resolve the case first, and record the attempt either way.

    EVERY document route goes through here, because this is the only
    authorisation check there is: `DocumentStore` takes no accessor and
    enforces nothing (see its Protocol for why one authorisation path beats
    two). A document route that skipped this would hand another firm's client's
    tax return to whoever asked.

    IT TAKES AN ACCESSOR, NOT A SUBJECT, and the rename from
    `_owned_case_or_404` is the point rather than tidying. "Owned" described a
    model where a case belonged to one Cognito subject; it now belongs to a
    FIRM, and reaching it means being in that firm AND (an admin, or
    `access_all_cases`, or linked to this matter). `CaseStore.get` applies the
    whole rule — core/access.may_see_case — so the change here is one argument
    and nothing else, which is exactly what it should have been.

    What that buys the documents feature: a colleague at the same firm can now
    open the bank statements on a matter they are on. Under the old model they
    got a 404 on their own firm's file.

    One access-log row per request, recorded here rather than after the
    document resolves, and "allowed" therefore means the caller reached the
    CASE. That is the right subject: the table is keyed by case, and the
    probing this log exists to make visible is somebody walking ids across
    cases — which lands here as `denied`. Someone guessing document ids inside
    a case they can already reach is guessing at their own firm's data.
    """
    case_store, _, _, access_log = _stores()
    case = case_store.get(case_id, accessor=accessor)
    access_log.record(
        record_access(
            case_id=case_id,
            principal=accessor.subject,
            action=action,
            outcome="allowed" if case is not None else "denied",
        )
    )
    if case is None:
        # Identical to a case that does not exist — core/errors.py explains why
        # distinguishing them turns this into an id oracle.
        raise NotFoundError("case not found")


def _document_or_404(case_id: str, document_id: str) -> Document:
    """One document of an already-authorised case.

    The case id is half the store's key, so a document id belonging to another
    case does not resolve here and answers the same 404 as one that never
    existed. That is the second half of the id-oracle argument: without it, a
    document id leaked from one firm's case would confirm itself against
    another firm's.
    """
    _, document_store, _, _ = _stores()
    document = document_store.get(case_id, document_id)
    if document is None:
        raise NotFoundError("document not found")
    return document


@blueprint.post("/v1/cases/<case_id>/documents")
@require_auth
@requires(DOCUMENTS, ADD_EDIT)
def create_document_route(case_id: str) -> ResponseReturnValue:
    """Record a document and hand back a capability to upload its bytes.

    Returns the record and an `upload` block: the URL, the verb, the headers
    the signature demands, and when it stops working.
    """
    _, document_store, blobs, _ = _stores()
    accessor = current_accessor()

    # Body BEFORE ownership, which looks backwards and is deliberate. The body
    # check is about the caller's own request and reveals nothing about the
    # case — a 400 says "your JSON is wrong", not "that case exists". Doing it
    # first keeps the access log honest: an entry there means the case was
    # actually reached, not that someone sent a malformed request at it.
    draft = parse_document_upload(_json_body())
    _reachable_case_or_404(accessor, case_id, "document.create")

    document = create_document(draft, case_id=case_id, uploaded_by=accessor.subject)

    # The ROW FIRST, then the capability, and the order matters. If the mint
    # fails after the row is written, the case shows a document whose bytes
    # never arrived — visible, and the client can simply upload again. The
    # reverse would hand out a write capability for an object nothing records,
    # and with no ListBucket on this service's role, nothing would ever find
    # it again. The bucket's own comments take the same position: the case
    # store is the record of what should be there.
    document_store.create(document)
    upload_url = blobs.upload_url(
        document.storage_ref,
        content_type=document.content_type,
        byte_size=document.byte_size,
        expires_in=UPLOAD_URL_TTL_SECONDS,
    )

    # GLBA: ids and the declared kind. NEVER the file name — a client's own
    # file names are routinely "jane-smith-2023-tax-return.pdf", which is both
    # the person and their affairs in one string, and log lines are the one
    # place that data would sit unencrypted and long-lived.
    logger.info(
        "document created",
        extra={"case_id": case_id, "document_id": document.id, "kind": document.kind},
    )
    return (
        jsonify(
            {
                "document": document_json(document),
                "upload": {
                    "url": upload_url,
                    "method": "PUT",
                    "headers": {
                        "Content-Type": document.content_type,
                        SSE_HEADER: SSE_VALUE,
                    },
                    "expiresAt": expiry_timestamp(UPLOAD_URL_TTL_SECONDS),
                },
            }
        ),
        201,
    )


@blueprint.get("/v1/cases/<case_id>/documents")
@require_auth
@requires(DOCUMENTS, VIEW_ONLY)
def list_documents_route(case_id: str) -> ResponseReturnValue:
    """Every document of one case, newest first.

    Not paginated, unlike GET /v1/cases: the answer is bounded by one case's
    paperwork rather than by an account's whole history, and the store promises
    all of them.
    """
    _, document_store, _, _ = _stores()
    accessor = current_accessor()

    _reachable_case_or_404(accessor, case_id, "document.read")
    documents = document_store.list_for_case(case_id)
    return jsonify({"documents": [document_json(d) for d in documents]}), 200


@blueprint.get("/v1/cases/<case_id>/documents/<document_id>/url")
@require_auth
@requires(DOCUMENTS, VIEW_ONLY)
def document_url_route(case_id: str, document_id: str) -> ResponseReturnValue:
    """A short-lived URL that serves one document's bytes.

    A separate endpoint rather than a field on the list, and that is the whole
    point of the split: listing a case's documents is a cheap, frequent
    operation, and minting a download capability for every row of it would hand
    out a dozen live tickets to satisfy a caller who wanted a file name. This
    way one access-log row means one document was actually fetched.
    """
    _, _, blobs, _ = _stores()
    accessor = current_accessor()

    _reachable_case_or_404(accessor, case_id, "document.download")
    document = _document_or_404(case_id, document_id)

    return (
        jsonify(
            {
                "url": blobs.download_url(
                    document.storage_ref, expires_in=DOWNLOAD_URL_TTL_SECONDS
                ),
                "method": "GET",
                "expiresAt": expiry_timestamp(DOWNLOAD_URL_TTL_SECONDS),
            }
        ),
        200,
    )


@blueprint.delete("/v1/cases/<case_id>/documents/<document_id>")
@require_auth
@requires(DOCUMENTS, ADD_EDIT)
def delete_document_route(case_id: str, document_id: str) -> ResponseReturnValue:
    """Remove a document from a case, and its bytes from the bucket."""
    _, document_store, blobs, _ = _stores()
    accessor = current_accessor()

    _reachable_case_or_404(accessor, case_id, "document.delete")
    document = _document_or_404(case_id, document_id)

    # THE ROW FIRST, THEN THE OBJECT, and this is the same ordering argument as
    # the create path read backwards. Whichever half fails, one of two states
    # is left behind: an object with no row, or a row with no object. The
    # bucket is built for the first — no ListBucket, because "an object that
    # exists without a row" is a state its own comments expect and tolerate —
    # while the second is a document the case still lists and nobody can ever
    # open.
    #
    # The conditional delete is what makes it safe under a double click: two
    # concurrent deletes cannot both remove the row, so only one goes on to
    # delete the object.
    if not document_store.delete(case_id, document_id):
        raise NotFoundError("document not found")
    blobs.delete(document.storage_ref)

    logger.info(
        "document deleted", extra={"case_id": case_id, "document_id": document_id}
    )
    # 204 rather than the deleted record: the client asked for it to be gone,
    # and echoing it back invites treating the response as the document.
    return "", 204
