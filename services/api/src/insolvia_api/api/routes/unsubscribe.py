from __future__ import annotations

import logging

from flask import Blueprint, jsonify, request

from insolvia_api.api.dependencies import dependencies
from insolvia_api.core.errors import ValidationError
from insolvia_api.core.unsubscribe import verify_token

logger = logging.getLogger(__name__)

blueprint = Blueprint("unsubscribe", __name__)

# A token is ~120 characters; this leaves room for a longer address without
# accepting a body worth parsing defensively.
MAX_REQUEST_BYTES = 4 * 1024

_REASON = "unsubscribe"


@blueprint.post("/v1/unsubscribe")
def submit_unsubscribe():
    """Honour an unsubscribe link (issue #80 / 6.8).

    Deliberately UNAUTHENTICATED in the AWS sense, like POST /v1/waitlist:
    the caller is the marketing site's SSR Lambda forwarding a click
    server-to-server (docs/adr/0001), and the person clicking holds no
    credentials. The token IS the authentication — an HMAC over the address,
    signed with a secret only this service holds — and verifying it is the
    whole reason this endpoint exists rather than the marketing site calling
    the mailer directly.

    Failure modes, and why they answer the way they do:

    - An invalid, forged, or truncated token -> 400. Nothing is suppressed.
    - No signing secret configured -> 500 via the unexpected-error handler.
      That is correct: without a key there is no way to tell a real token
      from a made-up one, and answering 200 would be a lie.
    - A valid token -> 202, always the same body. The response never reveals
      the address, never says whether it was already suppressed, and never
      says whether it corresponds to a known account, because this endpoint
      is reachable by anyone holding a link.
    """
    if request.content_length and request.content_length > MAX_REQUEST_BYTES:
        raise ValidationError("request body exceeds 4 KiB")
    payload = request.get_json(silent=True)
    if not isinstance(payload, dict):
        raise ValidationError("request body must be a JSON object")

    token = payload.get("token")
    if not isinstance(token, str):
        raise ValidationError("token is required")

    config = dependencies().config
    address = verify_token(token, secret=config.unsubscribe_secret or "")
    dependencies().mailer.suppress(address, reason=_REASON)

    # The address is PII and stays out of the log line, exactly as the
    # waitlist route logs only its server-generated id. That the mailer
    # stores only a hash would be undone by logging the plaintext here.
    logger.info("unsubscribe honoured")
    return jsonify({"status": "unsubscribed"}), 202
