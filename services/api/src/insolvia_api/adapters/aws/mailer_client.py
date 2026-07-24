"""SigV4-signed HTTP client for the mailer service (issue 6.4).

The mailer's API Gateway endpoint is IAM-authenticated (SigV4, service
`execute-api`) — only the caller role infra/modules/mailer/main.tf allowlists
(this Lambda's execution role, granted execute-api:Invoke in PR2) may invoke
it. Credentials come from the default provider chain, which resolves to that
role's temporary credentials automatically when this runs in Lambda.

Uses botocore directly (already a transitive dependency via boto3 — no new
package) for signing, and stdlib urllib for the actual HTTP call. No
`requests` dependency.
"""

from __future__ import annotations

import json
import os
import urllib.error
import urllib.request
from dataclasses import asdict

from botocore.auth import SigV4Auth
from botocore.awsrequest import AWSRequest
from botocore.session import Session

from insolvia_api.core.mail import OutboundEmail

_SERVICE_ID = "insolvia_api"
_SIGNING_SERVICE = "execute-api"
_SCHEMA_VERSION = 1


class MailerRequestError(Exception):
    """The mailer rejected or failed to accept a send (non-2xx response)."""

    def __init__(self, status: int, body: str) -> None:
        super().__init__(f"mailer request failed: HTTP {status}: {body}")
        self.status = status
        self.body = body


def _region(environ: dict[str, str]) -> str:
    return environ.get("AWS_REGION") or environ.get("AWS_DEFAULT_REGION") or "us-east-1"


def _message_body(email: OutboundEmail, idempotency_key: str) -> dict[str, object]:
    body = asdict(email)
    return {
        "schema_version": _SCHEMA_VERSION,
        "application_message_id": idempotency_key,
        "category": body["category"],
        "message_class": body["message_class"],
        "to_address": body["to_address"],
        "subject": body["subject"],
        "html_body": body["html_body"],
        "text_body": body["text_body"],
        "attachments": [],
    }


class SigV4MailerClient:
    """Mailer implementation that POSTs a signed request to the mailer's
    public HTTPS endpoint at `{base_url}/v1/services/insolvia_api/messages`.
    """

    def __init__(self, base_url: str) -> None:
        self.base_url = base_url.rstrip("/")
        self.region = _region(dict(os.environ))
        self._botocore_session = Session()

    def send(self, email: OutboundEmail, *, idempotency_key: str) -> None:
        url = f"{self.base_url}/v1/services/{_SERVICE_ID}/messages"
        payload = json.dumps(_message_body(email, idempotency_key)).encode("utf-8")

        credentials = self._botocore_session.get_credentials()
        if credentials is None:
            raise RuntimeError(
                "no AWS credentials available to sign the mailer request"
            )

        aws_request = AWSRequest(
            method="POST",
            url=url,
            data=payload,
            headers={"Content-Type": "application/json"},
        )
        SigV4Auth(credentials, _SIGNING_SERVICE, self.region).add_auth(aws_request)
        signed_headers = dict(aws_request.headers.items())

        request = urllib.request.Request(
            url, data=payload, headers=signed_headers, method="POST"
        )
        try:
            with urllib.request.urlopen(request) as response:
                if not (200 <= response.status < 300):
                    raise MailerRequestError(
                        response.status, response.read().decode("utf-8", "replace")
                    )
        except urllib.error.HTTPError as error:
            raise MailerRequestError(
                error.code, error.read().decode("utf-8", "replace")
            ) from error
