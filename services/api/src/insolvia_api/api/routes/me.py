from __future__ import annotations

from flask import Blueprint, jsonify
from flask.typing import ResponseReturnValue
from insolvia_core.firms import FEATURES, permission_for

from insolvia_api.api.auth import current_principal, require_auth, resolve_accessor

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

    ## The `firm` block, and why it is optional

    THE ONE ROUTE THAT RESOLVES THE ACCESSOR WITHOUT REQUIRING ONE. Everywhere
    else, a signed-in caller with no firm is a 403; here it is an ANSWER —
    `firm` is simply absent. That is what makes "signed in but not provisioned"
    a state the client can render, instead of an error screen with nothing on
    it. It is also the reason accessor resolution is a separate step from
    `require_auth` at all (see api/auth.py).

    `permissions` is the EFFECTIVE map, not the stored one — the opposite
    choice from core/firms.firm_user_json, and deliberately. That response is
    an admin looking at somebody's record, where the stored value and the admin
    override are two different facts they need to see apart. This one is a
    client asking "what may I do", and the honest answer to that is what the
    server will actually allow: an admin's row says `firm_administration:
    hidden` and they can nonetheless manage users. A client rendering the
    stored map would hide the button and then be surprised the endpoint works.
    """
    principal = current_principal()
    body: dict[str, object] = {
        "subject": principal.subject,
        "username": principal.username,
        "clientId": principal.client_id,
        "scopes": list(principal.scopes),
        "expiresAt": principal.expires_at,
    }

    accessor = resolve_accessor()
    if accessor is not None:
        body["firm"] = {
            "id": accessor.firm.id,
            "name": accessor.firm.name,
            "role": accessor.user.role,
            "displayName": accessor.user.display_name,
            "isAdmin": accessor.user.is_admin,
            "accessAllCases": accessor.user.access_all_cases,
            "permissions": {
                feature: permission_for(accessor.user, feature) for feature in FEATURES
            },
        }
    return jsonify(body)
