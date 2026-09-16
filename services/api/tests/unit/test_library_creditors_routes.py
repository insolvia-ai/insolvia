"""The firm's reusable creditor library (issue 13.9 / #350):
`/v1/firm/creditors`, gated by `creditor_library`.

The two things this file exists to hold down, the same pair
`test_firm_routes.py` holds for the staff list:

  1. EVERY ROUTE IS SCOPED TO THE CALLER'S FIRM — no firm id in any path, and
     an id belonging to another firm's library answers the same 404 as one
     that never existed.
  2. THE PERMISSION LEVELS GATE WHAT THEY SAY THEY GATE — `view_only` reads,
     `add_edit` writes, `hidden` gets neither.

Tokens are signed for real, as everywhere else. Every identifier below is
obviously fake. This repo is public.
"""

from __future__ import annotations

import time

import jwt
import pytest
from cryptography.hazmat.primitives.asymmetric import rsa
from insolvia_api.adapters.memory.mailer_client import InMemoryMailerClient
from insolvia_api.adapters.memory.waitlist_store import MemoryWaitlistStore
from insolvia_api.api.app_factory import create_app
from insolvia_api.api.dependencies import ApiDependencies
from insolvia_api.core.config import load_config
from insolvia_core.adapters.memory.access_log import MemoryAccessLog
from insolvia_core.adapters.memory.case_store import MemoryCaseStore
from insolvia_core.adapters.memory.firm_store import MemoryFirmStore
from insolvia_core.adapters.memory.jwks_provider import StaticJwksProvider
from insolvia_core.adapters.memory.user_directory import MemoryUserDirectory
from insolvia_core.firms import (
    ADD_EDIT,
    CREDITOR_LIBRARY,
    HIDDEN,
    VIEW_ONLY,
    Firm,
    FirmUser,
)

ISSUER = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE00"
CLIENT_ID = "exampleappclientid000000"
KID = "test-key-1"

FIRM_A = "00000000-0000-4000-8000-00000000f18a"
FIRM_B = "00000000-0000-4000-8000-00000000f18b"

# A firm A admin (full add_edit), someone view_only on the library, someone
# with the feature hidden, and a firm B admin — the same shape
# test_firm_routes.py uses for its cross-tenant checks.
ADMIN = "00000000-0000-4000-8000-00000000a11c"
VIEWER = "00000000-0000-4000-8000-00000000da4a"
BLOCKED = "00000000-0000-4000-8000-00000000b0b0"
OTHER_ADMIN = "00000000-0000-4000-8000-0000000ca201"

_PRIVATE_KEY = rsa.generate_private_key(public_exponent=65537, key_size=2048)
_PUBLIC_KEY = _PRIVATE_KEY.public_key()


def auth(subject: str) -> dict[str, str]:
    now = int(time.time())
    token = jwt.encode(
        {
            "iss": ISSUER,
            "client_id": CLIENT_ID,
            "token_use": "access",
            "sub": subject,
            "username": subject,
            "iat": now,
            "auth_time": now,
            "exp": now + 3600,
        },
        _PRIVATE_KEY,  # type: ignore[arg-type]
        algorithm="RS256",
        headers={"kid": KID},
    )
    return {"Authorization": f"Bearer {token}"}


def firm(firm_id: str, name: str) -> Firm:
    return Firm(
        id=firm_id,
        name=name,
        status="active",
        created_at="2026-01-01T00:00:00.000Z",
        updated_at="2026-01-01T00:00:00.000Z",
    )


def member(
    subject: str, firm_id: str, permission: str, *, is_admin: bool = False
) -> FirmUser:
    return FirmUser(
        firm_id=firm_id,
        subject=subject,
        email=f"{subject[-4:]}@example.test",
        first_name="Person",
        last_name=subject[-4:],
        role="attorney",
        is_admin=is_admin,
        access_all_cases=False,
        permissions={CREDITOR_LIBRARY: permission},
        status="active",
        created_at="2026-01-01T00:00:00.000Z",
        updated_at="2026-01-01T00:00:00.000Z",
    )


@pytest.fixture
def firms():
    store = MemoryFirmStore()
    store.create_firm(firm(FIRM_A, "Example & Partners"))
    store.create_firm(firm(FIRM_B, "Other Firm LLP"))
    store.add_user(member(ADMIN, FIRM_A, ADD_EDIT, is_admin=True))
    store.add_user(member(VIEWER, FIRM_A, VIEW_ONLY))
    store.add_user(member(BLOCKED, FIRM_A, HIDDEN))
    store.add_user(member(OTHER_ADMIN, FIRM_B, ADD_EDIT, is_admin=True))
    return store


