"""GET /v1/cases/<id>/liens — the claim read path for the derived collateral
figures (issue #345).

The arithmetic has its own suite (tests/unit/test_liens.py); what this file
pins is the wiring — that a stored claim and a stored asset reach the
function, that the route is authorised like the collection reads it is a
read over, and that the static segment is not swallowed by the generic
collection route. Every identifier below is obviously fake; this repo is
public.
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
from insolvia_core.adapters.memory.tax_id_cipher import LocalTaxIdCipher
from insolvia_core.adapters.memory.tax_id_store import MemoryTaxIdStore
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
            tax_id_store=MemoryTaxIdStore(),
            tax_id_cipher=LocalTaxIdCipher(),
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


def add(client, case_id, collection, **body):
    response = client.post(
        f"/v1/cases/{case_id}/{collection}",
        json={**body, "provenance": dict.fromkeys(body, TYPED)},
        headers=auth(ALICE),
    )
    assert response.status_code == 201, response.get_json()
    return response.get_json()["id"]


def liens(client, case_id, subject=ALICE):
    return client.get(f"/v1/cases/{case_id}/liens", headers=auth(subject))


# ── Auth and ownership ──────────────────────────────────────────


def test_the_route_refuses_an_unauthenticated_caller(client):
    assert client.get("/v1/cases/any-id/liens").status_code == 401


def test_an_unknown_case_is_not_found(client):
    assert liens(client, "no-such-case").status_code == 404


def test_another_firms_liens_are_the_same_404_as_no_case(client):
    case_id = open_case(client, ALICE)
    assert liens(client, case_id, subject=BOB).status_code == 404


def test_the_static_segment_is_not_shadowed_by_the_collection_routes(client):
    case_id = open_case(client)
    response = liens(client, case_id)
    assert response.status_code == 200
    assert response.get_json() == {"claims": [], "assets": []}


# ── The wiring ──────────────────────────────────────────────────


def test_a_stored_claim_and_its_asset_reach_the_arithmetic(client):
    case_id = open_case(client)
    house = add(
        client, case_id, "assets", category="real_property", value_entire="300000.00"
    )
    first = add(
        client,
        case_id,
        "claims",
        claim_class="secured",
        amount="250000.00",
        asset_id=house,
        lien_position=1,
    )
    second = add(
        client,
        case_id,
        "claims",
        claim_class="secured",
        amount="80000.00",
        asset_id=house,
        lien_position=2,
    )

    body = liens(client, case_id).get_json()

    assert body["assets"] == [
        {"assetId": house, "claimIds": [first, second], "securedTotal": "330000.00"}
    ]
    rows = {row["claimId"]: row for row in body["claims"]}
    assert rows[first]["unsecuredAmount"] == "0.00"
    assert rows[second]["seniorLiens"] == "250000.00"
    assert rows[second]["unsecuredAmount"] == "30000.00"
    assert rows[second]["securedAmount"] == "50000.00"


def test_moving_a_lien_senior_changes_the_deficiency(client):
    # The issue's "senior-lien ordering changes the deficiency correctly", over
    # the API: swap the positions with a PUT and the shortfall moves.
    case_id = open_case(client)
    house = add(
        client, case_id, "assets", category="real_property", value_entire="300000.00"
    )
    big = add(
        client,
        case_id,
        "claims",
        claim_class="secured",
        amount="250000.00",
        asset_id=house,
        lien_position=1,
    )
    add(
        client,
        case_id,
        "claims",
        claim_class="secured",
        amount="80000.00",
        asset_id=house,
        lien_position=2,
    )
    body = {"claim_class": "secured", "amount": "250000.00", "asset_id": house}
    demoted = {**body, "lien_position": 3}
    response = client.put(
        f"/v1/cases/{case_id}/claims/{big}",
        json={**demoted, "provenance": dict.fromkeys(demoted, TYPED)},
        headers=auth(ALICE),
    )
    assert response.status_code == 200, response.get_json()

    rows = {row["claimId"]: row for row in liens(client, case_id).get_json()["claims"]}

    assert rows[big]["seniorLiens"] == "80000.00"
    assert rows[big]["unsecuredAmount"] == "30000.00"


def test_money_is_a_string_never_a_json_number(client):
    case_id = open_case(client)
    add(client, case_id, "claims", claim_class="secured", amount="8412.66")

    (row,) = liens(client, case_id).get_json()["claims"]

    assert row["amount"] == "8412.66"
    assert isinstance(row["seniorLiens"], str)
