"""GET /v1/cases/<id>/creditor-matrix (issue #94).

The generator's behaviour is pinned in tests/test_creditor_matrix.py; this
file covers what the ROUTE adds: authorisation (the case lookup is the only
check there is), the wire shape, and that the static URL is not shadowed by
the generic /<collection> routes. Tokens are signed for real, mirroring
tests/test_case_entity_routes.py. Every identifier below is obviously fake;
this repo is public.
"""

from __future__ import annotations

import time

import jwt
import pytest
from cryptography.hazmat.primitives.asymmetric import rsa
from insolvia_api.adapters.memory.access_log import MemoryAccessLog
from insolvia_api.adapters.memory.case_entity_store import MemoryCaseEntityStore
from insolvia_api.adapters.memory.case_store import MemoryCaseStore
from insolvia_api.adapters.memory.debtor_store import MemoryDebtorStore
from insolvia_api.adapters.memory.mailer_client import InMemoryMailerClient
from insolvia_api.adapters.memory.waitlist_store import MemoryWaitlistStore
from insolvia_api.api.app_factory import create_app
from insolvia_api.api.dependencies import ApiDependencies
from insolvia_api.core.config import load_config
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
def client():
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
            access_log=MemoryAccessLog(),
            debtor_store=MemoryDebtorStore(),
            case_entity_store=MemoryCaseEntityStore(),
        )
    )
    return app.test_client()


def open_case(client, subject=ALICE):
    response = client.post(
        "/v1/cases", json={"chapter": 7, "district": "NDFL"}, headers=auth(subject)
    )
    assert response.status_code == 201
    return response.get_json()["id"]


MAILABLE_CREDITOR = {
    "name": "Example Bank",
    "address": {
        "line1": "PO Box 15168",
        "city": "Wilmington",
        "state": "DE",
        "postal_code": "19850",
    },
    "provenance": {
        "name": TYPED,
        "address.line1": TYPED,
        "address.city": TYPED,
        "address.state": TYPED,
        "address.postal_code": TYPED,
    },
}


def add_creditor(client, case_id, body=None):
    response = client.post(
        f"/v1/cases/{case_id}/creditors",
        json=body if body is not None else MAILABLE_CREDITOR,
        headers=auth(ALICE),
    )
    assert response.status_code == 201
    return response.get_json()["id"]


def get_matrix(client, case_id, subject=ALICE):
    return client.get(f"/v1/cases/{case_id}/creditor-matrix", headers=auth(subject))


# ── Auth and ownership ──────────────────────────────────────────


def test_the_route_refuses_an_unauthenticated_caller(client):
    assert client.get("/v1/cases/any-id/creditor-matrix").status_code == 401


def test_an_unknown_case_is_not_found(client):
    assert get_matrix(client, "no-such-case").status_code == 404


def test_another_firms_matrix_is_the_same_404_as_no_case(client):
    case_id = open_case(client, ALICE)
    add_creditor(client, case_id)
    assert get_matrix(client, case_id, subject=BOB).status_code == 404


# ── The wire shape ──────────────────────────────────────────────


def test_a_mailable_creditor_list_produces_the_file(client):
    case_id = open_case(client)
    add_creditor(client, case_id)
    response = get_matrix(client, case_id)
    assert response.status_code == 200
    assert response.get_json() == {
        "fileName": "creditor-matrix.txt",
        "creditorCount": 1,
        "duplicatesOmitted": 0,
        "problems": [],
        "content": "Example Bank\r\nPO Box 15168\r\nWilmington DE 19850\r\n",
    }


def test_an_unmailable_creditor_answers_problems_instead_of_a_file(client):
    case_id = open_case(client)
    creditor_id = add_creditor(
        client,
        case_id,
        body={
            "name": "Example Bank",
            "address": {"line1": "PO Box 15168"},
            "provenance": {"name": TYPED, "address.line1": TYPED},
        },
    )
    body = get_matrix(client, case_id).get_json()
    assert "content" not in body
    assert body["creditorCount"] == 0
    reported = {(p["creditorId"], p["field"]) for p in body["problems"]}
    assert reported == {
        (creditor_id, "address.city"),
        (creditor_id, "address.state"),
        (creditor_id, "address.postal_code"),
    }


def test_a_case_with_no_creditors_answers_the_case_level_problem(client):
    case_id = open_case(client)
    body = get_matrix(client, case_id).get_json()
    assert "content" not in body
    assert body["problems"] == [
        {
            "field": "creditors",
            "message": "The case has no creditors — a matrix must list every"
            " creditor before it can be filed.",
        }
    ]


def test_the_static_url_is_not_swallowed_by_the_collection_routes(client):
    # "creditor-matrix" shares the /v1/cases/<id>/<segment> shape with the
    # generic collection routes, which answer 404 for unknown collections —
    # so a 200 here proves Werkzeug routed the static rule, not theirs.
    case_id = open_case(client)
    add_creditor(client, case_id)
    assert get_matrix(client, case_id).status_code == 200
    # And the dynamic rule still owns real collections.
    listed = client.get(f"/v1/cases/{case_id}/creditors", headers=auth(ALICE))
    assert listed.status_code == 200
    assert len(listed.get_json()["creditors"]) == 1
