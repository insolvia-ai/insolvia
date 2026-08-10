"""The six admin routes, through the app over memory adapters.

Two invariants get pride of place: the CROSS-ISSUER 401 (a firm-pool-shaped
token must die in verification — the trust boundary the whole service rests
on), and the AUDIT ROW on every mutation (#178's "record who provisioned
what" — asserted per route, not assumed).

Every identifier below is obviously fake. This repo is public.
"""

from __future__ import annotations

import time

from insolvia_core.firms import create_firm, parse_firm_creation

from .conftest import CLIENT_ID, STAFF_EMAIL, STAFF_SUB, sign


def provision(
    client, staff_headers, name="Example & Partners", email="admin@example.test"
):
    return client.post(
        "/v1/firms",
        json={"name": name, "admin": {"email": email, "displayName": "Alice Attorney"}},
        headers=staff_headers,
    )


# ── The trust boundary ──────────────────────────────────────────────


def test_no_token_is_401(client):
    assert client.get("/v1/firms").status_code == 401


def test_a_firm_pool_shaped_token_is_401(client):
    """THE CROSS-ISSUER INVARIANT. This token is exactly what the tenant API
    accepts — token_use, client_id, a Cognito issuer, no aud, no hd — signed
    by a key this service's provider actually serves, so what refuses it is
    the Google profile's claim checks and nothing weaker."""
    cognito_shaped = sign(
        {
            "iss": "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_fake",
            "sub": "00000000-0000-4000-8000-000000000001",
            "token_use": "access",
            "client_id": "fakepoolclientid",
            "iat": int(time.time()),
            "exp": int(time.time()) + 3600,
        }
    )
    response = client.get(
        "/v1/firms", headers={"Authorization": f"Bearer {cognito_shaped}"}
    )
    assert response.status_code == 401


def test_a_personal_gmail_token_is_401(client):
    """Internal-client marking is Google's gate; the hd check is ours. A
    verified personal account carries NO hd claim at all, and absence must
    read as refusal on our side too."""
    now = int(time.time())
    personal = sign(
        {
            "iss": "https://accounts.google.com",
            "aud": CLIENT_ID,
            "sub": "200000000000000000002",
            "email": "someone@personal-mail.test",
            "email_verified": True,
            "iat": now,
            "exp": now + 3600,
        }
    )
    response = client.get("/v1/firms", headers={"Authorization": f"Bearer {personal}"})
    assert response.status_code == 401


def test_health_is_public(client):
    response = client.get("/health")
    assert response.status_code == 200
    assert response.get_json()["environment"] == "local"


# ── Provision ───────────────────────────────────────────────────────


def test_provisioning_creates_pool_account_rows_and_audit(
    client, staff_headers, firm_store, user_directory, audit_log
):
    response = provision(client, staff_headers)
    assert response.status_code == 201
    body = response.get_json()

    # Provenance is the staff caller, straight off the verified token.
    assert body["firm"]["createdBy"] == STAFF_SUB
    assert body["firm"]["createdByEmail"] == STAFF_EMAIL
    assert body["firm"]["status"] == "active"

    # The pool account exists and the row is keyed on ITS subject.
    assert "admin@example.test" in user_directory.subjects
    assert body["admin"]["subject"] == user_directory.subjects["admin@example.test"]

    # First administrator BY CONSTRUCTION.
    assert body["admin"]["isAdmin"] is True

    stored = firm_store.get_user(body["firm"]["id"], body["admin"]["subject"])
    assert stored is not None
    assert stored.is_admin

    # The row #178 demanded.
    assert [event.action for event in audit_log.events] == ["firm.provision"]
    event = audit_log.events[0]
    assert event.principal == STAFF_SUB
    assert event.principal_email == STAFF_EMAIL
    assert "admin@example.test" in event.detail


def test_a_duplicate_admin_address_is_409_and_writes_nothing(
    client, staff_headers, firm_store, user_directory, audit_log
):
    """Pool-account-first ordering observed from outside: the Cognito refusal
    arrives before any firm exists, so a retry with the right address starts
    clean rather than beside a half-provisioned firm."""
    user_directory.create_user("admin@example.test")
    response = provision(client, staff_headers)
    assert response.status_code == 409
    assert firm_store.firms == {}
    assert audit_log.events == []


def test_provisioning_without_an_admin_is_a_field_error(client, staff_headers):
    response = client.post("/v1/firms", json={"name": "Example"}, headers=staff_headers)
    assert response.status_code == 400
    assert "admin" in response.get_json()["fields"]


def test_the_body_cannot_demote_the_first_admin(client, staff_headers, firm_store):
    """isAdmin: false in the payload is overridden, not honoured — a firm
    whose only user cannot administer it is bricked at birth."""
    response = client.post(
        "/v1/firms",
        json={
            "name": "Example",
            "admin": {
                "email": "a@example.test",
                "displayName": "A",
                "isAdmin": False,
            },
        },
        headers=staff_headers,
    )
    assert response.status_code == 201
    assert response.get_json()["admin"]["isAdmin"] is True


