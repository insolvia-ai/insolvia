from __future__ import annotations

import logging

from flask import Blueprint, jsonify, request
from flask.typing import ResponseReturnValue
from insolvia_core.access import Accessor
from insolvia_core.errors import NotFoundError, ValidationError
from insolvia_core.firms import (
    FEATURES,
    apply_user_changes,
    full_name,
    parse_self_update,
    permission_for,
)
from insolvia_core.ports import FirmStore

from insolvia_api.api.auth import (
    current_accessor,
    current_principal,
    require_auth,
    resolve_accessor,
)
from insolvia_api.api.dependencies import dependencies

logger = logging.getLogger(__name__)

blueprint = Blueprint("me", __name__)

# The whole writable surface is a first and a last name, each capped at 100
# characters. 4 KiB is still generous for that, and the cap is here so a body
# that is not a name at all is refused before it is parsed at all.
MAX_REQUEST_BYTES = 4 * 1024


def _firm_store() -> FirmStore:
    deps = dependencies()
    if deps.firm_store is None:
        raise RuntimeError("firm store is not composed")
    return deps.firm_store


def _json_body() -> dict[str, object]:
    if request.content_length and request.content_length > MAX_REQUEST_BYTES:
        raise ValidationError("request body exceeds 4 KiB")
    payload = request.get_json(silent=True)
    if not isinstance(payload, dict):
        raise ValidationError("request body must be a JSON object")
    return payload


def _me_body(accessor: Accessor | None) -> dict[str, object]:
    """The one shape /v1/me answers with, for both the read and the write.

    PATCH returning the same body as GET is deliberate: the client's follow-up
    to "my name changed" is re-rendering the same screen state a fresh GET
    would produce, so handing it back saves the round trip and keeps one
    serializer honest for both.
    """
    principal = current_principal()
    body: dict[str, object] = {
        "subject": principal.subject,
        "username": principal.username,
        "clientId": principal.client_id,
        "scopes": list(principal.scopes),
        "expiresAt": principal.expires_at,
    }
    if accessor is not None:
        body["firm"] = {
            "id": accessor.firm.id,
            "name": accessor.firm.name,
            "role": accessor.user.role,
            # The two halves a client edits, and the composed string a client
            # renders. `displayName` is DERIVED — `full_name` is its only
            # author — so it costs nothing to keep sending and it is what lets
            # every screen that merely shows a name stay untouched by the
            # split. Both halves may be `""`: that means "never recorded", and
            # it is what the client's first-run prompt keys on.
            "firstName": accessor.user.first_name,
            "lastName": accessor.user.last_name,
            "displayName": full_name(accessor.user),
            "isAdmin": accessor.user.is_admin,
            "accessAllCases": accessor.user.access_all_cases,
            "permissions": {
                feature: permission_for(accessor.user, feature) for feature in FEATURES
            },
        }
    return body


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
    return jsonify(_me_body(resolve_accessor()))


@blueprint.patch("/v1/me")
@require_auth
def update_me() -> ResponseReturnValue:
    """Correct your own name (issue #216 / 11.16).

    Your name only — core/firms.parse_self_update owns why nothing else on
    the row is self-service. No `@requires` gate, and that is the point of the
    route: the permission axes govern what an admin may do to OTHERS, and
    before this existed a paralegal who mistyped their name at invite time had
    to ask an admin to fix it. `current_accessor()` is still the gate that
    matters — no active membership in an active firm, no row to rename, 403.

    IT IS ALSO WHAT THE CLIENT'S FIRST-RUN PROMPT WRITES TO. A row whose halves
    were derived from a pre-split display name can have an empty surname, and
    that is the state the app blocks on; this route is the only way out of it,
    which is why it takes either half alone rather than demanding both.

    The write goes to the firm-user row alone. Cognito holds no display name
    for this pool, so there is no second system to fall out of sync with —
    unlike email, which is exactly such a fact and stays read-only.
    """
    store = _firm_store()
    accessor = current_accessor()
    changes = parse_self_update(_json_body())

    updated = apply_user_changes(accessor.user, changes)
    written = store.update_user(updated)
    if written is None:
        # Removed from the firm between resolving the accessor and writing.
        # The next request will 403; this one reports the row it did not find.
        raise NotFoundError("firm user not found")

    # GLBA posture as everywhere in this file's neighbours: no name, no
    # subject — that somebody renamed themselves is all a log needs.
    logger.info("self name updated")
    return jsonify(_me_body(Accessor(firm=accessor.firm, user=written)))
