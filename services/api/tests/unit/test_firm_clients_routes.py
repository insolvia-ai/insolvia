"""The firm's client directory (ADR 0022 / #353): `/v1/firm/clients`, gated
by `clients`.

What this file holds down, in the order ADR 0022's PR 1 row states it:

  1. THE FIVE ROUTES WORK over the memory adapter — list, create, read,
     whole-record edit, and archive as a status write (no delete).
  2. THE GATE IS ADR 0009's: `view_only` reads, `add_edit` writes, and a user
     without `clients` — including every row written before the feature
     existed — gets the same 403 every other feature answers.
  3. EVERY ROUTE IS SCOPED TO THE CALLER'S FIRM, and another firm's id is the
     same 404 as one that never existed.
  4. `client.*` ACCESS ROWS ARE RECORDED, keyed `CLIENT#<id>`: creates,
     single reads (a refused one too) and updates — never the list.

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
from insolvia_core.access_log import access_item
from insolvia_core.adapters.memory.access_log import MemoryAccessLog
from insolvia_core.adapters.memory.case_store import MemoryCaseStore
from insolvia_core.adapters.memory.firm_store import MemoryFirmStore
from insolvia_core.adapters.memory.jwks_provider import StaticJwksProvider
from insolvia_core.adapters.memory.user_directory import MemoryUserDirectory
from insolvia_core.firms import (
    CLIENTS,
    HIDDEN,
    VIEW_ONLY,
    Firm,
    FirmUser,
    default_permissions,
)

ISSUER = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE00"
CLIENT_ID = "exampleappclientid000000"
KID = "test-key-1"

FIRM_A = "00000000-0000-4000-8000-00000000f18a"
FIRM_B = "00000000-0000-4000-8000-00000000f18b"

ADMIN = "00000000-0000-4000-8000-00000000a11c"  # firm A admin
VIEWER = "00000000-0000-4000-8000-00000000da4a"  # clients: view_only
BLOCKED = "00000000-0000-4000-8000-00000000b0b0"  # clients: hidden
LEGACY = "00000000-0000-4000-8000-00000000013c"  # a row from before `clients`
PARALEGAL = "00000000-0000-4000-8000-0000000000a1"  # the new-row defaults
OTHER_ADMIN = "00000000-0000-4000-8000-0000000ca201"  # firm B admin

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
    subject: str,
    firm_id: str,
    permissions: dict[str, str],
    *,
    role: str = "attorney",
    is_admin: bool = False,
) -> FirmUser:
    return FirmUser(
        firm_id=firm_id,
        subject=subject,
        email=f"{subject[-4:]}@example.test",
        first_name="Person",
        last_name=subject[-4:],
        role=role,
        is_admin=is_admin,
        access_all_cases=False,
        permissions=permissions,
        status="active",
        created_at="2026-01-01T00:00:00.000Z",
        updated_at="2026-01-01T00:00:00.000Z",
    )


def _legacy_permissions() -> dict[str, str]:
    """An attorney's full defaults as they were before ADR 0022 — every
    feature but `clients`, which that row was never written with."""
    permissions = dict(default_permissions("attorney"))
    del permissions[CLIENTS]
    return permissions


@pytest.fixture
def access_log():
    return MemoryAccessLog()


@pytest.fixture
def firms():
    store = MemoryFirmStore()
    store.create_firm(firm(FIRM_A, "Example & Partners"))
    store.create_firm(firm(FIRM_B, "Other Firm LLP"))
    store.add_user(member(ADMIN, FIRM_A, {}, is_admin=True))
    store.add_user(member(VIEWER, FIRM_A, {CLIENTS: VIEW_ONLY}))
    store.add_user(member(BLOCKED, FIRM_A, {CLIENTS: HIDDEN}))
    store.add_user(member(LEGACY, FIRM_A, _legacy_permissions()))
    store.add_user(
        member(PARALEGAL, FIRM_A, default_permissions("paralegal"), role="paralegal")
    )
    store.add_user(member(OTHER_ADMIN, FIRM_B, {}, is_admin=True))
    return store


@pytest.fixture
def client(firms, access_log):
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
            access_log=access_log,
            firm_store=firms,
            user_directory=MemoryUserDirectory(),
        )
    )
    return app.test_client()


def add(client, subject=ADMIN, **body):
    payload = {"name": {"given": "Jordan", "surname": "Example"}, **body}
    return client.post("/v1/firm/clients", json=payload, headers=auth(subject))


def rows(access_log):
    return [(e.action, e.client_id, e.principal, e.outcome) for e in access_log.events]


# ── Gating (ADR 0009) ───────────────────────────────────────────────


def test_view_only_can_list_and_read_but_not_write(client):
    created = add(client).get_json()
    assert client.get("/v1/firm/clients", headers=auth(VIEWER)).status_code == 200
    one = client.get(f"/v1/firm/clients/{created['id']}", headers=auth(VIEWER))
    assert one.status_code == 200

    assert add(client, subject=VIEWER).status_code == 403
    assert (
        client.put(
            f"/v1/firm/clients/{created['id']}",
            json={"name": {"surname": "X"}},
            headers=auth(VIEWER),
        ).status_code
        == 403
    )
    assert (
        client.put(
            f"/v1/firm/clients/{created['id']}/status",
            json={"status": "archived"},
            headers=auth(VIEWER),
        ).status_code
        == 403
    )


@pytest.mark.parametrize("subject", [BLOCKED, LEGACY])
def test_without_the_feature_every_route_is_the_adr_0009_403(client, subject):
    """`hidden`, and — the maintainer's choice in ADR 0022 — every row written
    before `clients` existed, which reads as hidden until an admin grants it.
    The same 403 body every other feature's refusal answers."""
    created = add(client).get_json()
    url = f"/v1/firm/clients/{created['id']}"
    responses = [
        client.get("/v1/firm/clients", headers=auth(subject)),
        add(client, subject=subject),
        client.get(url, headers=auth(subject)),
        client.put(url, json={"name": {"surname": "X"}}, headers=auth(subject)),
        client.put(f"{url}/status", json={"status": "archived"}, headers=auth(subject)),
    ]
    for response in responses:
        assert response.status_code == 403
        assert response.get_json() == {
            "error": "ForbiddenError",
            "message": "your firm has not granted you access to this",
        }


