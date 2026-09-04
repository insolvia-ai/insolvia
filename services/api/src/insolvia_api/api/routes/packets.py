"""Assembled-packet endpoints (issue #96): list a case's packets, and mint
the download for one.

The TRIGGER is not here, deliberately: assembly is a pipeline job, so
"assemble the packet" is POST /v1/cases/<id>/jobs with kind
`packet_assembly`, and its progress is the job status read — this module
serves only the RESULTS. The URL shares its prefix with the generic
/v1/cases/<id>/<collection> routes; "packets" is a static segment and not a
registered collection, so Werkzeug ranking keeps them apart (the
creditor-matrix route's note).
"""

from __future__ import annotations

import logging

from flask import Blueprint, jsonify
from flask.typing import ResponseReturnValue
from insolvia_core.access_log import record_access
from insolvia_core.documents import expiry_timestamp
from insolvia_core.errors import NotFoundError
from insolvia_core.firms import CASES, VIEW_ONLY
from insolvia_core.ports import AccessLog, CaseStore, DocumentBlobStore

from insolvia_api.api.auth import current_accessor, require_auth, requires
from insolvia_api.api.dependencies import dependencies
from insolvia_api.core.packets import packet_json
from insolvia_api.core.ports import PacketStore

logger = logging.getLogger(__name__)

blueprint = Blueprint("packets", __name__)

# The documents route's download TTL, for the documents route's reason: the
# app asks at the moment the user clicks and uses it immediately.
DOWNLOAD_URL_TTL_SECONDS = 5 * 60


def _stores() -> tuple[CaseStore, PacketStore, DocumentBlobStore, AccessLog]:
    deps = dependencies()
    if (
        deps.case_store is None
        or deps.packet_store is None
        or deps.document_blobs is None
        or deps.access_log is None
    ):
        raise RuntimeError(
            "case store, packet store, blob store and access log are not composed"
        )
    return deps.case_store, deps.packet_store, deps.document_blobs, deps.access_log


def _reachable_case_or_404(case_id: str, action: str) -> None:
    """The one authorisation check, the documents route's shape: resolve the
    case under the caller's accessor, record the attempt either way, and
    answer the undistinguishing 404 (the id-oracle rule in core/errors.py).
    PacketStore itself enforces nothing beyond the case scope in its key."""
    case_store, _, _, access_log = _stores()
    accessor = current_accessor()
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
        raise NotFoundError("case not found")


@blueprint.get("/v1/cases/<case_id>/packets")
@require_auth
@requires(CASES, VIEW_ONLY)
def list_packets_route(case_id: str) -> ResponseReturnValue:
    """Every assembled packet of this case, newest first. Old packets stay
    listed — re-assembly creates rather than replaces (core/packets.py), so
    the packet an attorney reviewed last week is still the one they reviewed."""
    _, packet_store, _, _ = _stores()
    _reachable_case_or_404(case_id, "packet.read")
    packets = packet_store.list_for_case(case_id)
    return jsonify({"packets": [packet_json(p) for p in packets]}), 200


@blueprint.get("/v1/cases/<case_id>/packets/<packet_id>/url")
@require_auth
@requires(CASES, VIEW_ONLY)
def packet_url_route(case_id: str, packet_id: str) -> ResponseReturnValue:
    """A short-lived URL that serves one packet's bytes — the issue's "one
    download". Same separate-endpoint argument as the document download: one
    access-log row means one packet actually fetched."""
    _, packet_store, blobs, _ = _stores()
    _reachable_case_or_404(case_id, "packet.download")
    packet = packet_store.get(case_id, packet_id)
    if packet is None:
        # A packet id from another case does not resolve — case_id is half
        # the key — and answers the same 404 as one that never existed.
        raise NotFoundError("packet not found")
    return (
        jsonify(
            {
                "url": blobs.download_url(
                    packet.storage_ref, expires_in=DOWNLOAD_URL_TTL_SECONDS
                ),
                "method": "GET",
                "expiresAt": expiry_timestamp(DOWNLOAD_URL_TTL_SECONDS),
            }
        ),
        200,
    )
