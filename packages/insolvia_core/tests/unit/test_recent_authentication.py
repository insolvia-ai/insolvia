"""The fresh-sign-in check (ADR 0024, guardrails 1 and 2): `auth_time`
within the window, to the second, and nothing that merely LOOKS fresh.

All instants are fixed and far in the future, so no test reads the clock.
"""

from __future__ import annotations

import pytest
from insolvia_core.auth import (
    AUTH_TIME_SKEW_SECONDS,
    ReauthenticationRequiredError,
    require_recent_authentication,
)
from insolvia_core.errors import ForbiddenError

NOW = 4_102_444_800  # 2100-01-01T00:00:00Z
WINDOW = 300


@pytest.mark.parametrize(
    "age",
    [0, 1, WINDOW - 1, WINDOW, -AUTH_TIME_SKEW_SECONDS],
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
    [None, NOW - WINDOW - 1, NOW - 86_400, NOW + AUTH_TIME_SKEW_SECONDS + 1],
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


def test_a_window_of_zero_is_a_programming_error() -> None:
    with pytest.raises(ValueError, match="must be positive"):
        require_recent_authentication(NOW, now=NOW, max_age_seconds=0)
