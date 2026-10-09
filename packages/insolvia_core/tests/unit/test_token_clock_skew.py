"""Clock skew on the Cognito access-token profile: a token minted a moment
ahead of this verifier's clock is accepted, one minted well ahead is not, and
expiry is extended by the leeway and not a second more.

PyJWT reads the real clock, so every token here is built relative to
`time.time()` and every margin is tens of seconds wide — no test sits on a
boundary a slow run could cross. Both verification paths are exercised,
because they share `_decode_verified_claims` and must stay identical there.
Every id below is obviously fake.
"""

from __future__ import annotations

import time
from collections.abc import Callable

import jwt
import pytest
from cryptography.hazmat.primitives.asymmetric import rsa
from insolvia_core.auth import (
    CLOCK_SKEW_SECONDS,
    AuthenticationError,
    AuthFailureReason,
    Principal,
    multi_client_settings_or_raise,
    settings_or_raise,
    verify_access_token,
    verify_access_token_for_clients,
)

ISSUER = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE00"
CLIENT = "exampleappclientid000000"
SUBJECT = "00000000-0000-4000-8000-000000000001"

_PRIVATE_KEY = rsa.generate_private_key(public_exponent=65537, key_size=2048)
_PUBLIC_KEY = _PRIVATE_KEY.public_key()


def _single(token: str) -> Principal:
    return verify_access_token(
        token,
        signing_key=_PUBLIC_KEY,
        settings=settings_or_raise(ISSUER, CLIENT),
    )


def _allowlist(token: str) -> Principal:
    return verify_access_token_for_clients(
        token,
        signing_key=_PUBLIC_KEY,
        settings=multi_client_settings_or_raise(ISSUER, (CLIENT,)),
    )


VERIFIERS = pytest.mark.parametrize(
    "verify", [_single, _allowlist], ids=["single-client", "allowlist"]
)


def make_token(
    *,
    issued_in: int = 0,
    expires_in: int = 3600,
    not_before_in: int | None = None,
) -> str:
    """A valid access token whose times are offsets from now, in seconds."""
    now = int(time.time())
    claims: dict[str, object] = {
        "iss": ISSUER,
        "client_id": CLIENT,
        "token_use": "access",
        "sub": SUBJECT,
        "iat": now + issued_in,
        "exp": now + expires_in,
    }
    if not_before_in is not None:
        claims["nbf"] = now + not_before_in
    return jwt.encode(claims, _PRIVATE_KEY, algorithm="RS256")


def test_the_leeway_is_a_minute() -> None:
    # Pinned so widening it is a reviewed change: every second added here is
    # a second an expired token keeps working.
    assert CLOCK_SKEW_SECONDS == 60


@VERIFIERS
@pytest.mark.parametrize(
    "token_kwargs",
    [{"issued_in": 30}, {"issued_in": 30, "not_before_in": 30}],
    ids=["iat-30s-ahead", "iat-and-nbf-30s-ahead"],
)
def test_a_token_minted_slightly_ahead_of_this_clock_is_accepted(
    verify: Callable[[str], Principal], token_kwargs: dict[str, int]
) -> None:
    assert verify(make_token(**token_kwargs)).subject == SUBJECT


@VERIFIERS
@pytest.mark.parametrize(
    "token_kwargs",
    [{"issued_in": 300}, {"not_before_in": 300}],
    ids=["iat-5min-ahead", "nbf-5min-ahead"],
)
def test_a_token_from_well_in_the_future_is_refused(
    verify: Callable[[str], Principal], token_kwargs: dict[str, int]
) -> None:
    with pytest.raises(AuthenticationError):
        verify(make_token(**token_kwargs))


@VERIFIERS
@pytest.mark.parametrize(
    "expires_in",
    [-(CLOCK_SKEW_SECONDS + 30), -3600],
    ids=["just-past-the-leeway", "an-hour-ago"],
)
def test_an_expired_token_is_still_refused(
    verify: Callable[[str], Principal], expires_in: int
) -> None:
    with pytest.raises(AuthenticationError) as excinfo:
        verify(make_token(issued_in=-7200, expires_in=expires_in))
    assert excinfo.value.reason is AuthFailureReason.EXPIRED


@VERIFIERS
def test_expiry_is_extended_by_the_leeway_and_no_more(
    verify: Callable[[str], Principal],
) -> None:
    # The cost of the leeway, stated rather than discovered: a token whose
    # `exp` is inside it still verifies. The test above is the bound.
    token = make_token(issued_in=-3600, expires_in=-(CLOCK_SKEW_SECONDS - 30))
    assert verify(token).subject == SUBJECT
