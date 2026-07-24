from __future__ import annotations

import logging

from flask import Blueprint, jsonify

from insolvia_mailer.api.dependencies import authorized_service, dependencies
from insolvia_mailer.api.request_body import json_body
from insolvia_mailer.core.models import SuppressionRequest

logger = logging.getLogger(__name__)

blueprint = Blueprint("suppressions", __name__)


@blueprint.post("/v1/services/<service_id>/suppressions")
def add_suppression(service_id: str):
    """Stop sending to an address (issue #80 / 6.8).

    SigV4-authenticated exactly like POST /v1/services/<id>/messages — same
    `authorized_service` gate, same allowlisted caller roles. There is no
    public, unauthenticated variant of this endpoint on purpose: the mailer
    has no way to tell whether whoever holds an unsubscribe link is the
    address's owner, so proving that is the caller's job and the mailer's job
    is only to trust a registered caller that says it has (see
    SuppressionRequest's docstring, and services/api core/unsubscribe.py for
    the proof insolvia_api actually uses).

    Idempotent: suppressing an already-suppressed address succeeds. A person
    clicking unsubscribe twice, or a mail client retrying a one-click POST,
    must not see an error for doing the thing that already worked.
    """
    service = authorized_service(service_id)
    suppression = SuppressionRequest.from_dict(json_body())
    dependencies().store.suppress_recipient(service, suppression)
    # The address is deliberately absent from this log line: the store keeps
    # only a one-way hash of it, and a log that keeps the plaintext would
    # quietly undo that.
    logger.info(
        "recipient suppressed service_id=%s reason=%s",
        service.service_id,
        suppression.reason,
    )
    return jsonify({"schema_version": 1, "status": "suppressed"}), 202
