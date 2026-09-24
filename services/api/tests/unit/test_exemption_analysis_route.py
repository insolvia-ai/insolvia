"""GET /v1/cases/<id>/exemption-analysis — the Schedule C workbench's read
path (issue #346).

The arithmetic has its own suite (tests/unit/test_exemption_analysis.py);
what this file pins is the wiring — that the debtor's state, the petition's
date, the assets, the liens and the claims all reach the function, that the
route is authorised like the intake reads it is a read over, and that the
static segment is not swallowed by the generic collection route (whose
`/exemptions` this deliberately does NOT share). Every identifier below is
obviously fake; this repo is public.
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
        json={"chapter": 7, "court": "flmb", "division": "tampa"},
        headers=auth(subject),
    )
    assert response.status_code == 201
    return response.get_json()["id"]


def put_debtor_1(client, case_id, *, state):
    response = client.put(
        f"/v1/cases/{case_id}/debtors/debtor_1",
        json={
            "name": {"given": "Ada"},
            "residence_address": {"state": state},
            "provenance": {"name.given": TYPED, "residence_address.state": TYPED},
        },
        headers=auth(ALICE),
    )
    assert response.status_code == 201, response.get_json()


def add(client, case_id, collection, **body):
    response = client.post(
        f"/v1/cases/{case_id}/{collection}",
        json={**body, "provenance": dict.fromkeys(body, TYPED)},
        headers=auth(ALICE),
    )
    assert response.status_code == 201, response.get_json()
    return response.get_json()["id"]


def analysis(client, case_id, subject=ALICE):
    return client.get(f"/v1/cases/{case_id}/exemption-analysis", headers=auth(subject))


# ── Auth and ownership ──────────────────────────────────────────


def test_the_route_refuses_an_unauthenticated_caller(client):
    assert client.get("/v1/cases/any-id/exemption-analysis").status_code == 401


def test_an_unknown_case_is_not_found(client):
    assert analysis(client, "no-such-case").status_code == 404


def test_another_firms_analysis_is_the_same_404_as_no_case(client):
    case_id = open_case(client, ALICE)
    assert analysis(client, case_id, subject=BOB).status_code == 404


def test_the_static_segment_is_not_shadowed_by_the_collection_routes(client):
    case_id = open_case(client)
    response = analysis(client, case_id)
    assert response.status_code == 200
    body = response.get_json()
    assert body["election"]["state"] is None
    assert body["entries"] == []
    assert any("no residence state" in p for p in body["problems"])


def test_the_exemption_records_own_listing_is_untouched(client):
    # `/exemptions` stays the collection route; the analysis is a sibling.
    case_id = open_case(client)
    response = client.get(f"/v1/cases/{case_id}/exemptions", headers=auth(ALICE))
    assert response.status_code == 200
    assert response.get_json() == {"exemptions": []}


# ── The wiring ──────────────────────────────────────────────────


def test_the_debtors_state_the_liens_and_the_claims_reach_the_arithmetic(client):
    case_id = open_case(client)
    put_debtor_1(client, case_id, state="FL")
    house = add(
        client,
        case_id,
        "assets",
        category="real_property",
        description="12 Byron Court",
        value_entire="300000.00",
    )
    add(
        client,
        case_id,
        "claims",
        claim_class="secured",
        amount="250000.00",
        asset_id=house,
        lien_position=1,
    )
    exemption = add(
        client,
        case_id,
        "exemptions",
        asset_id=house,
        statute_citation="Fla. Const. art. X, § 4(a)(2)",
        amount="1000.00",
        claims_full_fmv=False,
    )

    body = analysis(client, case_id).get_json()

    assert body["election"]["optedOut"] is True
    assert body["election"]["effective"] == "state_and_federal_nonbankruptcy"
    (row,) = body["assets"]
    assert row["assetId"] == house
    assert row["liens"] == "250000.00"
    assert row["netEquity"] == "50000.00"
    assert row["claimed"] == "1000.00"
    assert row["unexempt"] == "49000.00"
    assert row["claims"][0]["exemptionId"] == exemption
    personal = next(
        e for e in body["entries"] if e["entryId"] == "fl-personal-property"
    )
    assert personal["available"] == "0.00"


def test_the_petitions_expected_filing_date_is_the_resolution_date(client):
    case_id = open_case(client)
    put_debtor_1(client, case_id, state="GA")
    add(client, case_id, "petitions", expected_filing_date="2026-06-01")

    body = analysis(client, case_id).get_json()

    assert body["asOf"] == "2026-06-01"
    assert body["asOfSource"] == "expected_filing_date"
    assert body["lookbacks"]["section522p"] == "2023-02-02"
    # Georgia's series begins 2026-07-01: the registry refuses, and says so.
    assert any("no release of exemptions/ga" in p for p in body["problems"])


def test_the_stored_election_changes_the_table(client):
    case_id = open_case(client)
    put_debtor_1(client, case_id, state="TX")

    before = analysis(client, case_id).get_json()
    assert before["election"]["effective"] is None
    assert before["entries"] == []

    response = client.patch(
        f"/v1/cases/{case_id}",
        json={"exemption_set": "federal"},
        headers=auth(ALICE),
    )
    assert response.status_code == 200, response.get_json()

    after = analysis(client, case_id).get_json()
    assert after["election"]["stored"] == "federal"
    assert after["election"]["effective"] == "federal"
    assert any(e["entryId"] == "us-homestead" for e in after["entries"])


def test_money_is_a_string_never_a_json_number(client):
    case_id = open_case(client)
    put_debtor_1(client, case_id, state="FL")
    add(client, case_id, "assets", category="vehicle", value_entire="8412.66")

    (row,) = analysis(client, case_id).get_json()["assets"]

    assert row["currentValue"] == "8412.66"
    assert isinstance(row["liens"], str)
