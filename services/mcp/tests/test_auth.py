"""Token verification and accessor resolution — mostly what they REFUSE.

The JWT checks themselves are insolvia_core.auth's, pinned by the core
package's own suite; what this file pins is the composition: every refusal
becomes None (the SDK's 401), a good token becomes an AccessToken carrying
the subject and client id, and accessor resolution fails closed for disabled
users and suspended firms.
"""

from __future__ import annotations

import anyio
from insolvia_core.adapters.memory.firm_store import MemoryFirmStore
from insolvia_core.adapters.memory.jwks_provider import StaticJwksProvider
from insolvia_mcp.api.auth import CognitoTokenVerifier, resolve_accessor

from .conftest import (
    CLIENT_ID,
    ISSUER,
    KID,
    OTHER_CLIENT_ID,
    PUBLIC_KEY,
    SUBJECT,
    make_firm,
    make_token,
    make_user,
)


def _verifier(
    *,
    issuer: str | None = ISSUER,
    client_id: str | None = CLIENT_ID,
    provider: StaticJwksProvider | None = None,
) -> CognitoTokenVerifier:
    return CognitoTokenVerifier(
        issuer_url=issuer,
        client_id=client_id,
        jwks_provider=provider
        if provider is not None
        else StaticJwksProvider({KID: PUBLIC_KEY}),
    )


def _verify(verifier: CognitoTokenVerifier, token: str):
    return anyio.run(verifier.verify_token, token)


def test_a_valid_token_yields_the_principal() -> None:
    access = _verify(_verifier(), make_token())
    assert access is not None
    assert access.subject == SUBJECT
    assert access.client_id == CLIENT_ID
    assert access.scopes == ["insolvia/mcp"]


def test_the_app_clients_token_is_not_an_mcp_token() -> None:
    # The audience separation ADR 0016 makes structural: this service
    # verifies exactly its own client id, so a token minted for the app's
    # client fails closed with no code asked to distinguish the cases.
    assert _verify(_verifier(), make_token(client_id=OTHER_CLIENT_ID)) is None


def test_refusals_answer_none() -> None:
    verifier = _verifier()
    assert _verify(verifier, "not-a-jwt") is None
    assert _verify(verifier, make_token(expires_in=-60)) is None
    assert _verify(verifier, make_token(issuer=ISSUER + "x")) is None
    assert _verify(verifier, make_token(token_use="id")) is None
    assert _verify(verifier, make_token(kid="unknown-key")) is None


def test_missing_configuration_fails_closed() -> None:
    # No issuer, no client id, or no provider composed: every one is a
    # rejection, never a bypass.
    assert _verify(_verifier(issuer=None), make_token()) is None
    assert _verify(_verifier(client_id=None), make_token()) is None
    verifier = CognitoTokenVerifier(
        issuer_url=ISSUER, client_id=CLIENT_ID, jwks_provider=None
    )
    assert _verify(verifier, make_token()) is None


def test_resolve_accessor_answers_the_active_pair() -> None:
    store = MemoryFirmStore()
    store.create_firm(make_firm())
    store.add_user(make_user())
    accessor = resolve_accessor(store, SUBJECT)
    assert accessor is not None
    assert accessor.subject == SUBJECT


def test_resolution_fails_closed() -> None:
    unknown = MemoryFirmStore()
    assert resolve_accessor(unknown, SUBJECT) is None

    disabled = MemoryFirmStore()
    disabled.create_firm(make_firm())
    disabled.add_user(make_user(status="disabled"))
    assert resolve_accessor(disabled, SUBJECT) is None

    suspended = MemoryFirmStore()
    suspended.create_firm(make_firm(status="suspended"))
    suspended.add_user(make_user())
    assert resolve_accessor(suspended, SUBJECT) is None
