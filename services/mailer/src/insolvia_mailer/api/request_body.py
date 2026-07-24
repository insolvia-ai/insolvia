from __future__ import annotations

from flask import request

from insolvia_mailer.core.errors import ValidationError
from insolvia_mailer.core.models import MAX_REQUEST_BYTES


def json_body() -> dict:
    if request.content_length and request.content_length > MAX_REQUEST_BYTES:
        raise ValidationError("request exceeds 4 MiB")
    value = request.get_json(silent=True)
    if not isinstance(value, dict):
        raise ValidationError("request body must be a JSON object")
    return value