def test_a_new_paralegal_row_can_keep_the_directory(client):
    """`default_permissions` gives `clients: add_edit` to NEW rows."""
    assert add(client, subject=PARALEGAL).status_code == 201


def test_no_auth_is_401(client):
    assert client.get("/v1/firm/clients").status_code == 401


def test_there_is_no_delete(client):
    """Archive is a status write; a client is never deleted in this PR."""
    created = add(client).get_json()
    response = client.delete(f"/v1/firm/clients/{created['id']}", headers=auth(ADMIN))
    assert response.status_code == 405


# ── The five routes ────────────────────────────────────────────────


def test_a_created_client_is_returned_and_then_listed(client):
    created = add(
        client,
        residence_address={"line1": "1 Example St", "county": "Sangamon"},
        date_of_birth="1980-02-29",
        lead_source="Referral",
    )
    assert created.status_code == 201
    body = created.get_json()
    assert body["name"] == {"given": "Jordan", "surname": "Example"}
    assert body["residence_address"] == {"line1": "1 Example St", "county": "Sangamon"}
    assert body["status"] == "active"
    assert body["created_by"] == ADMIN
    assert body["lead_source"] == "Referral"

    listed = client.get("/v1/firm/clients", headers=auth(ADMIN)).get_json()
    assert [c["id"] for c in listed["clients"]] == [body["id"]]


def test_a_missing_name_is_a_400_naming_the_field(client):
    response = client.post(
        "/v1/firm/clients", json={"name": {"middle": "Q"}}, headers=auth(ADMIN)
    )
    assert response.status_code == 400
    assert "name" in response.get_json()["fields"]


def test_a_tax_id_in_the_body_is_a_400_not_a_silent_drop(client):
    response = add(client, tax_id={"kind": "ssn", "value": "987-65-4320"})
    assert response.status_code == 400
    assert "tax_id" in response.get_json()["fields"]


