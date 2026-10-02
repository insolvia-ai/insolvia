"""The client principal (ADR 0023), from outside the process.

The unit tier proves disjointness over the whole URL map against memory
stores. This proves it against the DEPLOYED configuration — a real access
token minted by the real pool through the PORTAL's app client, for the client
the seed bound to the fixture case — which is the one thing a mis-published
`/insolvia/<env>/api/auth-portal-client-id` (or none at all) would break:

  - the seeded client's portal token reaches /v1/portal/me — the firm's name
    and the case's chapter and stage — and 401s on /v1/me;
  - a staff token 401s on /v1/portal/me;
  - a revoked binding 403s (and the API writes a `denied` row — the access
    log is PutItem-only, so the row itself is not readable from here);
  - two bindings cannot both hold a debtor role;
  - a section the firm switches off is gone from /v1/portal/questionnaire
    and still on /v1/firm/questionnaire (PR 3's done-when) — and the firm's
    config is put back as it was found before the test returns.

WHO the client is comes from `seeds/<target>.json` — a fixture case's
`clients` — exactly as the staff people do. A fixture with no client skips
these tests and says so, rather than inventing one.

Scratch discipline: the revoke test re-invites the same address before it
returns, which re-binds the SAME subject (the refiled-case path), so the
seeded client is left as the seed left it — invited or active, bound to the
fixture case.
"""

from __future__ import annotations

from typing import Any

import pytest

from tests.integration.cognito_srp import SignInError, sign_in
from tests.integration.conftest import Api, _required


def _seeded_client(fixture: dict[str, Any]) -> dict[str, Any] | None:
    for entry in fixture.get("cases") or []:
        for client in entry.get("clients") or []:
            if client.get("handle") and client.get("email"):
                return {**client, "firmName": entry.get("firm")}
    return None


@pytest.fixture(scope="module")
def seeded_client(fixture: dict[str, Any]) -> dict[str, Any]:
    found = _seeded_client(fixture)
    if found is None:
        pytest.skip("the seed fixture binds no portal client to a case")
    return found


@pytest.fixture(scope="module")
def portal_token(seeded_client: dict[str, Any]) -> str:
    """The seeded client, signed in over SRP through the PORTAL's app client
    — the same pool the staff tokens come from, a different client id."""
    try:
        result = sign_in(
            pool_id=_required("INTEGRATION_AUTH_POOL_ID"),
            client_id=_required("INTEGRATION_AUTH_PORTAL_CLIENT_ID"),
            email=str(seeded_client["email"]),
            password=_required("E2E_TEST_USER_PASSWORD"),
        )
    except SignInError as refusal:
        pytest.fail(f"could not sign the seeded client in: {refusal}")
    return str(result["AccessToken"])


@pytest.fixture(scope="module")
def as_client(base_url: str, portal_token: str) -> Api:
    return Api(base_url, portal_token)


def _fixture_case_id(admin: Api, email: str) -> str:
    """The case the seed bound the client to — found through the invitation
    listing, since no portal response names a case (by design)."""
    cursor: str | None = None
    while True:
        params: dict[str, str] = {"limit": "50"}
        if cursor:
            params["cursor"] = cursor
        page = admin.get("/v1/cases", **params)
        for case in page.get("cases") or []:
            clients = admin.get(f"/v1/cases/{case['id']}/portal/clients")["clients"]
            if any(c["email"] == email and c["status"] != "revoked" for c in clients):
                return str(case["id"])
        cursor = page.get("nextCursor")
        if not cursor:
            pytest.fail(f"no case lists the seeded client {email} as live")


def test_the_seeded_client_reaches_the_portal(
    as_client: Api, seeded_client: dict[str, Any]
):
    me = as_client.get("/v1/portal/me")

    assert me["displayName"] == seeded_client["displayName"]
    assert me["roles"] == seeded_client["roles"]
    assert me["firm"] == {"name": seeded_client["firmName"]}
    assert "caseId" not in me
    # The public status, read through the binding: chapter and stage, and
    # nothing else of the case record (ADR 0023 decision 4).
    assert set(me["case"]) == {"chapter", "stage"}
    assert me["case"]["chapter"] in (7, 11, 12, 13)
    assert me["case"]["stage"] in ("intake", "ready_to_file", "filed")


