"""The packet endpoints (issue #96): list and download-URL minting.

What matters most, as for every case child resource: what these REFUSE — the
case lookup is the only authorisation there is. Tokens are signed for real,
mirroring tests/test_job_routes.py. Every identifier below is obviously
fake; this repo is public.
"""

from __future__ import annotations

import time

import jwt
import pytest
from cryptography.hazmat.primitives.asymmetric import rsa
from insolvia_api.adapters.memory.mailer_client import InMemoryMailerClient
from insolvia_api.adapters.memory.packet_store import MemoryPacketStore
from insolvia_api.adapters.memory.waitlist_store import MemoryWaitlistStore
from insolvia_api.api.app_factory import create_app
from insolvia_api.api.dependencies import ApiDependencies
from insolvia_api.core.config import load_config
from insolvia_api.core.packets import new_packet
from insolvia_core.adapters.memory.access_log import MemoryAccessLog
from insolvia_core.adapters.memory.case_store import MemoryCaseStore
from insolvia_core.adapters.memory.document_blobs import MemoryDocumentBlobStore
from insolvia_core.adapters.memory.firm_store import MemoryFirmStore
from insolvia_core.adapters.memory.jwks_provider import StaticJwksProvider
from insolvia_core.cases import pin_case
from insolvia_core.firms import Firm, FirmUser, default_permissions

ISSUER = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE00"
CLIENT_ID = "exampleappclientid000000"
FIRM_A = "00000000-0000-4000-8000-00000000f18a"
FIRM_B = "00000000-0000-4000-8000-00000000f18b"
ALICE = "00000000-0000-4000-8000-00000000a11c"
BOB = "00000000-0000-4000-8000-00000000b0b0"
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


def member(subject: str, firm_id: str) -> FirmUser:
    return FirmUser(
        firm_id=firm_id,
        subject=subject,
        email=f"{subject[-4:]}@example.test",
        first_name="Person",
        last_name=subject[-4:],
        role="attorney",
        is_admin=True,
        access_all_cases=True,
        permissions=default_permissions("attorney"),
        status="active",
        created_at="2026-01-01T00:00:00.000Z",
        updated_at="2026-01-01T00:00:00.000Z",
    )


@pytest.fixture
def stores():
    case_store = MemoryCaseStore()
    return {
        "case_store": case_store,
        "packet_store": MemoryPacketStore(case_store),
        "blobs": MemoryDocumentBlobStore(),
        "access_log": MemoryAccessLog(),
    }


@pytest.fixture
def client(stores):
    firms = MemoryFirmStore()
    for firm_id, name in ((FIRM_A, "Example & Partners"), (FIRM_B, "Other Firm LLP")):
        firms.create_firm(
            Firm(
                id=firm_id,
                name=name,
                status="active",
                created_at="2026-01-01T00:00:00.000Z",
                updated_at="2026-01-01T00:00:00.000Z",
            )
        )
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
            case_store=stores["case_store"],
            firm_store=firms,
            access_log=stores["access_log"],
            document_blobs=stores["blobs"],
            packet_store=stores["packet_store"],
        )
    )
    return app.test_client()


def open_case(client, subject=ALICE):
    response = client.post(
        "/v1/cases", json={"chapter": 7, "district": "MDFL"}, headers=auth(subject)
    )
    assert response.status_code == 201
    return response.get_json()["id"]


def store_packet(stores, case_id):
    """A packet the way the worker leaves one: bytes in the blob store, the
    record and the pins written together."""
    packet = new_packet(
        case_id=case_id,
        job_id="00000000-0000-4000-8000-00000000c0de",
        byte_size=3,
        sha256="cd" * 32,
        form_revisions={"form/b101": "2024-06-22"},
        constants_set_id="code/dollar-amounts@2025-04-01",
        creditor_count=2,
        created_by=ALICE,
    )
    stores["blobs"].put_bytes(
        packet.storage_ref, content=b"zip", content_type="application/zip"
    )
    case = stores["case_store"].cases[case_id]
    created = stores["packet_store"].create(
        packet,
        pinned_case=pin_case(
            case,
            form_revisions=packet.form_revisions,
            constants_set_id=packet.constants_set_id,
        ),
        expected_updated_at=case.updated_at,
    )
    assert created
    return packet


@pytest.mark.parametrize(
    "path",
    [
        "/v1/cases/any-id/packets",
        "/v1/cases/any-id/packets/any-packet/url",
    ],
)
def test_every_route_refuses_an_unauthenticated_caller(client, path):
    assert client.get(path).status_code == 401


def test_listing_returns_packets_newest_first(client, stores):
    case_id = open_case(client)
    first = store_packet(stores, case_id)
    second = store_packet(stores, case_id)
    body = client.get(f"/v1/cases/{case_id}/packets", headers=auth(ALICE)).get_json()
    ids = [p["id"] for p in body["packets"]]
    assert set(ids) == {first.id, second.id}
    listed = {p["id"]: p for p in body["packets"]}
    assert "storageRef" not in listed[first.id]
    assert listed[first.id]["formRevisions"] == {"form/b101": "2024-06-22"}


def test_another_firms_packets_are_not_found(client, stores):
    case_id = open_case(client, ALICE)
    packet = store_packet(stores, case_id)
    listing = client.get(f"/v1/cases/{case_id}/packets", headers=auth(BOB))
    url = client.get(f"/v1/cases/{case_id}/packets/{packet.id}/url", headers=auth(BOB))
    assert listing.status_code == url.status_code == 404


def test_a_packet_id_does_not_resolve_through_another_case(client, stores):
    first = open_case(client)
    second = open_case(client)
    packet = store_packet(stores, first)
    response = client.get(
        f"/v1/cases/{second}/packets/{packet.id}/url", headers=auth(ALICE)
    )
    assert response.status_code == 404


def test_the_url_route_mints_a_short_lived_get(client, stores):
    case_id = open_case(client)
    packet = store_packet(stores, case_id)
    response = client.get(
        f"/v1/cases/{case_id}/packets/{packet.id}/url", headers=auth(ALICE)
    )
    assert response.status_code == 200
    body = response.get_json()
    assert body["method"] == "GET"
    assert "expiresAt" in body
    minted = stores["blobs"].minted[-1]
    assert minted.method == "GET"
    assert minted.storage_ref == packet.storage_ref
    assert minted.expires_in == 5 * 60


def test_the_download_is_access_logged(client, stores):
    case_id = open_case(client)
    packet = store_packet(stores, case_id)
    client.get(f"/v1/cases/{case_id}/packets/{packet.id}/url", headers=auth(ALICE))
    assert any(
        e.action == "packet.download" and e.case_id == case_id
        for e in stores["access_log"].events
    )


def test_a_pinned_case_reports_its_revisions(client, stores):
    """The pin surfaces on the case body once assembly writes it — the app
    renders which printed revisions the packet used."""
    case_id = open_case(client)
    store_packet(stores, case_id)
    body = client.get(f"/v1/cases/{case_id}", headers=auth(ALICE)).get_json()
    assert body["formRevisions"] == {"form/b101": "2024-06-22"}


def test_an_unpinned_case_omits_the_revisions(client):
    case_id = open_case(client)
    body = client.get(f"/v1/cases/{case_id}", headers=auth(ALICE)).get_json()
    assert "formRevisions" not in body
    assert "constantsSetId" not in body
