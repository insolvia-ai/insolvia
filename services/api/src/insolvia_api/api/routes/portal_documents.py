"""The portal's document requests and uploads (ADR 0023 PR 5 / #364) — what
a CLIENT reaches: the documents their firm asked for, and the upload that
answers one.

The portal's rules, all of them (`routes/portal.py`'s docstring): every
route carries `@require_client` and nothing else; no route names a case —
the case is the binding's; and the case is reached only through the
binding's projection (`CaseStore.public_status`), never `CaseStore.get`.
The disjointness walk in tests/unit/test_portal_routes.py covers these
rules without a change; tests/unit/test_portal_documents.py pins the
source-level half for this module.

ONE UPLOAD SEAM, NOT TWO (ADR 0023 decision 6). `POST /v1/portal/documents`
is `create_document` with the client's subject as uploader, channel
`client`, the request's kind, the same presigned PUT (`documents.upload_block`
— same bucket, same `cases/<case_id>/<document_id>` key, same reaper tag, no
encryption header) and the same `complete` step, which is where the request
is satisfied (`documents.satisfy_request`). Then `_trigger_extraction`, which
returns at once for a client's upload: extraction waits for a firm user to
ask, until ADR 0019's ZDR item closes.

WHAT A CLIENT SEES of a document: their own uploads' metadata, against the
request each answers. Never a staff upload, never a download URL — not even
for their own file, in v1.

Every request is on the access log with the client as principal:
`portal.read` for the list, the existing `document.create` for the two
halves of an upload (`routes/documents.py` on why completion is not a verb
of its own).
"""

from __future__ import annotations

import logging

from flask import Blueprint, jsonify, request
from flask.typing import ResponseReturnValue
from insolvia_core.access import ClientAccessor
from insolvia_core.access_log import record_access
from insolvia_core.document_requests import (
    portal_request_json,
    progress,
    progress_json,
)
from insolvia_core.documents import (
    CHANNEL_CLIENT,
    Document,
    confirm_document,
    create_document,
    parse_document_upload,
    portal_document_json,
)
from insolvia_core.errors import (
    ConflictError,
    FieldValidationError,
    ForbiddenError,
    NotFoundError,
    ValidationError,
)
from insolvia_core.ports import (
    AccessLog,
    CaseStore,
    DocumentBlobStore,
    DocumentRequestStore,
    DocumentStore,
)

from insolvia_api.api.client_auth import current_client, require_client
from insolvia_api.api.dependencies import dependencies
from insolvia_api.api.routes.documents import (
    _trigger_extraction,
    open_request_or_400,
    satisfy_request,
    upload_block,
)

logger = logging.getLogger(__name__)

blueprint = Blueprint("portal_documents", __name__)

# routes/documents.py's limit, for the same four short fields plus an id.
MAX_REQUEST_BYTES = 8 * 1024


def _stores() -> tuple[
    CaseStore, DocumentStore, DocumentBlobStore, DocumentRequestStore, AccessLog
]:
    deps = dependencies()
    if (
        deps.case_store is None
        or deps.document_store is None
        or deps.document_blobs is None
        or deps.document_request_store is None
        or deps.access_log is None
    ):
        raise RuntimeError("the portal's document stores are not composed")
    return (
        deps.case_store,
        deps.document_store,
        deps.document_blobs,
        deps.document_request_store,
        deps.access_log,
    )


def _json_body() -> dict[str, object]:
    if request.content_length and request.content_length > MAX_REQUEST_BYTES:
        raise ValidationError("request body exceeds 8 KiB")
    payload = request.get_json(silent=True)
    if not isinstance(payload, dict):
        raise ValidationError("request body must be a JSON object")
    return payload


def _bound_case(client: ClientAccessor, action: str) -> str:
    """The binding's case, if it still exists, with the request on the
    access log — the portal's one way to a case. A live binding whose case
    is gone is the same 403 `routes/portal.py` answers."""
    case_store, _, _, _, access_log = _stores()
    access_log.record(
        record_access(case_id=client.case_id, principal=client.subject, action=action)
    )
    if case_store.public_status(client) is None:
        raise ForbiddenError(
            "you do not have access to a case through the client portal"
        )
    return client.case_id


