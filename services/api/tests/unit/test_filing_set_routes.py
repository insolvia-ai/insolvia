"""`GET /v1/cases/<id>/filing-set` (ADR 0024 build PR 3).

What the route REFUSES is the point, as for every case child resource; the
filing set's own content is test_filing_set.py's. Tokens are signed for real
via the forms hub route tests' helpers. Every identifier is fake.
"""

from __future__ import annotations

import pytest
from insolvia_api.adapters.memory.mailer_client import InMemoryMailerClient
from insolvia_api.adapters.memory.packet_store import MemoryPacketStore
from insolvia_api.adapters.memory.waitlist_store import MemoryWaitlistStore
from insolvia_api.api.app_factory import create_app
from insolvia_api.api.dependencies import ApiDependencies
from insolvia_api.core.config import load_config
from insolvia_core.adapters.memory.access_log import MemoryAccessLog
from insolvia_core.adapters.memory.case_entity_store import MemoryCaseEntityStore
from insolvia_core.adapters.memory.case_store import MemoryCaseStore
from insolvia_core.adapters.memory.debtor_store import MemoryDebtorStore
from insolvia_core.adapters.memory.document_blobs import MemoryDocumentBlobStore
from insolvia_core.adapters.memory.firm_store import MemoryFirmStore
from insolvia_core.adapters.memory.jwks_provider import StaticJwksProvider
from insolvia_core.adapters.memory.tax_id_cipher import LocalTaxIdCipher
from insolvia_core.adapters.memory.tax_id_store import MemoryTaxIdStore
from insolvia_core.firms import Firm

from tests.unit.opening import with_client
from tests.unit.test_forms_hub_routes import (
    _PUBLIC_KEY,
    ALICE,
    BOB,
    CLIENT_ID,
    FIRM_A,
    FIRM_B,
    ISSUER,
    KID,
    auth,
    member,
)


@pytest.fixture
def access_log():
    return MemoryAccessLog()


@pytest.fixture
def client(access_log):
    firms = MemoryFirmStore()
    for firm_id in (FIRM_A, FIRM_B):
        firms.create_firm(
            Firm(
                id=firm_id,
                name=f"Firm {firm_id[-4:]}",
                status="active",
                created_at="2026-01-01T00:00:00.000Z",
                updated_at="2026-01-01T00:00:00.000Z",
            )
        )
    firms.add_user(member(ALICE, FIRM_A))
    firms.add_user(member(BOB, FIRM_B))
    debtors = MemoryDebtorStore()
    case_store = MemoryCaseStore(debtor_store=debtors)
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
            case_store=case_store,
            firm_store=firms,
            access_log=access_log,
            debtor_store=debtors,
            tax_id_store=MemoryTaxIdStore(),
            tax_id_cipher=LocalTaxIdCipher(),
            case_entity_store=MemoryCaseEntityStore(),
            document_blobs=MemoryDocumentBlobStore(),
            packet_store=MemoryPacketStore(case_store),
        )
    )
    return app.test_client()


def open_case(client, court="txwb", division="austin"):
    response = client.post(
        "/v1/cases",
        json=with_client(
            client, auth(ALICE), {"chapter": 7, "court": court, "division": division}
        ),
        headers=auth(ALICE),
    )
    assert response.status_code == 201
    return response.get_json()["id"]


def test_an_unauthenticated_caller_is_refused(client):
    assert client.get("/v1/cases/any-id/filing-set").status_code == 401


def test_an_unknown_case_is_not_found(client):
    response = client.get("/v1/cases/no-such-case/filing-set", headers=auth(ALICE))
    assert response.status_code == 404


def test_another_firms_case_is_the_same_404(client, access_log):
    case_id = open_case(client)
    response = client.get(f"/v1/cases/{case_id}/filing-set", headers=auth(BOB))
    assert response.status_code == 404
    assert any(
        e.action == "case.read" and e.outcome == "denied" and e.principal == BOB
        for e in access_log.events
    )


def test_a_new_case_gets_its_courts_filing_set_and_what_is_missing(client):
    case_id = open_case(client)
    response = client.get(f"/v1/cases/{case_id}/filing-set", headers=auth(ALICE))
    assert response.status_code == 200
    body = response.get_json()
    assert body["court"] == {
        "code": "txwb",
        "name": "Western District of Texas",
        "divisionName": "Austin Division",
    }
    assert body["filingMethod"] == "hand_off"
    assert body["registryRelease"].startswith("courts/us-bankruptcy@")
    assert "packet" not in body
    keys = [d["key"] for d in body["documents"]]
    assert keys[0] == "form/b101"
    assert "creditor_matrix" in keys
    b121 = next(d for d in body["documents"] if d["key"] == "form/b121")
    # TXWB's registry record says B121 is not filed there (unverified).
    assert b121["handling"] == "not_filed"
    checklist = {item["id"]: item for item in body["checklist"]}
    assert checklist["packet"]["status"] == "missing"
    assert checklist["packet"]["link"] == "packet"
    assert checklist["ssn_statement"]["status"] == "confirm"
    for item in body["checklist"]:
        assert set(item) <= {"id", "status", "title", "detail", "link"}
        assert item["status"] in {"ready", "missing", "action", "confirm"}