# ── List / get ──────────────────────────────────────────────────────


def test_the_firm_list_carries_counts_and_provenance(client, staff_headers, firm_store):
    provision(client, staff_headers, name="Beta LLP", email="b@example.test")
    provision(client, staff_headers, name="Alpha & Co", email="a@example.test")

    response = client.get("/v1/firms", headers=staff_headers)
    firms = response.get_json()["firms"]
    assert [firm["name"] for firm in firms] == ["Alpha & Co", "Beta LLP"]
    assert all(firm["userCount"] == 1 for firm in firms)
    assert all(firm["createdBy"] == STAFF_SUB for firm in firms)


def test_a_seeded_firm_reads_back_authorless(client, staff_headers, firm_store):
    """Pre-portal rows carry no provenance; the list must say null, not
    invent an author."""
    firm_store.create_firm(create_firm(parse_firm_creation({"name": "Seeded LLP"})))
    firms = client.get("/v1/firms", headers=staff_headers).get_json()["firms"]
    assert firms[0]["createdBy"] is None


def test_an_unknown_firm_is_404(client, staff_headers):
    assert client.get("/v1/firms/nope", headers=staff_headers).status_code == 404


# ── Suspend / reactivate ────────────────────────────────────────────


def test_suspend_then_reactivate_audits_each_transition(
    client, staff_headers, audit_log
):
    firm_id = provision(client, staff_headers).get_json()["firm"]["id"]

    suspended = client.patch(
        f"/v1/firms/{firm_id}", json={"status": "suspended"}, headers=staff_headers
    )
    assert suspended.status_code == 200
    assert suspended.get_json()["status"] == "suspended"

    reactivated = client.patch(
        f"/v1/firms/{firm_id}", json={"status": "active"}, headers=staff_headers
    )
    assert reactivated.get_json()["status"] == "active"

    assert [event.action for event in audit_log.events] == [
        "firm.provision",
        "firm.suspend",
        "firm.reactivate",
    ]


def test_an_unknown_status_is_a_field_error(client, staff_headers):
    firm_id = provision(client, staff_headers).get_json()["firm"]["id"]
    response = client.patch(
        f"/v1/firms/{firm_id}", json={"status": "disabled"}, headers=staff_headers
    )
    assert response.status_code == 400
    assert "status" in response.get_json()["fields"]


def test_suspending_an_unknown_firm_is_404(client, staff_headers, audit_log):
    response = client.patch(
        "/v1/firms/nope", json={"status": "suspended"}, headers=staff_headers
    )
    assert response.status_code == 404
    assert audit_log.events == []


# ── A firm's users ──────────────────────────────────────────────────


def test_a_firms_users_are_admin_shaped(client, staff_headers):
    firm_id = provision(client, staff_headers).get_json()["firm"]["id"]
    users = client.get(f"/v1/firms/{firm_id}/users", headers=staff_headers).get_json()[
        "users"
    ]
    assert len(users) == 1
    # The full admin shape — email and permissions included, per
    # firm_user_json's docstring on who this reader is.
    assert users[0]["email"] == "admin@example.test"
    assert "permissions" in users[0]


# ── Resend invite ───────────────────────────────────────────────────


def test_resend_reinvites_and_audits(client, staff_headers, user_directory, audit_log):
    body = provision(client, staff_headers).get_json()
    firm_id, subject = body["firm"]["id"], body["admin"]["subject"]

    response = client.post(
        f"/v1/firms/{firm_id}/users/{subject}/resend-invite", headers=staff_headers
    )
    assert response.status_code == 204
    assert user_directory.resent == ["admin@example.test"]
    assert audit_log.events[-1].action == "invite.resend"
    assert audit_log.events[-1].detail == "admin@example.test"


def test_resend_is_firm_scoped(client, staff_headers, firm_store):
    """A real subject under the WRONG firm id answers the same 404 an unknown
    one does — cross-tenant reach never becomes cross-tenant confusion."""
    body = provision(client, staff_headers).get_json()
    other = create_firm(parse_firm_creation({"name": "Other LLP"}))
    firm_store.create_firm(other)
    response = client.post(
        f"/v1/firms/{other.id}/users/{body['admin']['subject']}/resend-invite",
        headers=staff_headers,
    )
    assert response.status_code == 404


def test_resending_to_a_confirmed_user_is_409(
    client, staff_headers, user_directory, audit_log
):
    body = provision(client, staff_headers).get_json()
    user_directory.confirmed.add("admin@example.test")
    response = client.post(
        f"/v1/firms/{body['firm']['id']}/users/{body['admin']['subject']}/resend-invite",
        headers=staff_headers,
    )
    assert response.status_code == 409
    assert [event.action for event in audit_log.events] == ["firm.provision"]
