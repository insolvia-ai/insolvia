"""A real token from the real pool, verified by the running API.

`/v1/me` is the app's own "is my token still good?" probe, and the one route
that answers for a signed-in caller with no firm — so it is where a broken
seed (an account with no membership) shows up as a missing `firm` block
rather than as a 403 everywhere else.
"""

from __future__ import annotations

from typing import Any

from tests.integration.conftest import Api


def test_every_seeded_person_resolves_to_the_firm_the_fixture_puts_them_in(
    as_user, people: dict[str, dict[str, Any]]
):
    for handle, person in people.items():
        me = as_user(handle).get("/v1/me")

        assert me["subject"], f"{handle}: the access token carried no subject"
        assert "firm" in me, (
            f"{handle} signed in but resolves to no firm — the seed step did not "
            "converge, or the pool was recreated and the firm rows name an old sub"
        )
        assert me["firm"]["name"] == person["firmName"]
        assert me["firm"]["isAdmin"] is bool(person.get("isAdmin"))
        assert me["firm"]["role"] == person["role"]


def test_no_token_is_a_401_not_a_500(anonymous: Api):
    """Fail closed. A protected route with no header answers 401 with the API's
    own body — and the status is what matters: a 500 here would mean the
    deployed configuration lost its auth settings and the fail-closed default
    in api/auth.py is what is answering, not verification."""
    anonymous.get("/v1/me", expect=401)


def test_a_forged_token_is_a_401(base_url: str):
    """Signed by nobody the pool knows: the JWKS fetch and the signature check
    both have to actually run for this to be refused."""
    forged = (
        "eyJhbGciOiJSUzI1NiIsImtpZCI6Im5vYm9keSJ9."
        "eyJzdWIiOiJub2JvZHkiLCJ0b2tlbl91c2UiOiJhY2Nlc3MifQ."
        "bm90LWEtc2lnbmF0dXJl"
    )
    Api(base_url, forged).get("/v1/me", expect=401)