@pytest.fixture
def client(firms):
    app = create_app(
        ApiDependencies(
            config=load_config(
                {
                    "INSOLVIA_ENV": "local",
                    "AUTH_ISSUER_URL": ISSUER,
                    "AUTH_CLIENT_ID": CLIENT_ID,
                }
            ),
            waitlist_store=MemoryWaitlistStore(),
            mailer=InMemoryMailerClient(),
            jwks_provider=StaticJwksProvider({KID: _PUBLIC_KEY}),
            case_store=MemoryCaseStore(),
            access_log=MemoryAccessLog(),
            firm_store=firms,
            user_directory=MemoryUserDirectory(),
        )
    )
    return app.test_client()


def add(client, name="Acme Collections", subject=ADMIN, **overrides):
    body = {"name": name, **overrides}
    return client.post("/v1/firm/creditors", json=body, headers=auth(subject))


# ── Gating ────────────────────────────────────────────────────────


def test_view_only_can_list_but_not_add(client):
    listed = client.get("/v1/firm/creditors", headers=auth(VIEWER))
    assert listed.status_code == 200

    created = add(client, subject=VIEWER)
    assert created.status_code == 403


def test_hidden_cannot_even_list(client):
    assert client.get("/v1/firm/creditors", headers=auth(BLOCKED)).status_code == 403


def test_no_auth_is_401(client):
    assert client.get("/v1/firm/creditors").status_code == 401


# ── CRUD ──────────────────────────────────────────────────────────


def test_a_created_creditor_is_returned_and_then_listed(client):
    created = add(client, address={"line1": "1 Main St", "city": "Springfield"})
    assert created.status_code == 201
    body = created.get_json()
    assert body["name"] == "Acme Collections"
    assert body["address"]["line1"] == "1 Main St"
    assert body["preferred"] is False

    listed = client.get("/v1/firm/creditors", headers=auth(ADMIN))
    assert listed.status_code == 200
    assert [c["id"] for c in listed.get_json()["creditors"]] == [body["id"]]


def test_a_missing_name_is_a_400_naming_the_field(client):
    response = client.post("/v1/firm/creditors", json={}, headers=auth(ADMIN))
    assert response.status_code == 400
    assert "name" in response.get_json()["fields"]


def test_get_one_by_id(client):
    created = add(client).get_json()
    fetched = client.get(f"/v1/firm/creditors/{created['id']}", headers=auth(ADMIN))
    assert fetched.status_code == 200
    assert fetched.get_json()["id"] == created["id"]


def test_a_replace_keeps_the_id_and_changes_the_fields(client):
    created = add(client).get_json()
    replaced = client.put(
        f"/v1/firm/creditors/{created['id']}",
        json={"name": "Acme Collections LLC", "preferred": True},
        headers=auth(ADMIN),
    )
    assert replaced.status_code == 200
    body = replaced.get_json()
    assert body["id"] == created["id"]
    assert body["name"] == "Acme Collections LLC"
    assert body["preferred"] is True
    # A replace with no address CLEARS it — this is PUT, whole-record.
    assert body["address"] == {}


def test_deleting_removes_it_from_the_list(client):
    created = add(client).get_json()
    deleted = client.delete(f"/v1/firm/creditors/{created['id']}", headers=auth(ADMIN))
    assert deleted.status_code == 204

    listed = client.get("/v1/firm/creditors", headers=auth(ADMIN))
    assert listed.get_json()["creditors"] == []


def test_deleting_twice_is_404_the_second_time(client):
    created = add(client).get_json()
    client.delete(f"/v1/firm/creditors/{created['id']}", headers=auth(ADMIN))
    again = client.delete(f"/v1/firm/creditors/{created['id']}", headers=auth(ADMIN))
    assert again.status_code == 404


# ── Firm scoping (the anti-oracle rule) ─────────────────────────────


def test_another_firms_creditor_is_a_404_not_a_403(client):
    """Same anti-oracle rule test_firm_routes.py's cross-tenant checks hold:
    an id belonging to another firm must read exactly like an id that never
    existed, or a caller could probe for other firms' library sizes."""
    created = add(client).get_json()

    assert (
        client.get(
            f"/v1/firm/creditors/{created['id']}", headers=auth(OTHER_ADMIN)
        ).status_code
        == 404
    )
    assert (
        client.put(
            f"/v1/firm/creditors/{created['id']}",
            json={"name": "Hijacked"},
            headers=auth(OTHER_ADMIN),
        ).status_code
        == 404
    )
    assert (
        client.delete(
            f"/v1/firm/creditors/{created['id']}", headers=auth(OTHER_ADMIN)
        ).status_code
        == 404
    )

    # And it is untouched, from the owning firm's side.
    still_there = client.get(f"/v1/firm/creditors/{created['id']}", headers=auth(ADMIN))
    assert still_there.status_code == 200
    assert still_there.get_json()["name"] == "Acme Collections"


def test_additional_notice_parties_round_trip(client):
    created = add(
        client,
        additional_notice_parties=[
            {"id": "np1", "name": "Legal Dept", "account_last4": "1234"}
        ],
    ).get_json()
    assert created["additional_notice_parties"] == [
        {"id": "np1", "name": "Legal Dept", "account_last4": "1234", "address": {}}
    ]