def test_get_one_by_id(client):
    created = add(client).get_json()
    fetched = client.get(f"/v1/firm/clients/{created['id']}", headers=auth(ADMIN))
    assert fetched.status_code == 200
    assert fetched.get_json() == created


def test_a_replace_is_whole_record_and_keeps_the_server_owned_fields(client):
    created = add(client, lead_source="Referral").get_json()
    replaced = client.put(
        f"/v1/firm/clients/{created['id']}",
        # A client echoing server-owned fields: ignored, not obeyed.
        json={
            "name": {"surname": "Renamed"},
            "status": "archived",
            "created_by": OTHER_ADMIN,
        },
        headers=auth(ADMIN),
    )
    assert replaced.status_code == 200
    body = replaced.get_json()
    assert body["id"] == created["id"]
    assert body["name"] == {"surname": "Renamed"}
    assert "lead_source" not in body  # PUT, whole-record: omitted is cleared
    assert body["status"] == "active"
    assert body["created_by"] == ADMIN
    assert body["created_at"] == created["created_at"]


def test_archive_is_a_status_write_and_is_reversible(client):
    created = add(client).get_json()
    url = f"/v1/firm/clients/{created['id']}/status"

    archived = client.put(url, json={"status": "archived"}, headers=auth(ADMIN))
    assert archived.status_code == 200
    assert archived.get_json()["status"] == "archived"
    assert archived.get_json()["name"] == created["name"]

    # Still in the directory: archived is a state, not an absence.
    listed = client.get("/v1/firm/clients", headers=auth(ADMIN)).get_json()
    assert [c["status"] for c in listed["clients"]] == ["archived"]

    restored = client.put(url, json={"status": "active"}, headers=auth(ADMIN))
    assert restored.get_json()["status"] == "active"


def test_an_unknown_status_is_a_400(client):
    created = add(client).get_json()
    response = client.put(
        f"/v1/firm/clients/{created['id']}/status",
        json={"status": "deleted"},
        headers=auth(ADMIN),
    )
    assert response.status_code == 400
    assert "status" in response.get_json()["fields"]


# ── Firm scoping (the anti-oracle rule) ─────────────────────────────


def test_another_firms_client_is_a_404_not_a_403(client):
    created = add(client).get_json()
    url = f"/v1/firm/clients/{created['id']}"

    assert client.get(url, headers=auth(OTHER_ADMIN)).status_code == 404
    assert (
        client.put(
            url, json={"name": {"surname": "Hijacked"}}, headers=auth(OTHER_ADMIN)
        ).status_code
        == 404
    )
    assert (
        client.put(
            f"{url}/status", json={"status": "archived"}, headers=auth(OTHER_ADMIN)
        ).status_code
        == 404
    )
    assert client.get("/v1/firm/clients", headers=auth(OTHER_ADMIN)).get_json() == {
        "clients": []
    }

    untouched = client.get(url, headers=auth(ADMIN)).get_json()
    assert untouched == created


# ── The access log ──────────────────────────────────────────────────


def test_create_read_and_update_are_logged_under_the_client(client, access_log):
    created = add(client).get_json()
    cid = created["id"]
    client.get(f"/v1/firm/clients/{cid}", headers=auth(VIEWER))
    client.put(
        f"/v1/firm/clients/{cid}", json={"name": {"surname": "R"}}, headers=auth(ADMIN)
    )
    client.put(
        f"/v1/firm/clients/{cid}/status",
        json={"status": "archived"},
        headers=auth(ADMIN),
    )

    assert rows(access_log) == [
        ("client.create", cid, ADMIN, "allowed"),
        ("client.read", cid, VIEWER, "allowed"),
        ("client.update", cid, ADMIN, "allowed"),
        ("client.update", cid, ADMIN, "allowed"),
    ]
    assert {access_item(e)["PK"] for e in access_log.events} == {f"CLIENT#{cid}"}
    assert all(e.case_id is None for e in access_log.events)


