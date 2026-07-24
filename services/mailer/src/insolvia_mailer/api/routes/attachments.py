from __future__ import annotations

from flask import Blueprint, jsonify, request

from insolvia_mailer.api.dependencies import authorized_service, dependencies
from insolvia_mailer.api.request_body import json_body
from insolvia_mailer.core.errors import ValidationError
from insolvia_mailer.core.models import AttachmentUploadRequest

blueprint = Blueprint("attachments", __name__)


@blueprint.post("/v1/services/<service_id>/attachment-uploads")
def attachment_upload(service_id: str):
    service = authorized_service(service_id)
    upload = AttachmentUploadRequest.from_dict(json_body())
    response = dependencies().store.register_attachment(
        service,
        upload,
        base_url=request.host_url.rstrip("/"),
    )
    return jsonify(response), 201


@blueprint.put("/v1/development-uploads/<token>")
def development_upload(token: str):
    receiver = dependencies().attachment_receiver
    if receiver is None:
        raise ValidationError("development attachment uploads are unavailable")
    receiver.put_upload(
        token,
        request.get_data(cache=False),
        request.headers.get("content-type", ""),
        request.headers.get("x-mailer-content-sha256", ""),
    )
    return "", 204