def test_a_portal_token_is_refused_by_staff_routes(as_client: Api):
    as_client.get("/v1/me", expect=401)
    as_client.get("/v1/cases", expect=401)


def test_a_staff_token_is_refused_by_the_portal(admin: Api):
    admin.get("/v1/portal/me", expect=401)


def test_a_role_already_held_cannot_be_given_to_a_second_client(
    admin: Api, seeded_client: dict[str, Any]
):
    """Refused BEFORE an account is minted, so this leaves nothing behind."""
    case_id = _fixture_case_id(admin, str(seeded_client["email"]))

    admin.post(
        f"/v1/cases/{case_id}/portal/invitation",
        {
            "email": "integration-second-client@insolvia.test",
            "displayName": "Second Client",
            "roles": seeded_client["roles"],
        },
        expect=409,
    )


def test_a_revoked_client_is_refused_until_re_invited(
    admin: Api, as_client: Api, seeded_client: dict[str, Any]
):
    email = str(seeded_client["email"])
    case_id = _fixture_case_id(admin, email)
    subject = as_client.get("/v1/portal/me")["subject"]

    admin.delete(f"/v1/cases/{case_id}/portal/clients/{subject}", expect=200)
    try:
        # The same, still-unexpired access token: revocation is a store read
        # on every request, not a token lifetime.
        as_client.get("/v1/portal/me", expect=403)
    finally:
        rebound = admin.post(
            f"/v1/cases/{case_id}/portal/invitation",
            {
                "email": email,
                "displayName": seeded_client["displayName"],
                "roles": seeded_client["roles"],
            },
        )
        assert rebound["subject"] == subject

    as_client.get("/v1/portal/me")


# ── The questionnaire (ADR 0023 PR 3 / #362) ────────────────────


def _save_body(sections: list[dict[str, Any]]) -> dict[str, Any]:
    return {
        "sections": [
            {
                "id": section["id"],
                "enabled": section["enabled"],
                "instructions": section["instructions"],
            }
            for section in sections
        ]
    }


def test_a_section_switched_off_is_hidden_from_the_client_and_kept_for_staff(
    admin: Api, as_client: Api
):
    """The ADR's done-when for PR 3, against the deployed stack.

    It changes the FIRM's config — there is no scratch firm — so it puts the
    config back exactly as it found it before it returns: a firm that was on
    the defaults is reset (the item deleted), one that had saved is saved
    again with what it had."""
    before = admin.get("/v1/firm/questionnaire")
    target = next(
        (s["id"] for s in before["sections"] if s["switchable"] and s["enabled"]),
        None,
    )
    if target is None:
        pytest.skip("the firm already hides every switchable section")
    switched = [
        {**s, "enabled": False} if s["id"] == target else s for s in before["sections"]
    ]

    admin.put("/v1/firm/questionnaire", _save_body(switched))
    try:
        seen_by_client = [
            s["id"] for s in as_client.get("/v1/portal/questionnaire")["sections"]
        ]
        staff_view = admin.get("/v1/firm/questionnaire")["sections"]
    finally:
        if before["isDefault"]:
            admin.delete("/v1/firm/questionnaire", expect=200)
        else:
            admin.put("/v1/firm/questionnaire", _save_body(before["sections"]))

    assert target not in seen_by_client
    assert "personal_information" in seen_by_client
    assert [s["id"] for s in staff_view] == [s["id"] for s in before["sections"]]
    assert {s["id"]: s["enabled"] for s in staff_view}[target] is False
    after = admin.get("/v1/firm/questionnaire")
    assert after["isDefault"] == before["isDefault"]
    assert after["sections"] == before["sections"]


def test_the_questionnaire_routes_keep_the_two_principals_apart(
    admin: Api, as_client: Api
):
    as_client.get("/v1/firm/questionnaire", expect=401)
    admin.get("/v1/portal/questionnaire", expect=401)
