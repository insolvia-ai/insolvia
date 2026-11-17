"""The client-allowlist verification profile (issue #261).

The single-client profile is pinned end-to-end by services/api's test_auth;
this file pins what the ALLOWLIST path adds and — mostly — what it refuses.
Every token is signed for real. Every id below is obviously fake.
"""

from __future__ import annotations

import time

import jwt
import pytest
from cryptography.hazmat.primitives.asymmetric import rsa
from insolvia_core.auth import (
    AuthenticationError,
    AuthFailureReason,
    multi_client_settings_or_raise,
    verify_access_token_for_clients,
)

ISSUER = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE00"
CLAUDE_CLIENT = "examplemcpclaudeclient00"
INSPECTOR_CLIENT = "examplemcpinspector00000"
APP_CLIENT = "exampleappclientid000000"
SUBJECT = "00000000-0000-4000-8000-000000000001"

_PRIVATE_KEY = rsa.generate_private_key(public_exponent=65537, key_size=2048)
_PUBLIC_KEY = _PRIVATE_KEY.public_key()

SETTINGS = multi_client_settings_or_raise(ISSUER, (CLAUDE_CLIENT, INSPECTOR_CLIENT))


def make_token(
    *,
    issuer: str = ISSUER,
    client_id: str = CLAUDE_CLIENT,
    token_use: str = "access",
    subject: str | None = SUBJECT,
    expires_in: int = 3600,
) -> str:
    now = int(time.time())
    claims: dict[str, object] = {
        "iss": issuer,
        "client_id": client_id,
        "token_use": token_use,
        "iat": now,
        "exp": now + expires_in,
    }
    if subject is not None:
        claims["sub"] = subject
    return jwt.encode(claims, _PRIVATE_KEY, algorithm="RS256")


def _verify(token: str):
    return verify_access_token_for_clients(
        token, signing_key=_PUBLIC_KEY, settings=SETTINGS
    )


@pytest.mark.parametrize("client_id", [CLAUDE_CLIENT, INSPECTOR_CLIENT])
def test_every_allowlisted_client_verifies(client_id: str) -> None:
    principal = _verify(make_token(client_id=client_id))
    assert principal.subject == SUBJECT
    assert principal.client_id == client_id


def test_a_client_off_the_allowlist_is_refused() -> None:
    # The app's own token is the case that matters: the two surfaces'
    # client sets are disjoint by construction (ADR 0016), and this is the
    # check that makes an app token not an MCP token.
    with pytest.raises(AuthenticationError) as excinfo:
        _verify(make_token(client_id=APP_CLIENT))
    assert excinfo.value.reason is AuthFailureReason.INVALID_CLIENT


@pytest.mark.parametrize(
    ("token_kwargs", "reason"),
    [
        ({"expires_in": -60}, AuthFailureReason.EXPIRED),
        ({"issuer": ISSUER + "x"}, AuthFailureReason.INVALID_ISSUER),
        ({"token_use": "id"}, AuthFailureReason.WRONG_TOKEN_USE),
        ({"subject": None}, AuthFailureReason.INVALID_CLAIMS),
    ],
)
def test_the_shared_checks_hold_on_this_path(token_kwargs, reason) -> None:
    with pytest.raises(AuthenticationError) as excinfo:
        _verify(make_token(**token_kwargs))
    assert excinfo.value.reason is reason


@pytest.mark.parametrize(
    ("issuer", "client_ids"),
    [
        (None, (CLAUDE_CLIENT,)),
        (ISSUER, None),
        (ISSUER, ()),
        (ISSUER, ("",)),
    ],
)
def test_missing_configuration_fails_closed(issuer, client_ids) -> None:
    with pytest.raises(AuthenticationError) as excinfo:
        multi_client_settings_or_raise(issuer, client_ids)
    assert excinfo.value.reason is AuthFailureReason.NOT_CONFIGURED


def test_the_issuer_is_normalised_like_the_single_client_profile() -> None:
    settings = multi_client_settings_or_raise(ISSUER + "/", (CLAUDE_CLIENT,))
    assert settings.issuer_url == ISSUER
