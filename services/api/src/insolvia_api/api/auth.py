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

## Three states, not two

A route is public, authenticated, or authenticated-and-permitted. The third
arrived with firms and it is a genuinely different failure:

    @blueprint.post("/v1/cases")
    @require_auth                          # 401 — who are you
    @requires(CASES, ADD_EDIT)             # 403 — what may you do
    def create_case_route(): ...

`require_auth` still does authentication and nothing else. Resolving which
firm the caller belongs to is `current_accessor()`, called by the route or by
`@requires`, and it answers **403** for a signed-in user who is not in a firm.
That separation is load-bearing rather than tidy: `/v1/me` must keep working
for somebody who has just signed up and has not been added to a firm yet, and
it could not if authentication itself required one.

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
from insolvia_api.core.access import Accessor
from insolvia_api.core.auth import (
    AuthenticationError,
    AuthFailureReason,
    bearer_token,
    key_id,
    settings_or_raise,
    verify_access_token,
)
from insolvia_api.core.auth import Principal as Principal
from insolvia_api.core.errors import ForbiddenError

logger = logging.getLogger(__name__)

# One body for every 401. The caller learns "unauthorized"; which check failed
# stays in the log, where only we can read it.
UNAUTHORIZED_BODY = {"error": "Unauthorized", "message": "authentication required"}

_PRINCIPAL_KEY = "insolvia_principal"
_ACCESSOR_KEY = "insolvia_accessor"

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


def resolve_accessor() -> Accessor | None:
    """The caller's accessor, or None if they are not active in an active firm.

    The non-raising half of `current_accessor`, and it exists for exactly one
    caller: `/v1/me`, which has to be able to REPORT that a signed-in person has
    no firm rather than refusing them. Every other caller wants the 403 and
    should use `current_accessor`.
    """
    cached = getattr(g, _ACCESSOR_KEY, None)
    if cached is not None:
        return cast("Accessor", cached)

    deps = dependencies()
    if deps.firm_store is None:
        # A composition bug, not a caller error: api_lambda.py refuses to boot
        # without FIRM_TABLE_NAME. It raises rather than returning None — a
        # deployment that cannot resolve anyone must not look like every user
        # being unprovisioned, which is a 403 nobody would investigate.
        raise RuntimeError("firm store is not composed")

    subject = current_principal().subject
    user = deps.firm_store.find_user(subject)
    if user is None or user.status != "active":
        logger.info("accessor unresolved", extra={"reason": "no_active_firm_user"})
        return None

    firm = deps.firm_store.get_firm(user.firm_id)
    if firm is None or firm.status != "active":
        logger.info("accessor unresolved", extra={"reason": "firm_not_active"})
        return None

    accessor = Accessor(firm=firm, user=user)
    setattr(g, _ACCESSOR_KEY, accessor)
    return accessor


def current_accessor() -> Accessor:
    """The caller's firm and firm user, or a 403.

    THE STEP BETWEEN "WHO ARE YOU" AND "WHAT MAY YOU SEE", and it is a separate
    step rather than part of `require_auth` for one concrete reason: `/v1/me`
    has to keep working for somebody who has signed in and has not been added
    to a firm yet. Folding resolution into authentication would make that
    request a 403 and leave a new user with nothing to look at but an error.

    Authenticated-but-unprovisioned is a 403, NOT a 401 and not a 404. The
    token is fine; a token they cannot fix by signing in again. And it is a
    fact about their OWN account, so core/errors.ForbiddenError applies rather
    than the anti-oracle 404 that other firms' case ids get.

    TWO READS, and worth stating because this is the hottest path in the
    service. One resolves the firm user through the by-subject index; one
    fetches the firm itself. The second exists so a SUSPENDED firm is actually
    suspended — the alternative is a status field that lies, or bulk-disabling
    every user of a firm that stops paying. Both reads are small keyed lookups
    on the same table, and neither is cached: a cache here would mean a
    permission an admin has just revoked keeps working, which is the one place
    staleness is a security property rather than a latency one.

    Cached per REQUEST, though, on `g` — several routes resolve the accessor,
    and doing the pair of reads once per request rather than once per call is
    free.
    """
    accessor = resolve_accessor()
    if accessor is None:
        # ONE MESSAGE FOR EVERY REASON. "You were disabled", "you were never
        # added" and "your firm is suspended" are the same instruction to the
        # caller — talk to your firm's admin — and which one it is belongs to
        # the firm rather than to this endpoint's response body.
        raise ForbiddenError("your account is not active in a firm")
    return accessor


def requires(feature: str, level: str) -> Callable[[View], View]:
    """Refuse the request with 403 unless the caller's firm grants them
    `level` on `feature`.

    Applied BELOW `@require_auth`, for the same reason `@require_auth` goes
    below the route decorator — a decorator that never wraps the registered
    view is a check that never runs:

        @blueprint.post("/v1/cases")
        @require_auth
        @requires(CASES, ADD_EDIT)
        def create_case_route() -> ResponseReturnValue: ...

    It resolves the accessor as a side effect, which means a route carrying it
    cannot forget to: an unprovisioned caller is refused here rather than
    reaching a handler that would have read a store with no firm to scope by.
    """

    def decorate(view: View) -> View:
        @functools.wraps(view)
        def wrapper(*args: Any, **kwargs: Any) -> ResponseReturnValue:
            if not current_accessor().may(feature, level):
                # The FEATURE is logged, never the subject. Which permission a
                # named person lacks is exactly the kind of thing a request log
                # must not accumulate.
                logger.info("permission refused", extra={"feature": feature})
                raise ForbiddenError("your firm has not granted you access to this")
            return view(*args, **kwargs)

        return cast("View", wrapper)

    return decorate
