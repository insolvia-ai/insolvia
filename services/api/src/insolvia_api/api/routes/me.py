from __future__ import annotations

from flask import Blueprint, jsonify
from flask.typing import ResponseReturnValue

from insolvia_api.api.auth import current_principal, require_auth

blueprint = Blueprint("me", __name__)


@blueprint.get("/v1/me")
@require_auth
def read_me() -> ResponseReturnValue:
    """The signed-in caller's identity (issue #79 / 7.4).

    The first authenticated endpoint, and the app's "is my token still good?"
    probe. `@require_auth` sits BELOW the route decorator — see
    api/auth.py's module docstring for why that ordering is not optional.

    Everything in the body comes from claims this request's token already
    proved. **There is no call to Cognito** — no GetUser, no AWS call at all.
    A round trip per request would add latency and a Cognito dependency to
    read data the signed token already carries.

    No email, on purpose. The pool uses `username_attributes = ["email"]`, so
    an access token's `username` is a Cognito-generated UUID and the address
    appears in no access-token claim. The app shows the email from the ID
    token it already holds; this endpoint answers "who does the API think you
    are", which is `sub`.

    `username` is that UUID, not an address — it is returned for support and
    correlation, and clients must not display it as one.
    """
    principal = current_principal()
    return jsonify(
        {
            "subject": principal.subject,
            "username": principal.username,
            "clientId": principal.client_id,
            "scopes": list(principal.scopes),
            "expiresAt": principal.expires_at,
        }
    )
