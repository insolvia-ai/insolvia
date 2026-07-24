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
    payload: dict[str, object] = {
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
    # Omitted rather than sent as null when there is no link. The mailer's
    # request model rejects unknown keys but accepts a missing optional one,
    # and omitting keeps the wire body identical to what it was before
    # unsubscribe links existed for any send that has none.
    if body["list_unsubscribe_url"]:
        payload["list_unsubscribe_url"] = body["list_unsubscribe_url"]
    return payload


class SigV4MailerClient:
    """Mailer implementation that POSTs signed requests to the mailer's public
    HTTPS endpoints under `{base_url}/v1/services/insolvia_api/` — `messages`
    to send, `suppressions` to stop sending.
    """

    def __init__(self, base_url: str) -> None:
        self.base_url = base_url.rstrip("/")
        self.region = _region(dict(os.environ))
        self._botocore_session = Session()

    def send(self, email: OutboundEmail, *, idempotency_key: str) -> None:
        self._post("messages", _message_body(email, idempotency_key))

    def suppress(self, address: str, *, reason: str) -> None:
        """Add `address` to the mailer's suppression store (issue #80).

        Same endpoint family, same signing, same allowlisted caller role as
        `send` — the mailer treats a suppression write as one more thing a
        registered service may ask for. The ownership proof that justifies
        the call is verified before we get here (api/routes/unsubscribe.py).
        """
        self._post(
            "suppressions",
            {
                "schema_version": _SCHEMA_VERSION,
                "email_address": address,
                "reason": reason,
            },
        )

    def _post(self, resource: str, body: dict[str, object]) -> None:
        url = f"{self.base_url}/v1/services/{_SERVICE_ID}/{resource}"
        payload = json.dumps(body).encode("utf-8")

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