def _own_upload_or_404(client: ClientAccessor, document_id: str) -> Document:
    """One of THIS client's uploads on their case. A staff upload, another
    client's (the other spouse's on a joint case), or a document of another
    case all answer the same 404 as one that never existed."""
    _, document_store, _, _, _ = _stores()
    document = document_store.get(client.case_id, document_id)
    if (
        document is None
        or document.channel != CHANNEL_CLIENT
        or document.uploaded_by != client.subject
    ):
        raise NotFoundError("document not found")
    return document


@blueprint.get("/v1/portal/document-requests")
@require_client
def portal_document_requests_route() -> ResponseReturnValue:
    """The documents the firm asked this client for, each with its status and
    the client's own uploads against it, and the same progress staff see.
    Every request on the case, waived ones included — "no longer needed" is
    something the client should be told rather than left to infer."""
    client = current_client()
    case_id = _bound_case(client, "portal.read")
    _, document_store, _, request_store, _ = _stores()
    requests = request_store.list_for_case(case_id)
    own = [
        document
        for document in document_store.list_for_case(case_id)
        if document.channel == CHANNEL_CLIENT and document.uploaded_by == client.subject
    ]
    return (
        jsonify(
            {
                "requests": [
                    portal_request_json(
                        item,
                        [
                            portal_document_json(d)
                            for d in own
                            if d.request_id == item.id
                        ],
                    )
                    for item in requests
                ],
                "progress": progress_json(progress(requests)),
            }
        ),
        200,
    )


@blueprint.post("/v1/portal/documents")
@require_client
def portal_create_document_route() -> ResponseReturnValue:
    """Authorise one upload against one of the case's requests:
    `{requestId, fileName, contentType, byteSize}`. The kind is the
    request's. Answers like the staff route — the record, `pending`, and the
    `upload` block — in the client's projection."""
    client = current_client()
    _, document_store, _, _, _ = _stores()

    # Body before the case, routes/documents.py's order and reason.
    body = _json_body()
    request_id = body.get("requestId")
    if not isinstance(request_id, str) or not request_id:
        raise FieldValidationError(
            {"requestId": "Choose the document this upload is for."}
        )
    case_id = _bound_case(client, "document.create")
    answered = open_request_or_400(case_id, request_id)
    draft = parse_document_upload(body, kind=answered.kind)

    document = create_document(
        draft,
        case_id=case_id,
        uploaded_by=client.subject,
        channel=CHANNEL_CLIENT,
        request_id=answered.id,
    )
    document_store.create(document)
    upload = upload_block(document)
    logger.info(
        "client document created",
        extra={"document_id": document.id, "request_id": answered.id},
    )
    return (
        jsonify({"document": portal_document_json(document), "upload": upload}),
        201,
    )


@blueprint.post("/v1/portal/documents/<document_id>/complete")
@require_client
def portal_complete_document_route(document_id: str) -> ResponseReturnValue:
    """The client says the PUT finished: the staff route's three steps
    (HeadObject, clear the tag, write `stored`), then the request it answers
    is received. Idempotent throughout."""
    client = current_client()
    _, document_store, blobs, _, _ = _stores()

    _bound_case(client, "document.create")
    document = _own_upload_or_404(client, document_id)

    stored = blobs.stat(document.storage_ref)
    if stored is None:
        raise ConflictError(
            "the object for this document is not in the bucket; "
            "the upload did not complete"
        )
    blobs.clear_upload_tag(document.storage_ref)
    confirmed = confirm_document(document, stored)
    if document_store.update(confirmed) is None:
        raise NotFoundError("document not found")

    satisfy_request(confirmed)
    # A no-op for this channel — the function's own first line — kept so
    # the one place that decides auto-extraction decides it for both routes.
    _trigger_extraction(confirmed, accepted_by=client.subject)
    logger.info("client document completed", extra={"document_id": document_id})
    return jsonify({"document": portal_document_json(confirmed)}), 200
