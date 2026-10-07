"""The credential vault over HTTP (ADR 0024, guardrail 3): enrol seals into
the real vault table under the real vault key, status shows no secret, and
revoke destroys.

What only this tier can show: that the API's own principal may GenerateDataKey
under the vault key with the vault's encryption context (on staging, the
Lambda's role through the key policy's AllowApiSeal; on dev, the developer
through the root delegation) and may write, query and delete rows in the
dedicated table. That the same principal is REFUSED Decrypt is proved by
services/api/scripts/dev-filing-vault-proof.sh — there is, deliberately, no
route that could even try.

FAKE VALUES ONLY: the login is FAKE-ECF-USER plus a run suffix, the
password is an obviously fake literal, and the TOTP seed is minted at test
time. Every credential this module enrols is revoked in teardown, and any a
crashed earlier run left behind (by the FAKE-ECF-USER prefix) is revoked
before it starts — the scratch discipline conftest describes.
"""

from __future__ import annotations

import base64
import os
from collections.abc import Iterator
from typing import Any

import pytest

from tests.integration.conftest import Api

PATH = "/v1/me/filing-credentials"
LOGIN_PREFIX = "FAKE-ECF-USER"
PASSWORD = "FAKE-ECF-PASSWORD-not-a-real-one"


def _revoke_leftovers(api: Api) -> None:
    for credential in api.get(PATH)["credentials"]:
        if credential["login"].startswith(LOGIN_PREFIX):
            api.delete(f"{PATH}/{credential['id']}", expect=(204, 404))


AUTHORIZATION = "/v1/me/filing-authorization"


@pytest.fixture
def admin(fresh_as_user) -> Iterator[Api]:
    """The seeded admin — admins hold every feature, `electronic_filing`
    included, which is hidden for everybody else by default.

    Signed in JUST NOW, and holding a current signed authorization
    (guardrail 2 is a precondition of enrolment — test_filing_authorization
    owns proving that). If this fixture had to sign, it withdraws on the way
    out, so the account is left as it was found."""
    api = fresh_as_user("admin")
    signed_here = False
    if not api.get(AUTHORIZATION)["current"]:
        text = api.get(AUTHORIZATION)["text"]
        api.post(
            AUTHORIZATION,
            {"text_version": text["version"], "text_digest": text["digest"]},
            expect=201,
        )
        signed_here = True
    _revoke_leftovers(api)
    yield api
    _revoke_leftovers(api)
    if signed_here:
        api.delete(AUTHORIZATION, expect=200)


def _enrolment() -> dict[str, Any]:
    return {
        "login": f"{LOGIN_PREFIX}-{os.urandom(3).hex()}",
        "password": PASSWORD,
        "totp_seed": base64.b32encode(os.urandom(20)).decode("ascii"),
    }


def test_enrol_seals_status_hides_the_secret_and_revoke_destroys(admin: Api):
    body = _enrolment()
    enrolled = admin.post(PATH, body)

    assert enrolled["login"] == body["login"]
    assert enrolled["status"] == "active"
    assert set(enrolled) == {
        "id",
        "login",
        "courts",
        "status",
        "created_at",
        "updated_at",
    }

    listed = admin.get(PATH)["credentials"]
    assert enrolled in listed
    flat = repr(listed)
    assert body["password"] not in flat
    assert body["totp_seed"] not in flat

    admin.delete(f"{PATH}/{enrolled['id']}")
    assert enrolled not in admin.get(PATH)["credentials"]
    admin.delete(f"{PATH}/{enrolled['id']}", expect=404)


def test_another_firms_admin_cannot_see_or_revoke_it(
    admin: Api, as_user, people: dict[str, dict[str, Any]]
):
    if "outsider" not in people:
        pytest.skip("this environment's seed has no second firm")
    enrolled = admin.post(PATH, _enrolment())
    outsider = as_user("outsider")
    assert enrolled not in outsider.get(PATH)["credentials"]
    outsider.delete(f"{PATH}/{enrolled['id']}", expect=404)
    assert enrolled in admin.get(PATH)["credentials"]


def test_without_the_feature_it_is_a_403(as_user, people: dict[str, dict[str, Any]]):
    if "paralegal" not in people:
        pytest.skip("this environment's seed has no non-admin member")
    as_user("paralegal").get(PATH, expect=403)
