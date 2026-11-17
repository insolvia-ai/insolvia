"""The Flask glue for staff authentication (#209 / #212).

The same opt-in decorator pattern the tenant API set (its api/auth.py owns
the full argument for decorators-below-the-route and fail-closed), with one
structural difference that IS the admin service's trust model: the verifier
here speaks the GOOGLE profile (`insolvia_core.auth.verify_google_id_token`)
and only that profile. A firm user's Cognito access token — valid, unexpired,
signed by AWS — dies in verification here, because it carries the wrong
issuer, no `aud`, and no `hd`. There is no role check to forget; the
signature-and-claims check is the boundary.

There is deliberately NO third state here. The tenant API separates
"authenticated" from "authenticated-and-permitted" because a signed-in user
may not be in a firm yet; a staff caller has no such intermediate — the
Workspace domain check inside verification is the whole authorization, and
every verified staff member may do everything this service offers. The day
that stops being true (read-only staff, say), the decision point is
`staff_principal_from_claims`, not a permissions table bolted on here.
"""

from __future__ import annotations

import functools
import logging
from collections.abc import Callable
from typing import Any, TypeVar, cast

from flask import g, jsonify, request
from flask.typing import ResponseReturnValue
from insolvia_core.auth import (
    AuthenticationError,
    AuthFailureReason,
    StaffPrincipal,
    bearer_token,
    google_settings_or_raise,
    key_id,
    verify_google_id_token,
)

from insolvia_admin.api.dependencies import dependencies

logger = logging.getLogger(__name__)

# One body for every 401 — which check failed stays in the log.
UNAUTHORIZED_BODY = {"error": "Unauthorized", "message": "authentication required"}

_STAFF_KEY = "insolvia_staff_principal"

View = TypeVar("View", bound=Callable[..., ResponseReturnValue])


def authenticate() -> StaffPrincipal:
    """Verify the request's bearer token as a Google Workspace ID token.

    Raises AuthenticationError for every rejection, including missing
    configuration — a deployment without GOOGLE_CLIENT_ID answers 401 on
    every staff route, never "allow".
    """
    deps = dependencies()
    settings = google_settings_or_raise(
        deps.config.google_client_id, deps.config.workspace_domain
    )
    provider = deps.jwks_provider
    if provider is None:
        raise AuthenticationError(AuthFailureReason.NOT_CONFIGURED)

    token = bearer_token(request.headers.get("Authorization"))
    signing_key = provider.signing_key(key_id(token))
    return verify_google_id_token(token, signing_key=signing_key, settings=settings)


def require_staff(view: View) -> View:
    """Reject the request with 401 unless it carries a valid Workspace ID
    token. On success the principal is on `flask.g` for `current_staff()`.

    Applied BELOW the route decorator, or the check silently never runs —
    the tenant API's api/auth.py owns that footgun's writeup.
    """

    @functools.wraps(view)
    def wrapper(*args: Any, **kwargs: Any) -> ResponseReturnValue:
        try:
            principal = authenticate()
        except AuthenticationError as error:
            logger.info(
                "staff authentication rejected",
                extra={"reason": error.reason.value},
            )
            return jsonify(UNAUTHORIZED_BODY), 401
        setattr(g, _STAFF_KEY, principal)
        return view(*args, **kwargs)

    return cast(View, wrapper)


def current_staff() -> StaffPrincipal:
    """The verified staff caller. Only callable below `@require_staff`."""
    principal = getattr(g, _STAFF_KEY, None)
    if principal is None:
        # A programming error (route forgot the decorator), not a caller
        # error — but the answer is still 401-shaped denial, never data.
        raise AuthenticationError(AuthFailureReason.MISSING_CREDENTIALS)
    return cast(StaffPrincipal, principal)
