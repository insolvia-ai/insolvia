"""The generic case-collection endpoints (issue #249).

What matters most is what these REFUSE, exactly as for debtors: the collections
hold creditor names, amounts owed and a family's expenses, and the case lookup
is the only authorisation there is. Tokens are signed for real, mirroring
tests/test_debtor_routes.py. Every identifier below is obviously fake; this
repo is public.
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
from insolvia_core.adapters.memory.case_entity_store import MemoryCaseEntityStore
from insolvia_core.adapters.memory.case_store import MemoryCaseStore
from insolvia_core.adapters.memory.debtor_store import MemoryDebtorStore
from insolvia_core.adapters.memory.firm_store import MemoryFirmStore
from insolvia_core.adapters.memory.jwks_provider import StaticJwksProvider
from insolvia_core.firms import Firm, FirmUser, default_permissions

ISSUER = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE00"
CLIENT_ID = "exampleappclientid000000"
FIRM_A = "00000000-0000-4000-8000-00000000f18a"
FIRM_B = "00000000-0000-4000-8000-00000000f18b"
ALICE = "00000000-0000-4000-8000-00000000a11c"
BOB = "00000000-0000-4000-8000-00000000b0b0"
KID = "test-key-1"

TYPED = {"source": "staff_typed"}

_PRIVATE_KEY = rsa.generate_private_key(public_exponent=65537, key_size=2048)
_PUBLIC_KEY = _PRIVATE_KEY.public_key()


def token_for(subject: str) -> str:
    now = int(time.time())
    return jwt.encode(
        {
            "sub": subject,
            "iss": ISSUER,
            "client_id": CLIENT_ID,
            "token_use": "access",
            "iat": now,
            "exp": now + 3600,
        },
        _PRIVATE_KEY,  # type: ignore[arg-type]
        algorithm="RS256",
        headers={"kid": KID},
    )


def auth(subject: str) -> dict[str, str]:
    return {"Authorization": f"Bearer {token_for(subject)}"}


def firm(firm_id: str, name: str) -> Firm:
    return Firm(
        id=firm_id,
        name=name,
        status="active",
        created_at="2026-01-01T00:00:00.000Z",
        updated_at="2026-01-01T00:00:00.000Z",
    )


def member(subject: str, firm_id: str) -> FirmUser:
    return FirmUser(
        firm_id=firm_id,
        subject=subject,
        email=f"{subject[-4:]}@example.test",
        first_name="Person",
        last_name=subject[-4:],
        role="attorney",
        is_admin=True,
        access_all_cases=False,
        permissions=default_permissions("attorney"),
        status="active",
        created_at="2026-01-01T00:00:00.000Z",
        updated_at="2026-01-01T00:00:00.000Z",
    )


@pytest.fixture
def access_log():
    return MemoryAccessLog()


@pytest.fixture
def client(access_log):
    firms = MemoryFirmStore()
    firms.create_firm(firm(FIRM_A, "Example & Partners"))
    firms.create_firm(firm(FIRM_B, "Other Firm LLP"))
    firms.add_user(member(ALICE, FIRM_A))
    firms.add_user(member(BOB, FIRM_B))
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
            firm_store=firms,
            access_log=access_log,
            # Composed so the not-shadowed test below can prove the static
            # debtor route still answers, not because these tests need it.
            debtor_store=MemoryDebtorStore(),
            case_entity_store=MemoryCaseEntityStore(),
        )
    )
    return app.test_client()


def open_case(client, subject=ALICE):
    response = client.post(
        "/v1/cases", json={"chapter": 7, "district": "NDCA"}, headers=auth(subject)
    )
    assert response.status_code == 201
    return response.get_json()["id"]


CREDITOR_BODY = {
    "name": "Example Bank",
    "address": {"line1": "1 Example Way", "city": "Exampleville"},
    "provenance": {
        "name": TYPED,
        "address.line1": TYPED,
        "address.city": TYPED,
    },
}


def add_creditor(client, case_id, subject=ALICE, body=None):
    return client.post(
        f"/v1/cases/{case_id}/creditors",
        json=body if body is not None else CREDITOR_BODY,
        headers=auth(subject),
    )


# ── Auth and ownership ──────────────────────────────────────────


@pytest.mark.parametrize(
    ("method", "path"),
    [
        ("post", "/v1/cases/any-id/creditors"),
        ("get", "/v1/cases/any-id/creditors"),
        ("get", "/v1/cases/any-id/creditors/any-entity"),
        ("put", "/v1/cases/any-id/creditors/any-entity"),
        ("delete", "/v1/cases/any-id/creditors/any-entity"),
    ],
)
def test_every_route_refuses_an_unauthenticated_caller(client, method, path):
    assert getattr(client, method)(path, json={}).status_code == 401


def test_another_firms_case_is_not_found_on_write(client):
    case_id = open_case(client, ALICE)
    assert add_creditor(client, case_id, subject=BOB).status_code == 404


def test_a_record_written_by_one_firm_is_invisible_to_another(client):
    case_id = open_case(client, ALICE)
    entity_id = add_creditor(client, case_id).get_json()["id"]
    foreign = client.get(
        f"/v1/cases/{case_id}/creditors/{entity_id}", headers=auth(BOB)
    )
    missing = client.get(
        "/v1/cases/no-such-case/creditors/no-such-id", headers=auth(BOB)
    )
    assert foreign.status_code == missing.status_code == 404
    assert foreign.get_json() == missing.get_json()


def test_an_unknown_collection_is_a_plain_404(client):
    case_id = open_case(client)
    response = client.get(f"/v1/cases/{case_id}/pay_stubs", headers=auth(ALICE))
    assert response.status_code == 404


def test_an_entity_id_does_not_resolve_through_another_collection(client):
    case_id = open_case(client)
    entity_id = add_creditor(client, case_id).get_json()["id"]
    response = client.get(
        f"/v1/cases/{case_id}/claims/{entity_id}", headers=auth(ALICE)
    )
    assert response.status_code == 404


def test_the_static_debtor_and_document_routes_are_not_shadowed(client):
    # /v1/cases/<id>/<collection> is dynamic; Werkzeug must keep routing
    # /debtors and /documents to their own modules.
    case_id = open_case(client)
    response = client.get(f"/v1/cases/{case_id}/debtors", headers=auth(ALICE))
    assert response.status_code == 200
    assert response.get_json() == {"debtors": []}


# ── The write path ──────────────────────────────────────────────


def test_adding_a_record_answers_201_with_the_stored_record(client):
    case_id = open_case(client)
    response = add_creditor(client, case_id)
    assert response.status_code == 201
    record = response.get_json()
    assert record["name"] == "Example Bank"
    assert record["address"] == {"line1": "1 Example Way", "city": "Exampleville"}
    assert record["case_id"] == case_id
    assert record["provenance"]["name"] == TYPED
    assert record["id"]
    assert record["created_at"] == record["updated_at"]


def test_a_populated_field_without_provenance_is_a_400(client):
    case_id = open_case(client)
    response = add_creditor(
        client, case_id, body={"name": "Example Bank", "provenance": {}}
    )
    assert response.status_code == 400
    assert "provenance.name" in response.get_json()["fields"]


def test_an_unconfirmed_extracted_value_is_a_400(client):
    case_id = open_case(client)
    response = add_creditor(
        client,
        case_id,
        body={
            "name": "Example Bank",
            "provenance": {"name": {"source": "ai_extracted"}},
        },
    )
    assert response.status_code == 400
    assert "provenance.name" in response.get_json()["fields"]


def test_editing_a_record_replaces_it_whole(client):
    case_id = open_case(client)
    entity_id = add_creditor(client, case_id).get_json()["id"]
    response = client.put(
        f"/v1/cases/{case_id}/creditors/{entity_id}",
        json={"name": "Renamed Bank", "provenance": {"name": TYPED}},
        headers=auth(ALICE),
    )
    assert response.status_code == 200
    record = response.get_json()
    assert record["name"] == "Renamed Bank"
    # The PUT replaced the record: the address sent on create is gone.
    assert "address" not in record
    assert record["id"] == entity_id


def test_editing_an_id_the_server_never_minted_is_a_404(client):
    # No upsert: ids are server-minted, so an unknown id is a client error.
    case_id = open_case(client)
    response = client.put(
        f"/v1/cases/{case_id}/creditors/never-minted",
        json={"name": "Example", "provenance": {"name": TYPED}},
        headers=auth(ALICE),
    )
    assert response.status_code == 404


def test_removing_a_record_answers_204_then_404(client):
    case_id = open_case(client)
    entity_id = add_creditor(client, case_id).get_json()["id"]
    first = client.delete(
        f"/v1/cases/{case_id}/creditors/{entity_id}", headers=auth(ALICE)
    )
    second = client.delete(
        f"/v1/cases/{case_id}/creditors/{entity_id}", headers=auth(ALICE)
    )
    assert first.status_code == 204
    assert second.status_code == 404
    listing = client.get(f"/v1/cases/{case_id}/creditors", headers=auth(ALICE))
    assert listing.get_json() == {"creditors": []}


# ── Listing ─────────────────────────────────────────────────────


def test_listing_returns_records_in_creation_order(client):
    case_id = open_case(client)
    first = add_creditor(client, case_id).get_json()["id"]
    second = add_creditor(
        client,
        case_id,
        body={"name": "Second Bank", "provenance": {"name": TYPED}},
    ).get_json()["id"]
    listing = client.get(f"/v1/cases/{case_id}/creditors", headers=auth(ALICE))
    assert listing.status_code == 200
    assert [record["id"] for record in listing.get_json()["creditors"]] == [
        first,
        second,
    ]


def test_collections_are_listed_separately(client):
    case_id = open_case(client)
    add_creditor(client, case_id)
    response = client.post(
        f"/v1/cases/{case_id}/expenses",
        json={
            "category": "rent_or_home_ownership",
            "amount": "1800.00",
            "provenance": {"category": TYPED, "amount": TYPED},
        },
        headers=auth(ALICE),
    )
    assert response.status_code == 201
    expenses = client.get(f"/v1/cases/{case_id}/expenses", headers=auth(ALICE))
    creditors = client.get(f"/v1/cases/{case_id}/creditors", headers=auth(ALICE))
    assert len(expenses.get_json()["expenses"]) == 1
    assert len(creditors.get_json()["creditors"]) == 1


def test_a_sofa_entry_round_trips_over_the_api(client):
    case_id = open_case(client)
    response = client.post(
        f"/v1/cases/{case_id}/sofa_entries",
        json={
            "entry_type": "gift",
            "payload": {"recipient": {"name": "Example Recipient"}, "value": "700.00"},
            "provenance": {
                "entry_type": TYPED,
                "payload.recipient.name": TYPED,
                "payload.value": TYPED,
            },
        },
        headers=auth(ALICE),
    )
    assert response.status_code == 201
    record = response.get_json()
    assert record["entry_type"] == "gift"
    assert record["payload"] == {
        "recipient": {"name": "Example Recipient"},
        "value": "700.00",
    }


# ── The access log ──────────────────────────────────────────────


def test_reads_and_writes_are_recorded_either_way(client, access_log):
    case_id = open_case(client)
    add_creditor(client, case_id)
    client.get(f"/v1/cases/{case_id}/creditors", headers=auth(ALICE))
    client.get(f"/v1/cases/{case_id}/creditors", headers=auth(BOB))
    outcomes = [
        (event.action, event.outcome)
        for event in access_log.events
        if event.case_id == case_id
    ]
    assert ("case.update", "allowed") in outcomes
    assert ("case.read", "allowed") in outcomes
    assert ("case.read", "denied") in outcomes


def test_a_malformed_body_never_reaches_the_access_log(client, access_log):
    case_id = open_case(client)
    before = len(access_log.events)
    response = add_creditor(client, case_id, body={"name": 7})
    assert response.status_code == 400
    assert len(access_log.events) == before
