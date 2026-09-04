"""Token verification and accessor resolution for the MCP surface.

The verification itself is `insolvia_core.auth`, unchanged — the same RS256 /
issuer / token_use / client_id checks the tenant API runs, composed here into
the MCP SDK's `TokenVerifier` seam so the transport answers 401 (with the
spec's `WWW-Authenticate` challenge) for anything the verifier refuses.

Fail closed, exactly as api/auth.py in services/api: every failure — no
header, malformed token, bad signature, expired, wrong issuer, wrong client,
wrong token_use, unknown kid, and no auth config on this deployment at all —
is a None from `verify_token`, which the SDK turns into the same 401. A
rejection logs the coarse `AuthFailureReason` category and nothing else (GLBA).

THE CLIENT-ID ALLOWLIST IS THE AUDIENCE CHECK. Cognito access tokens carry
`client_id` and `scope`, never an RFC 8707 `aud`, so audience binding is
approximated the way `services/api` already does it, widened to a set: this
service verifies its own pre-registered clients — one per harness
(infra/modules/auth), DISJOINT from the app's client id — so an app token
presented here fails closed with no code asked to distinguish the cases
(ADR 0016). That gap against the spec's aud-validation MUST is real and
stated, not papered over; docs/reference/mcp-surface.md § Identity records
it.
"""

from __future__ import annotations

import logging

from insolvia_core.access import Accessor
from insolvia_core.auth import (
    AuthenticationError,
    AuthFailureReason,
    key_id,
    multi_client_settings_or_raise,
    verify_access_token_for_clients,
)
from insolvia_core.ports import FirmStore, JwksProvider
from mcp.server.auth.provider import AccessToken, TokenVerifier

logger = logging.getLogger(__name__)


class CognitoTokenVerifier(TokenVerifier):
    """The MCP SDK's verification seam, over insolvia_core.auth."""

    def __init__(
        self,
        *,
        issuer_url: str | None,
        client_ids: tuple[str, ...],
        jwks_provider: JwksProvider | None,
    ) -> None:
        self._issuer_url = issuer_url
        self._client_ids = client_ids
        self._jwks_provider = jwks_provider

    async def verify_token(self, token: str) -> AccessToken | None:
        try:
            settings = multi_client_settings_or_raise(
                self._issuer_url, self._client_ids
            )
            if self._jwks_provider is None:
                # Configured issuer/clients but no provider composed: a broken
                # deployment, not a caller error. Still a rejection — never
                # "allow".
                raise AuthenticationError(AuthFailureReason.NOT_CONFIGURED)
            signing_key = self._jwks_provider.signing_key(key_id(token))
            principal = verify_access_token_for_clients(
                token, signing_key=signing_key, settings=settings
            )
        except AuthenticationError as error:
            # Category only. No token, no claims, no subject.
            logger.info("authentication rejected", extra={"reason": error.reason.value})
            return None
        return AccessToken(
            token=token,
            client_id=principal.client_id,
            scopes=list(principal.scopes),
            expires_at=principal.expires_at,
            subject=principal.subject,
        )


def resolve_accessor(firm_store: FirmStore, subject: str) -> Accessor | None:
    """The caller's accessor, or None if they are not active in an active firm.

    THE TWO READS ADR 0009 COSTS, once per tool call — never cached, not even
    per session, because the transport is stateless and staleness here is a
    security property: an admin who cuts an agent's access expects it cut
    now, not within the hour (mcp-surface.md § Identity). One resolves the
    firm user through the by-subject index; one fetches the firm itself, so a
    SUSPENDED firm is actually suspended.
    """
    user = firm_store.find_user(subject)
    if user is None or user.status != "active":
        logger.info("accessor unresolved", extra={"reason": "no_active_firm_user"})
        return None
    firm = firm_store.get_firm(user.firm_id)
    if firm is None or firm.status != "active":
        logger.info("accessor unresolved", extra={"reason": "firm_not_active"})
        return None
    return Accessor(firm=firm, user=user)
