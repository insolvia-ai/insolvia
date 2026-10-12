"""The written authorization over HTTP (ADR 0024, guardrail 2), against the
real vault table and the real pool: enrolment without a signature fails,
signing after a fresh sign-in makes it work, and withdrawing destroys the
credential and refuses the next enrolment.

What this tier adds over the unit tests: the authorization items really
land in `insolvia-<env>-filing-credentials` under the API's own grant (no
IAM change was made for them), and the `auth_time` the check reads is the
one the REAL POOL put in a real token — including the property the whole
design leans on, that a refresh keeps it (`test_a_refresh_keeps_auth_time`).

That the WORKER's next open fails after a withdrawal is not observable over
HTTP — there is, deliberately, no route that opens a credential. The dev
proof (services/api/scripts/dev-filing-vault-proof.sh) shows it under the
worker's real role.

WHAT IT CANNOT DO: type a password into the managed login page, so the
app's `prompt=login` round trip is not exercised here. `fresh_sign_in` is
a new SRP sign-in instead — the same act as far as `auth_time` is
concerned (a password verified by the pool, now).

FAKE VALUES ONLY, as in test_filing_credentials: FAKE-ECF-USER logins, a
fake password literal, a seed minted per run. The admin's authorization is
withdrawn at the start of each test (destroying only scratch credentials)
and left withdrawn.
"""

from __future__ import annotations

import base64
import os
import time
from collections.abc import Iterator
from typing import Any

import boto3
import jwt
import pytest
from botocore import UNSIGNED
from botocore.config import Config
from insolvia_core.filing_authorization import SIGNATURE_MAX_AGE_SECONDS

from tests.integration.conftest import Api

AUTHORIZATION = "/v1/me/filing-authorization"
CREDENTIALS = "/v1/me/filing-credentials"
LOGIN_PREFIX = "FAKE-ECF-USER"
PASSWORD = "FAKE-ECF-PASSWORD-not-a-real-one"


def _enrolment() -> dict[str, Any]:
    return {
        "login": f"{LOGIN_PREFIX}-{os.urandom(3).hex()}",
        "password": PASSWORD,
        "totp_seed": base64.b32encode(os.urandom(20)).decode("ascii"),
    }


def _signature(api: Api) -> dict[str, str]:
    text = api.get(AUTHORIZATION)["text"]
    return {"text_version": text["version"], "text_digest": text["digest"]}


def _claims(token: str) -> dict[str, Any]:
    """Read a token the REAL pool just issued to this test. Not verified —
    this reads what Cognito wrote; the API's verification is what the
    other tests exercise."""
    return dict(jwt.decode(token, options={"verify_signature": False}))


@pytest.fixture
def unsigned_admin(fresh_as_user) -> Iterator[Api]:
    """The seeded admin, signed in just now, with NO authorization in force
    (any a previous run left is withdrawn — which also destroys any scratch
    credential it left)."""
    api = fresh_as_user("admin")
    api.delete(AUTHORIZATION, expect=(200, 404))
    yield api
    api.delete(AUTHORIZATION, expect=(200, 404))


def test_enrolment_without_a_signed_authorization_fails(unsigned_admin: Api):
    status = unsigned_admin.get(AUTHORIZATION)
    assert status["current"] is False
    assert status["signature"] is None

    refused = unsigned_admin.post(CREDENTIALS, _enrolment(), expect=403)

    assert refused["error"] == "ForbiddenError"
    assert unsigned_admin.get(CREDENTIALS)["credentials"] == []


def test_signing_after_a_fresh_sign_in_then_enrolment_works(unsigned_admin: Api):
    signed = unsigned_admin.post(AUTHORIZATION, _signature(unsigned_admin))
    assert signed["current"] is True
    assert signed["signature"]["text_version"] == signed["text"]["version"]
    assert signed["signature"]["text_digest"] == signed["text"]["digest"]

    enrolled = unsigned_admin.post(CREDENTIALS, _enrolment())

    assert enrolled["status"] == "active"
    assert enrolled in unsigned_admin.get(CREDENTIALS)["credentials"]


def test_withdrawing_revokes_the_credential_and_refuses_the_next_enrolment(
    unsigned_admin: Api,
):
    unsigned_admin.post(AUTHORIZATION, _signature(unsigned_admin))
    enrolled = unsigned_admin.post(CREDENTIALS, _enrolment())

    withdrawn = unsigned_admin.delete(AUTHORIZATION, expect=200)

    assert withdrawn["credentials_revoked"] >= 1
    assert enrolled not in unsigned_admin.get(CREDENTIALS)["credentials"]
    unsigned_admin.delete(f"{CREDENTIALS}/{enrolled['id']}", expect=404)
    assert unsigned_admin.get(AUTHORIZATION)["current"] is False
    unsigned_admin.post(CREDENTIALS, _enrolment(), expect=403)


def test_a_refresh_keeps_auth_time(fresh_sign_in):
    """The property `require_recent_authentication` depends on: a token a
    refresh mints carries the ORIGINAL sign-in's `auth_time`, so keeping a
    session alive never makes it fresh. Cognito's documented behaviour,
    checked against this environment's real pool and app client (refresh
    token rotation on, so the refresh is GetTokensFromRefreshToken)."""
    signed_in = fresh_sign_in("admin")
    pool_id = os.environ["INTEGRATION_AUTH_POOL_ID"]
    cognito = boto3.client(
        "cognito-idp",
        region_name=pool_id.split("_", 1)[0],
        config=Config(signature_version=UNSIGNED),
    )
    time.sleep(1.5)  # so the refreshed token's iat is a later second

    refreshed = cognito.get_tokens_from_refresh_token(
        RefreshToken=signed_in["RefreshToken"],
        ClientId=os.environ["INTEGRATION_AUTH_CLIENT_ID"],
    )["AuthenticationResult"]

    before = _claims(signed_in["AccessToken"])
    after = _claims(refreshed["AccessToken"])
    assert after["iat"] > before["iat"]
    assert after["auth_time"] == before["auth_time"]


def test_a_sign_in_older_than_the_window_cannot_sign(
    as_user, fresh_as_user, tokens: dict[str, str]
):
    """The session-scoped token was minted when the run started. When the
    run has been going longer than the signature window, it is a real stale
    sign-in, and it is refused; on a quicker run there is nothing stale to
    present yet, and the unit tier's boundary tests stand alone."""
    age = time.time() - _claims(tokens["admin"])["auth_time"]
    if age <= SIGNATURE_MAX_AGE_SECONDS + 5:
        pytest.skip(
            f"the session's sign-in is only {int(age)}s old; a stale one needs "
            f"more than {SIGNATURE_MAX_AGE_SECONDS}s"
        )
    fresh_as_user("admin").delete(AUTHORIZATION, expect=(200, 404))
    stale = as_user("admin")

    refused = stale.post(AUTHORIZATION, _signature(stale), expect=403)

    assert refused["error"] == "ReauthenticationRequired"
    assert stale.get(AUTHORIZATION)["current"] is False
