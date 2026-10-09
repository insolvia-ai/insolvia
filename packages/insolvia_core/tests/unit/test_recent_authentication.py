"""The fresh-sign-in check (ADR 0024, guardrails 1 and 2): `auth_time`
within the window, to the second, and nothing that merely LOOKS fresh.

All instants are fixed and far in the future, so no test reads the clock.
"""

from __future__ import annotations

import time

import jwt
import pytest
from cryptography.hazmat.primitives.asymmetric import rsa
from insolvia_core.auth import (
    CLOCK_SKEW_SECONDS,
    ReauthenticationRequiredError,
    multi_client_settings_or_raise,
    require_recent_authentication,
    settings_or_raise,
    verify_access_token,
    verify_access_token_for_clients,
)
from insolvia_core.errors import ForbiddenError

NOW = 4_102_444_800  # 2100-01-01T00:00:00Z
WINDOW = 300
ISSUER = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE00"
CLIENT = "exampleappclientid000000"
_PRIVATE_KEY = rsa.generate_private_key(public_exponent=65537, key_size=2048)


@pytest.mark.parametrize(
    "age",
    [0, 1, WINDOW - 1, WINDOW, -CLOCK_SKEW_SECONDS],
    ids=["just-now", "a-second-ago", "inside", "the-boundary", "skew-ahead"],
)
def test_a_sign_in_inside_the_window_is_accepted(age: int) -> None:
    signed_in = NOW - age
    assert (
        require_recent_authentication(signed_in, now=NOW, max_age_seconds=WINDOW)
        == signed_in
    )


@pytest.mark.parametrize(
    "authenticated_at",
    [None, NOW - WINDOW - 1, NOW - 86_400, NOW + CLOCK_SKEW_SECONDS + 1],
    ids=["no-auth-time", "a-second-too-old", "a-day-old", "from-the-future"],
)
def test_anything_else_requires_signing_in_again(authenticated_at: int | None) -> None:
    with pytest.raises(ReauthenticationRequiredError):
        require_recent_authentication(authenticated_at, now=NOW, max_age_seconds=WINDOW)


def test_it_is_a_403_kind_of_refusal_not_a_401() -> None:
    """The token is valid; a refresh cannot help (it keeps auth_time). So it
    is a ForbiddenError, which the API answers 403, never an
    AuthenticationError the client's refresh-and-retry loop would chase."""
    assert issubclass(ReauthenticationRequiredError, ForbiddenError)


@pytest.mark.parametrize(
    ("claim", "expected"),
    [
        (NOW - 120, NOW - 120),
        (None, None),
        (True, None),
        ("1700000000", None),
        (0, None),
    ],
    ids=["an-epoch", "absent", "a-bool", "a-string", "zero"],
)
def test_the_principal_carries_auth_time_only_when_it_is_a_time(
    claim: object, expected: int | None
) -> None:
    """Read from the VERIFIED claims of a really-signed token, on both
    verification profiles — anything that is not a positive integer is
    "unknown", which the check above treats as stale."""
    issued = int(time.time())
    claims: dict[str, object] = {
        "iss": ISSUER,
        "client_id": CLIENT,
        "token_use": "access",
        "sub": "00000000-0000-4000-8000-000000000001",
        "iat": issued,
        "exp": issued + 3600,
    }
    if claim is not None:
        claims["auth_time"] = claim
    token = jwt.encode(claims, _PRIVATE_KEY, algorithm="RS256")
    single = verify_access_token(
        token,
        signing_key=_PRIVATE_KEY.public_key(),
        settings=settings_or_raise(ISSUER, CLIENT),
    )
    multi = verify_access_token_for_clients(
        token,
        signing_key=_PRIVATE_KEY.public_key(),
        settings=multi_client_settings_or_raise(ISSUER, (CLIENT,)),
    )
    assert single.authenticated_at == expected
    assert multi.authenticated_at == expected


def test_a_window_of_zero_is_a_programming_error() -> None:
    with pytest.raises(ValueError, match="must be positive"):
        require_recent_authentication(NOW, now=NOW, max_age_seconds=0)