def test_the_list_is_not_logged(client, access_log):
    add(client)
    before = len(access_log.events)
    client.get("/v1/firm/clients", headers=auth(ADMIN))
    assert len(access_log.events) == before


def test_a_refused_read_is_logged_as_denied(client, access_log):
    """Someone walking client ids — including another firm's — is exactly
    what the access log should show."""
    created = add(client).get_json()
    client.get(f"/v1/firm/clients/{created['id']}", headers=auth(OTHER_ADMIN))
    assert rows(access_log)[-1] == (
        "client.read",
        created["id"],
        OTHER_ADMIN,
        "denied",
    )


def test_a_forbidden_caller_writes_no_row(client, access_log):
    """The feature gate refuses before any record is touched — there is no
    client read to log."""
    created = add(client).get_json()
    before = len(access_log.events)
    client.get(f"/v1/firm/clients/{created['id']}", headers=auth(BLOCKED))
    assert len(access_log.events) == before


# ── A client's cases (ADR 0022, PR 2) ───────────────────────────────


def open_for(client, client_ids, subject=ADMIN):
    response = client.post(
        "/v1/cases",
        json={
            "chapter": 7,
            "court": "flmb",
            "division": "tampa",
            "client_ids": client_ids,
        },
        headers=auth(subject),
    )
    assert response.status_code == 201, response.get_json()
    return response.get_json()["id"]


def cases_of(client, client_id, subject=ADMIN):
    return client.get(f"/v1/firm/clients/{client_id}/cases", headers=auth(subject))


def test_a_clients_cases_list_newest_first_with_the_role_they_hold(client):
    jordan = add(client).get_json()["id"]
    sam = add(client, name={"given": "Sam"}).get_json()["id"]
    alone = open_for(client, [jordan])
    joint = open_for(client, [sam, jordan])

    response = cases_of(client, jordan)

    assert response.status_code == 200
    body = response.get_json()
    assert [(e["case"]["id"], e["filing_role"]) for e in body["cases"]] == [
        (joint, "debtor_2"),
        (alone, "debtor_1"),
    ]
    # The case in GET /v1/cases/<id>'s own shape.
    assert body["cases"][0]["case"]["district"] == "Middle District of Florida"
    # A joint case is in each of its clients' lists, once.
    assert [e["case"]["id"] for e in cases_of(client, sam).get_json()["cases"]] == [
        joint
    ]


def test_a_client_with_no_case_yet_is_a_prospect_with_an_empty_list(client):
    prospect = add(client).get_json()["id"]
    assert cases_of(client, prospect).get_json() == {"cases": []}


def test_the_list_holds_only_cases_the_caller_may_see_and_no_count(client):
    """The paralegal is linked to the matter they opened and nothing else.
    The admin's case for the same client is not listed, and nothing in the
    body says it exists — a count would be ADR 0009's enumeration."""
    jordan = add(client).get_json()["id"]
    open_for(client, [jordan], subject=ADMIN)
    theirs = open_for(client, [jordan], subject=PARALEGAL)

    body = cases_of(client, jordan, PARALEGAL).get_json()

    assert [e["case"]["id"] for e in body["cases"]] == [theirs]
    assert set(body) == {"cases"}
    assert len(cases_of(client, jordan).get_json()["cases"]) == 2


def test_another_firms_client_is_the_same_404_as_none_and_is_logged(client, access_log):
    jordan = add(client).get_json()["id"]
    open_for(client, [jordan])
    foreign = cases_of(client, jordan, OTHER_ADMIN)
    missing = cases_of(client, "no-such-client", OTHER_ADMIN)
    assert foreign.status_code == missing.status_code == 404
    assert foreign.get_json() == missing.get_json()
    assert rows(access_log)[-2] == ("client.read", jordan, OTHER_ADMIN, "denied")


def test_listing_a_clients_cases_needs_the_cases_feature_too(client):
    """VIEWER may see the directory but holds no `cases` grant at all."""
    jordan = add(client).get_json()["id"]
    assert cases_of(client, jordan, VIEWER).status_code == 403
