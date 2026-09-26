"""`GET /v1/courts` (issue #360) — the registry as a client reads it.

The registry's own invariants live in packages/insolvia_core's test_courts.py;
this file pins the wire shape and the auth posture: authenticated, every
member of a firm, no feature gate.

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
from insolvia_core.adapters.memory.firm_store import MemoryFirmStore
from insolvia_core.adapters.memory.jwks_provider import StaticJwksProvider
from insolvia_core.firms import Firm, FirmUser, default_permissions

ISSUER = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE00"
CLIENT_ID = "exampleappclientid000000"
FIRM_A = "00000000-0000-4000-8000-00000000f18a"
GREG = "00000000-0000-4000-8000-000000009e69"  # staff — role defaults only
FRANK = "00000000-0000-4000-8000-00000000f4a2"  # in no firm
KID = "test-key-1"

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


@pytest.fixture
def client():
    firms = MemoryFirmStore()
    firms.create_firm(
        Firm(
            id=FIRM_A,
            name="Example & Partners",
            status="active",
            created_at="2026-01-01T00:00:00.000Z",
            updated_at="2026-01-01T00:00:00.000Z",
        )
    )
    firms.add_user(
        FirmUser(
            firm_id=FIRM_A,
            subject=GREG,
            email="greg@example.test",
            first_name="Greg",
            last_name="Staff",
            role="staff",
            is_admin=False,
            access_all_cases=False,
            permissions=default_permissions("staff"),
            status="active",
            created_at="2026-01-01T00:00:00.000Z",
            updated_at="2026-01-01T00:00:00.000Z",
        )
    )
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
            firm_store=firms,
        )
    )
    return app.test_client()


def test_the_registry_needs_a_token(client):
    assert client.get("/v1/courts").status_code == 401


def test_a_caller_in_no_firm_is_403(client):
    assert client.get("/v1/courts", headers=auth(FRANK)).status_code == 403


def test_every_member_reads_the_ten_launch_districts(client):
    # Staff with role defaults only — no feature gate applies here.
    body = client.get("/v1/courts", headers=auth(GREG)).get_json()
    assert body["releaseId"] == "courts/us-bankruptcy@2026-09-24"
    assert body["effectiveDate"] == "2026-09-24"
    assert [d["code"] for d in body["districts"]] == [
        "flmb", "flnb", "flsb", "gamb", "ganb", "gasb",
        "txeb", "txnb", "txsb", "txwb",
    ]  # fmt: skip


def test_a_district_carries_what_a_picker_and_a_caption_need(client):
    body = client.get("/v1/courts", headers=auth(GREG)).get_json()
    flmb = next(d for d in body["districts"] if d["code"] == "flmb")
    assert flmb["name"] == "Middle District of Florida"
    assert flmb["courtId"] == "FLMBK"
    assert flmb["state"] == "FL"
    assert flmb["circuit"] == 11
    assert flmb["caseUpload"] == {"status": "unverified", "verifiedAt": None}
    tampa = next(d for d in flmb["divisions"] if d["code"] == "tampa")
    assert tampa["name"] == "Tampa Division"
    assert tampa["officeCode"] == "8"
    assert tampa["officeCodeVerified"] is True
    assert tampa["courthouse"]["city"] == "Tampa"
    assert {"name": "Hillsborough", "fips": "12057"} in tampa["counties"]


def test_an_unverified_office_code_is_null_and_says_so(client):
    body = client.get("/v1/courts", headers=auth(GREG)).get_json()
    txsb = next(d for d in body["districts"] if d["code"] == "txsb")
    houston = next(d for d in txsb["divisions"] if d["code"] == "houston")
    assert houston["officeCode"] is None
    assert houston["officeCodeVerified"] is False
