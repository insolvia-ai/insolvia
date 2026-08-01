"""The Flask glue for authentication (issue #79 / 7.4).

This is the FIRST decorator in this codebase, so it sets the pattern. The
shape to copy:

    from insolvia_api.api.auth import current_principal, require_auth

    @blueprint.get("/v1/thing")
    @require_auth
    def read_thing() -> ResponseReturnValue:
        principal = current_principal()
        ...

`@require_auth` goes **below** the route decorator — Flask registers whatever
function `@blueprint.get` receives, so the auth wrapper has to be applied
first or the route would point at the unwrapped view and the check would
silently never run. That ordering is the one easy way to get this wrong.

## Opt-in, not opt-out

Routes are public unless they say otherwise. A global `before_request` guard
with an exemption list is the alternative, and it is worse here: this service
has three deliberately public routes (`/health`, `POST /v1/waitlist`,
`POST /v1/unsubscribe` — see their docstrings for why each one is), and an
exemption list drifts silently as routes are added. A decorator is visible in
the diff that adds the route.

The trade-off is real and worth naming: forgetting `@require_auth` leaves a
route open. That is why the decorator is loud, documented here, and why
`/me`'s test asserts the unauthenticated case rather than only the happy one.

## Fail closed

Every failure — no header, malformed header, bad signature, expired, wrong
issuer, wrong client, wrong token_use, unknown kid, **and no auth config on
this deployment at all** — is a 401 with the same body. There is no branch
anywhere below in which a missing key, a missing setting, or a missing
provider results in the request proceeding.

## Logging

A rejection logs the coarse `AuthFailureReason` category and nothing else.
Never the token, never a claim, never `sub` — the request log is metadata
only (GLBA), and this line holds to the same rule.
"""

from __future__ import annotations

import functools
import logging
from collections.abc import Callable
from typing import Any, TypeVar, cast

from flask import g, jsonify, request
from flask.typing import ResponseReturnValue

from insolvia_api.api.dependencies import dependencies
from insolvia_api.core.auth import (
    AuthenticationError,
    AuthFailureReason,
    bearer_token,
    key_id,
    settings_or_raise,
    verify_access_token,
)
from insolvia_api.core.auth import Principal as Principal

logger = logging.getLogger(__name__)

# One body for every 401. The caller learns "unauthorized"; which check failed
# stays in the log, where only we can read it.
UNAUTHORIZED_BODY = {"error": "Unauthorized", "message": "authentication required"}

_PRINCIPAL_KEY = "insolvia_principal"

View = TypeVar("View", bound=Callable[..., ResponseReturnValue])


def authenticate() -> Principal:
    """Verify the request's bearer token and return the principal.

    Raises AuthenticationError for every rejection. Composed from the pure
    pieces in core/auth.py plus exactly one impure step — asking the
    JwksProvider port for the key named by the token's `kid`.
    """
    deps = dependencies()
    settings = settings_or_raise(
        deps.config.auth_issuer_url, deps.config.auth_client_id
    )
    provider = deps.jwks_provider
    if provider is None:
        # Configured issuer/client but no provider composed: a broken
        # deployment, not a caller error. Still 401 — never "allow".
        raise AuthenticationError(AuthFailureReason.NOT_CONFIGURED)

    token = bearer_token(request.headers.get("Authorization"))
    signing_key = provider.signing_key(key_id(token))
    return verify_access_token(token, signing_key=signing_key, settings=settings)


def require_auth(view: View) -> View:
    """Reject the request with 401 unless it carries a valid access token.

    On success the principal is on `flask.g` for `current_principal()`.
    """

    @functools.wraps(view)
    def wrapper(*args: Any, **kwargs: Any) -> ResponseReturnValue:
        try:
            principal = authenticate()
        except AuthenticationError as error:
            # Category only. No token, no claims, no subject.
            logger.info("authentication rejected", extra={"reason": error.reason.value})
            return jsonify(UNAUTHORIZED_BODY), 401
        setattr(g, _PRINCIPAL_KEY, principal)
        return view(*args, **kwargs)

    return cast("View", wrapper)


def current_principal() -> Principal:
    """The principal `require_auth` put on `g`.

    Raises if called from an unprotected view — that is a programming error,
    not a request the caller can provoke, so it is a 500 rather than a 401.
    """
    principal = getattr(g, _PRINCIPAL_KEY, None)
    if principal is None:
        raise RuntimeError("current_principal() requires @require_auth on the view")
    return cast("Principal", principal)
