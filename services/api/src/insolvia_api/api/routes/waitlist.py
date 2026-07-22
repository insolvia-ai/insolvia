from __future__ import annotations

import logging

from flask import Blueprint, jsonify, request

from insolvia_api.api.dependencies import dependencies
from insolvia_api.core.errors import ValidationError
from insolvia_api.core.waitlist import create_waitlist_record, parse_waitlist_submission

logger = logging.getLogger(__name__)

blueprint = Blueprint("waitlist", __name__)

# Generous for a six-field form; rejects junk uploads before JSON parsing.
MAX_REQUEST_BYTES = 64 * 1024


@blueprint.post("/v1/waitlist")
def submit_waitlist():
    """Accept a marketing-site waitlist submission.

    Deliberately UNAUTHENTICATED — this is the public waitlist endpoint the
    marketing SSR Lambda calls server-to-server (docs/adr/0001). Abuse
    control is API Gateway throttling (the infra PR) plus the marketing
    form's honeypot; the honeypot is checked in the marketing action and
    never reaches this API, so there is no such field here.
    """
    if request.content_length and request.content_length > MAX_REQUEST_BYTES:
        raise ValidationError("request body exceeds 64 KiB")
    payload = request.get_json(silent=True)
    if not isinstance(payload, dict):
        raise ValidationError("request body must be a JSON object")

    submission = parse_waitlist_submission(payload)
    record = create_waitlist_record(submission)
    dependencies().waitlist_store.add(record)

    # GLBA: only the server-generated id — never a submitted field value.
    logger.info("waitlist submission stored", extra={"waitlist_id": record.id})
    return jsonify({"id": record.id, "submittedAt": record.submitted_at}), 201
