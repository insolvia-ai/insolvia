"""The client-portal principal (ADR 0023): `@require_client`.

The second authenticating decorator in this service, and the only one a
`/v1/portal/` route may carry. It does in ONE step what staff routes split
across `@require_auth` and `current_accessor()`, because the portal has no
`/v1/me`-style route that must answer for a caller with nothing to resolve:

    @blueprint.get("/v1/portal/me")
    @require_client                        # 401 — not a portal token
    def portal_me_route():                 # 403 — no live binding
        client = current_client()
        ...

Same placement rule as `@require_auth`: BELOW the route decorator, or the
registered view is the unwrapped one and the check never runs.

## What it verifies, and against what

The token's `client_id` must equal `AUTH_PORTAL_CLIENT_ID` — the portal's own
app client, and nothing else. A staff token names the web client and fails
here with INVALID_CLIENT; a portal token fails every staff route the same way
in reverse. The two ids must differ or nothing verifies at all
(`insolvia_core.auth.portal_settings_or_raise`): disjointness IS the audience
check, since Cognito access tokens carry no `aud` (ADR 0016's pattern).

## What it resolves, and why by primary key

The binding (`insolvia_core.clients`). The by-subject index answers "which
firm", eventually consistently; the row is then re-read by PRIMARY KEY,
strongly consistent, so a revocation takes effect on the very next request
rather than whenever the index catches up. Then the firm, so a suspended firm's
clients are suspended with it — `current_accessor`'s second read, for the same
reason. Neither is cached beyond the request: a revoked binding that kept
working would be the one staleness that is a security property.

## What a refusal records

A binding that exists and no longer admits its holder — revoked, or its firm
suspended — answers 403 AND writes a `denied` access-log row against the
binding's case, the client's subject as principal: that is what a person
whose access was withdrawn trying anyway looks like, and it is the row ADR
0023's threat model promises. A subject with no binding at all has no case to
record against; it answers the same 403 and leaves only the metadata log line.

The action is `portal.read` for a safe method and `portal.answer` otherwise —
the verb the request would have been.
"""

from __future__ import annotations

import functools
import logging
from typing import Any, NoReturn, cast

from flask import g, jsonify, request
from flask.typing import ResponseReturnValue
from insolvia_core.access import ClientAccessor
from insolvia_core.access_log import record_access
from insolvia_core.auth import AuthenticationError, portal_settings_or_raise
from insolvia_core.clients import INVITED, ClientBinding, activate
from insolvia_core.errors import ForbiddenError
from insolvia_core.ports import ClientBindingStore

from insolvia_api.api.auth import (
    CLIENT,
    PRINCIPAL_CLASS_ATTRIBUTE,
    UNAUTHORIZED_BODY,
    Principal,
    View,
    verify_request_token,
)
from insolvia_api.api.dependencies import dependencies

logger = logging.getLogger(__name__)

_CLIENT_KEY = "insolvia_client_accessor"
_CLIENT_PRINCIPAL_KEY = "insolvia_client_principal"

# One message for every reason, `current_accessor`'s rule: "your access was
# withdrawn", "you were never invited" and "your lawyer's firm is suspended"
# are the same instruction — talk to your law firm — and which one it is
# belongs to the firm, not to this response body.
_REFUSED = "you do not have access to a case through the client portal"

_SAFE_METHODS = frozenset({"GET", "HEAD", "OPTIONS"})


def authenticate_client() -> Principal:
    """Verify the request's bearer token as a CLIENT-PORTAL token."""
    config = dependencies().config
    return verify_request_token(
        portal_settings_or_raise(
            config.auth_issuer_url,
            config.auth_portal_client_id,
            staff_client_id=config.auth_client_id,
        )
    )


def _bindings() -> ClientBindingStore:
    store = dependencies().client_binding_store
    if store is None:
        # A composition bug, not a caller error — the same rule
        # resolve_accessor applies to a missing firm store.
        raise RuntimeError("client binding store is not composed")
    return store


def _refuse(binding: ClientBinding, *, reason: str) -> NoReturn:
    """A binding that no longer admits its holder: log the denial against its
    case, then 403."""
    access_log = dependencies().access_log
    if access_log is None:
        raise RuntimeError("access log is not composed")
    access_log.record(
        record_access(
            case_id=binding.case_id,
            principal=binding.subject,
            action="portal.read"
            if request.method in _SAFE_METHODS
            else "portal.answer",
            outcome="denied",
        )
    )
    # A category, never the subject or the case.
    logger.info("client unresolved", extra={"reason": reason})
    raise ForbiddenError(_REFUSED)


def resolve_client(principal: Principal) -> ClientAccessor:
    """The caller's live binding and active firm, or a 403."""
    store = _bindings()
    found = store.find(principal.subject)
    if found is None:
        logger.info("client unresolved", extra={"reason": "no_binding"})
        raise ForbiddenError(_REFUSED)

    # Re-read by primary key: the index is eventually consistent, the row is
    # not, and a revocation must not wait for the index.
    binding = store.get(found.firm_id, principal.subject)
    if binding is None:
        logger.info("client unresolved", extra={"reason": "no_binding"})
        raise ForbiddenError(_REFUSED)
    if not binding.live:
        _refuse(binding, reason="binding_revoked")

    firm_store = dependencies().firm_store
    if firm_store is None:
        raise RuntimeError("firm store is not composed")
    firm = firm_store.get_firm(binding.firm_id)
    if firm is None or firm.status != "active":
        _refuse(binding, reason="firm_not_active")

    if binding.status == INVITED:
        # The first request after the client's first sign-in. Written here,
        # once, rather than on every request: the store is asked only when
        # the status actually changes.
        activated = store.update(activate(binding))
        if activated is None:
            # Revoked-and-gone between the read and the write.
            raise ForbiddenError(_REFUSED)
        binding = activated

    return ClientAccessor(firm=firm, binding=binding)


def require_client(view: View) -> View:
    """401 unless the request carries a valid CLIENT-PORTAL token; 403 unless
    that client holds a live binding in an active firm. On success the
    ClientAccessor is on `g` for `current_client()`.

    Stamped `client` for the URL-map check (api/auth.PRINCIPAL_CLASS_ATTRIBUTE).
    """

    @functools.wraps(view)
    def wrapper(*args: Any, **kwargs: Any) -> ResponseReturnValue:
        try:
            principal = authenticate_client()
        except AuthenticationError as error:
            logger.info(
                "portal authentication rejected", extra={"reason": error.reason.value}
            )
            return jsonify(UNAUTHORIZED_BODY), 401
        setattr(g, _CLIENT_PRINCIPAL_KEY, principal)
        setattr(g, _CLIENT_KEY, resolve_client(principal))
        return view(*args, **kwargs)

    setattr(wrapper, PRINCIPAL_CLASS_ATTRIBUTE, CLIENT)
    return cast("View", wrapper)


def current_client() -> ClientAccessor:
    """The ClientAccessor `require_client` put on `g`. A programming error
    (500), not a 401, if the view does not carry the decorator."""
    client = getattr(g, _CLIENT_KEY, None)
    if client is None:
        raise RuntimeError("current_client() requires @require_client on the view")
    return cast("ClientAccessor", client)


def current_client_principal() -> Principal:
    """The verified portal token's principal — for an `origin` (ADR 0023 §
    Writes takes `client_id` and `subject` from here, never a body)."""
    principal = getattr(g, _CLIENT_PRINCIPAL_KEY, None)
    if principal is None:
        raise RuntimeError(
            "current_client_principal() requires @require_client on the view"
        )
    return cast("Principal", principal)
