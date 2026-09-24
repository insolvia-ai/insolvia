"""`GET /v1/cases/{id}/standards` (issue #348) — the IRS National and Local
Standards beside Schedule J's health-care and transportation lines.

Same auth/ownership shape as `/summary` (test_case_summary_route.py); this
file's own job is the jurisdiction lookup and the "too early to answer"
degrade path. Alachua County, FL is a real row in the committed registry
(see test_means_test.py's docstring) — the arithmetic itself belongs to
test_ust_data.py's known-answer tests, so this file only pins the wiring.

Every identifier below is obviously fake; this repo is public.
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
        "/v1/cases",
        json={"chapter": 7, "district": "Middle District of Florida"},
        headers=auth(subject),
    )
    assert response.status_code == 201
    return response.get_json()["id"]


def standards(client, case_id, subject=ALICE):
    return client.get(f"/v1/cases/{case_id}/standards", headers=auth(subject))


def put_debtor_1(client, case_id, *, state=None, county=None, subject=ALICE):
    body: dict[str, object] = {"name": {"given": "Ada"}}
    provenance = {"name.given": TYPED}
    if state is not None or county is not None:
        address: dict[str, str] = {}
        if state is not None:
            address["state"] = state
            provenance["residence_address.state"] = TYPED
        if county is not None:
            address["county"] = county
            provenance["residence_address.county"] = TYPED
        body["residence_address"] = address
    response = client.put(
        f"/v1/cases/{case_id}/debtors/debtor_1",
        json={**body, "provenance": provenance},
        headers=auth(subject),
    )
    assert response.status_code == 201, response.get_json()
    return response.get_json()["id"]


# ── Auth and ownership ──────────────────────────────────────────


def test_the_route_refuses_an_unauthenticated_caller(client):
    assert client.get("/v1/cases/any-id/standards").status_code == 401


def test_an_unknown_case_is_not_found(client):
    assert standards(client, "no-such-case").status_code == 404


def test_another_firms_standards_are_the_same_404_as_no_case(client):
    case_id = open_case(client, ALICE)
    assert standards(client, case_id, subject=BOB).status_code == 404


def test_the_static_segment_is_not_shadowed_by_the_collection_routes(client):
    case_id = open_case(client)
    response = standards(client, case_id)
    assert response.status_code == 200
    assert "nationalStandards" in response.get_json()


# ── Too early to answer ──────────────────────────────────────────


def test_a_case_with_no_debtor_1_reports_the_problem_not_an_error(client):
    case_id = open_case(client)

    body = standards(client, case_id).get_json()

    assert body["state"] is None
    assert body["householdSize"] is None
    assert body["nationalStandards"]["allowance"] is None
    assert any("Debtor 1" in problem for problem in body["problems"])


def test_a_debtor_with_no_county_reports_the_problem_not_an_error(client):
    case_id = open_case(client)
    put_debtor_1(client, case_id, state="FL")

    body = standards(client, case_id).get_json()

    assert body["state"] is None
    assert any("state and county" in problem for problem in body["problems"])


def test_an_unsupported_jurisdiction_is_reported_but_the_national_figures_still_land(
    client,
):
    case_id = open_case(client)
    put_debtor_1(client, case_id, state="ZZ", county="Nowhere County")

    body = standards(client, case_id).get_json()

    assert body["state"] == "ZZ"
    # No jurisdiction-specific housing/transportation figures for a state the
    # Local Standards launch set does not carry —
    assert body["localStandards"]["housingNonMortgage"] is None
    # — but the National Standards apply everywhere, so they still resolve.
    assert body["nationalStandards"]["allowance"] is not None
    assert body["problems"]


# ── The wire shape, for a jurisdiction the registry carries ──────


def test_a_supported_county_returns_every_figure_and_no_problems(client):
    case_id = open_case(client)
    put_debtor_1(client, case_id, state="FL", county="Alachua County")

    body = standards(client, case_id).get_json()

    assert body["state"] == "FL"
    assert body["county"] == "Alachua County"
    assert body["householdSize"] == 1
    assert body["problems"] == []
    national = body["nationalStandards"]
    assert national["releaseId"]
    assert national["allowance"]
    assert national["oopHealthcareUnder65"]
    assert national["oopHealthcare65AndOlder"]
    local = body["localStandards"]
    assert local["releaseId"]
    assert local["housingNonMortgage"]
    assert local["housingMortgageRent"]
    assert local["transportationPublicNational"]
    assert local["transportationOwnershipOneCar"]
    assert local["transportationOwnershipTwoCars"]
    assert local["transportationOperatingOneCar"]
    assert local["transportationOperatingTwoCars"]


def test_every_money_figure_is_a_string_never_a_json_number(client):
    case_id = open_case(client)
    put_debtor_1(client, case_id, state="FL", county="Alachua County")

    body = standards(client, case_id).get_json()

    assert isinstance(body["nationalStandards"]["allowance"], str)
    assert isinstance(body["localStandards"]["housingNonMortgage"], str)


def test_a_dependent_living_with_the_debtor_grows_the_household(client):
    case_id = open_case(client)
    put_debtor_1(client, case_id, state="FL", county="Alachua County")
    dep_body = {
        "household_id": "no-household-yet",
        "relationship": "child",
        "age": 9,
        "lives_with_debtor": True,
    }
    response = client.post(
        f"/v1/cases/{case_id}/dependents",
        json={**dep_body, "provenance": dict.fromkeys(dep_body, TYPED)},
        headers=auth(ALICE),
    )
    assert response.status_code == 201, response.get_json()

    body = standards(client, case_id).get_json()

    assert body["householdSize"] == 2
