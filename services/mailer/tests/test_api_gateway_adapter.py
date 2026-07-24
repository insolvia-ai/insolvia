import asyncio
import json

from asgiref.wsgi import WsgiToAsgi
from mangum import Mangum

from insolvia_mailer.adapters.aws.auth import (
    IamAuthorizer,
    reset_principal,
    set_principal,
)
from insolvia_mailer.api.app_factory import create_app
from insolvia_mailer.api.dependencies import ApiDependencies
from insolvia_mailer.core.config import ServiceConfig


class RecordingStore:
    def __init__(self):
        self.messages = []

    def register_attachment(self, service, upload, *, base_url=None):
        raise AssertionError("attachment registration was not expected")

    def admit_message(self, service, message):
        self.messages.append((service, message))


def test_http_api_event_uses_shared_flask_routes_and_iam_context():
    role = "arn:aws:iam::123456789012:role/insolvia_api"
    service = ServiceConfig(
        service_id="insolvia_api",
        sender_name="Insolvia",
        sender_address="no-reply@insolvia.ai",
        allowed_categories=frozenset({"welcome"}),
        allowed_message_classes=frozenset({"transactional"}),
        allowed_role_arns=frozenset({role}),
    )
    store = RecordingStore()
    app = create_app(
        ApiDependencies(
            services={"insolvia_api": service},
            store=store,
            authorizer=IamAuthorizer(),
        )
    )
    adapter = Mangum(WsgiToAsgi(app), lifespan="off")
    body = json.dumps(
        {
            "schema_version": 1,
            "application_message_id": "ins_welcome_123",
            "category": "welcome",
            "message_class": "transactional",
            "to_address": "recipient@example.com",
            "subject": "Welcome",
            "html_body": "<p>Hello</p>",
            "text_body": "Hello",
            "attachments": [],
        }
    )
    event = {
        "version": "2.0",
        "routeKey": "POST /v1/services/{service_id}/messages",
        "rawPath": "/v1/services/insolvia_api/messages",
        "rawQueryString": "",
        "headers": {
            "content-length": str(len(body.encode())),
            "content-type": "application/json",
            "host": "mailer.example.com",
        },
        "requestContext": {
            "domainName": "mailer.example.com",
            "http": {
                "method": "POST",
                "path": "/v1/services/insolvia_api/messages",
                "protocol": "HTTP/1.1",
                "sourceIp": "127.0.0.1",
            },
            "requestId": "test",
            "stage": "$default",
        },
        "body": body,
        "isBase64Encoded": False,
    }

    loop = asyncio.new_event_loop()
    asyncio.set_event_loop(loop)
    token = set_principal(role)
    try:
        response = adapter(event, None)
    finally:
        reset_principal(token)
        loop.close()
        asyncio.set_event_loop(None)

    assert response["statusCode"] == 202, response
    assert len(store.messages) == 1
