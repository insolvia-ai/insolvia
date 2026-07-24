from __future__ import annotations

from flask import Blueprint, jsonify

from insolvia_mailer.api.dependencies import authorized_service, dependencies
from insolvia_mailer.api.request_body import json_body
from insolvia_mailer.core.models import MessageRequest

blueprint = Blueprint("messages", __name__)


@blueprint.post("/v1/services/<service_id>/messages")
def submit_message(service_id: str):
    service = authorized_service(service_id)
    message = MessageRequest.from_dict(json_body(), service)
    dependencies().store.admit_message(service, message)
    return (
        jsonify(
            {
                "schema_version": 1,
                "application_message_id": message.application_message_id,
                "status": "accepted",
            }
        ),
        202,
    )
