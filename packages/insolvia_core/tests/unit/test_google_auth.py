"""The Google Workspace ID-token profile (issue #209).

Real RS256 over a test keypair, same approach as the API's auth tests: the
token is signed here and verified through the same code path production runs,
no socket involved. The cross-profile case at the bottom is the security
invariant the admin service exists on — a firm-pool-shaped token must never
verify as staff.

Every identifier below is obviously fake. This repo is public.
"""

from __future__ import annotations

import time
from typing import Any

import jwt
import pytest
from cryptography.hazmat.primitives.asymmetric import rsa
from insolvia_core.auth import (
    AuthenticationError,
    AuthFailureReason,
    GoogleAuthSettings,
    google_settings_or_raise,
    verify_google_id_token,
)

CLIENT_ID = "000000000000-fake.apps.googleusercontent.com"
OTHER_CLIENT_ID = "111111111111-other.apps.googleusercontent.com"
DOMAIN = "example-workspace.test"
SUBJECT = "100000000000000000001"

_KEY = rsa.generate_private_key(public_exponent=65537, key_size=2048)
_PUBLIC = _KEY.public_key()

SETTINGS = GoogleAuthSettings(client_id=CLIENT_ID, workspace_domain=DOMAIN)


def claims(**overrides: Any) -> dict[str, Any]:
    now = int(time.time())
    base: dict[str, Any] = {
        "iss": "https://accounts.google.com",
        "aud": CLIENT_ID,
        "sub": SUBJECT,
        "hd": DOMAIN,
        "email": "staff@example-workspace.test",
        "email_verified": True,
        "iat": now,
        "exp": now + 3600,
    }
    base.update(overrides)
    # None means "remove the claim entirely" — an absent claim and a null one
    # are different wire shapes, and absent is the one these tests need.
    return {key: value for key, value in base.items() if value is not None}


def token(**overrides: Any) -> str:
    return jwt.encode(claims(**overrides), _KEY, algorithm="RS256")


def verify(value: str) -> Any:
    return verify_google_id_token(value, signing_key=_PUBLIC, settings=SETTINGS)


def test_a_workspace_token_yields_the_staff_principal():
    principal = verify(token())
    assert principal.subject == SUBJECT
    assert principal.email == "staff@example-workspace.test"


def test_the_legacy_issuer_form_is_accepted():
    """Google documents both forms; a verifier pinned to one logs staff out
    the day Google flips which it mints."""
    assert verify(token(iss="accounts.google.com")).subject == SUBJECT


@pytest.mark.parametrize(
    ("override", "reason"),
    [
        ({"iss": "https://accounts.example.test"}, AuthFailureReason.INVALID_ISSUER),
        ({"aud": OTHER_CLIENT_ID}, AuthFailureReason.INVALID_CLIENT),
        ({"hd": "personal-gmail.test"}, AuthFailureReason.INVALID_CLAIMS),
        # A personal Gmail carries no hd at all — the comparison must read
        # absence as refusal, not as a special case.
        ({"hd": None}, AuthFailureReason.INVALID_CLAIMS),
        ({"email_verified": False}, AuthFailureReason.INVALID_CLAIMS),
        ({"email": None}, AuthFailureReason.INVALID_CLAIMS),
        ({"exp": int(time.time()) - 60}, AuthFailureReason.EXPIRED),
    ],
    ids=[
        "issuer",
        "audience",
        "wrong-hd",
        "no-hd",
        "unverified",
        "no-email",
        "expired",
    ],
)
def test_each_broken_claim_is_refused(override: dict[str, Any], reason):
    with pytest.raises(AuthenticationError) as caught:
        verify(token(**override))
    assert caught.value.reason == reason


def test_a_cognito_shaped_token_is_refused():
    """THE CROSS-ISSUER INVARIANT. A firm-pool access token carries token_use
    and client_id and NO aud — against the Google profile it must die on the
    missing-claims check, never verify. This is the boundary the admin
    service's whole trust model rests on."""
    cognito_shaped = jwt.encode(
        {
            "iss": "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_fake",
            "sub": "00000000-0000-4000-8000-000000000001",
            "token_use": "access",
            "client_id": "fakepoolclientid",
            "iat": int(time.time()),
            "exp": int(time.time()) + 3600,
        },
        _KEY,
        algorithm="RS256",
    )
    with pytest.raises(AuthenticationError):
        verify(cognito_shaped)


def test_missing_settings_fail_closed():
    for pair in ((None, DOMAIN), (CLIENT_ID, None), (None, None)):
        with pytest.raises(AuthenticationError) as caught:
            google_settings_or_raise(*pair)
        assert caught.value.reason == AuthFailureReason.NOT_CONFIGURED
