"""The case lifecycle over HTTP (issue 14.3 / #355): the funnel to filed with
its docket facts, the history a move writes, `first_retained_at` stamped by
the retained transition, the archive as a second view of the list, soft
delete, and copy case naming its source in provenance.

The domain rules themselves — which moves exist, what a filing needs — are
packages/insolvia_core's test_case_lifecycle.py; this file pins what the
routes do with them: who may, what is logged, what the wire says.
"""

from __future__ import annotations

from datetime import date

import pytest
from insolvia_api.adapters.memory.event_store import MemoryEventStore
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

from tests.unit.opening import add_client
from tests.unit.test_cases import (
    _PUBLIC_KEY,
    ALICE,
    BOB,
    CAROL,
    CLIENT_ID,
    DANA,
    FIRM_A,
    FIRM_B,
    ISSUER,
    KID,
    TAMPA,
    auth,
    firm,
    member,
)

FILING = {"status": "filed", "filed_at": "2026-09-01", "case_number": "8:26-bk-01234"}
TYPED = {"source": "staff_typed"}


@pytest.fixture
def debtors():
    return MemoryDebtorStore()


@pytest.fixture
def store(debtors):
    return MemoryCaseStore(debtor_store=debtors)


@pytest.fixture
def access_log():
    return MemoryAccessLog()


@pytest.fixture
def firms():
    firm_store = MemoryFirmStore()
    firm_store.create_firm(firm(FIRM_A, "Example & Partners"))
    firm_store.create_firm(firm(FIRM_B, "Other Firm LLP"))
    firm_store.add_user(member(ALICE, is_admin=True))
    firm_store.add_user(member(DANA, access_all_cases=True))
    firm_store.add_user(member(BOB, role="paralegal"))
    firm_store.add_user(member(CAROL, FIRM_B, is_admin=True))
    return firm_store


@pytest.fixture
def entities():
    return MemoryCaseEntityStore()


@pytest.fixture
def tax_ids():
    return MemoryTaxIdStore()


@pytest.fixture
def client(store, access_log, firms, debtors, entities, tax_ids):
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
            case_store=store,
            access_log=access_log,
            firm_store=firms,
            debtor_store=debtors,
            tax_id_store=tax_ids,
            tax_id_cipher=LocalTaxIdCipher(),
            case_entity_store=entities,
            event_store=MemoryEventStore(),
        )
    )
    return app.test_client()


def open_case(client, subject=ALICE, **extra):
    client_id = add_client(client, auth(subject))
    response = client.post(
        "/v1/cases",
        json={**TAMPA, "client_ids": [client_id], **extra},
        headers=auth(subject),
    )
    assert response.status_code == 201, response.get_json()
    return response.get_json(), client_id


def patch(client, case_id, body, subject=ALICE):
    return client.patch(f"/v1/cases/{case_id}", json=body, headers=auth(subject))


def get_client(client, client_id, subject=ALICE):
    return client.get(f"/v1/firm/clients/{client_id}", headers=auth(subject))


def listed(client, subject=ALICE, query=""):
    response = client.get(f"/v1/cases{query}", headers=auth(subject))
    assert response.status_code == 200
    return [case["id"] for case in response.get_json()["cases"]]


# ── The funnel to filed ────────────────────────────────────────────


def test_a_prospect_is_funnelled_to_filed_with_its_docket_facts(client):
    case, _ = open_case(client, status="prospect")
    assert (case["status"], case["prospectStage"]) == ("prospect", "possible")
    steps = [
        {"prospect_stage": "consultation_scheduled"},
        {"prospect_stage": "awaiting_signed_agreement"},
        {"status": "intake"},
        {"status": "ready_to_file"},
        {
            **FILING,
            "meeting_341_at": "2026-10-05",
            "judge": "Hon. Example Judge",
            "trustee": "Example Trustee",
            "office_file_number": "F-1001",
        },
    ]
    for body in steps:
        response = patch(client, case["id"], body)
        assert response.status_code == 200, response.get_json()

    filed = response.get_json()
    assert filed["status"] == "filed"
    assert "prospectStage" not in filed
    assert {key: filed[key] for key in ("caseNumber", "judge", "trustee")} == {
        "caseNumber": "8:26-bk-01234",
        "judge": "Hon. Example Judge",
        "trustee": "Example Trustee",
    }
    assert filed["officeFileNumber"] == "F-1001"
    assert (filed["filedAt"], filed["meeting341At"]) == ("2026-09-01", "2026-10-05")

    history = client.get(
        f"/v1/cases/{case['id']}/status-history", headers=auth(ALICE)
    ).get_json()["history"]
    assert [(h["fromStatus"], h["toStatus"]) for h in history] == [
        ("prospect", "prospect"),
        ("prospect", "prospect"),
        ("prospect", "intake"),
        ("intake", "ready_to_file"),
        ("ready_to_file", "filed"),
    ]
    assert history[0]["toStage"] == "consultation_scheduled"
    assert all(h["changedBy"] == ALICE for h in history)


def test_filing_without_the_case_number_is_refused_naming_it(client):
    case, _ = open_case(client)
    response = patch(client, case["id"], {"status": "filed", "filed_at": "2026-09-01"})
    assert response.status_code == 400
    assert set(response.get_json()["fields"]) == {"case_number"}


def test_a_filed_case_cannot_be_moved_back_into_preparation(client):
    case, _ = open_case(client)
    patch(client, case["id"], FILING)
    response = patch(client, case["id"], {"status": "intake"})
    assert response.status_code == 409


def test_a_status_history_is_as_private_as_its_case(client):
    case, _ = open_case(client)
    assert (
        client.get(
            f"/v1/cases/{case['id']}/status-history", headers=auth(CAROL)
        ).status_code
        == 404
    )


# ── first_retained_at ──────────────────────────────────────────────


def test_the_retained_transition_stamps_first_retained_at(client):
    case, client_id = open_case(client, status="prospect")
    assert "first_retained_at" not in get_client(client, client_id).get_json()

    patch(client, case["id"], {"status": "intake"})

    stamped = get_client(client, client_id).get_json()
    assert stamped["first_retained_at"] == date.today().isoformat()


def test_an_earlier_retained_date_is_never_overwritten(client):
    client_id = add_client(client, auth(ALICE), first_retained_at="2020-01-02")
    response = client.post(
        "/v1/cases",
        json={**TAMPA, "client_ids": [client_id], "status": "prospect"},
        headers=auth(ALICE),
    )
    patch(client, response.get_json()["id"], {"status": "intake"})
    assert get_client(client, client_id).get_json()["first_retained_at"] == (
        "2020-01-02"
    )


def test_a_case_opened_retained_stamps_its_client(client):
    _, client_id = open_case(client)
    assert get_client(client, client_id).get_json()["first_retained_at"] == (
        date.today().isoformat()
    )


# ── Archive ────────────────────────────────────────────────────────


def test_an_archived_case_leaves_the_default_list_but_not_the_firms_records(
    client, access_log
):
    case, _ = open_case(client)
    kept, _ = open_case(client)

    response = client.put(
        f"/v1/cases/{case['id']}/archived", json={"archived": True}, headers=auth(ALICE)
    )

    assert response.status_code == 200
    assert response.get_json()["archivedBy"] == ALICE
    assert listed(client) == [kept["id"]]
    assert listed(client, query="?archived=true") == [case["id"]]
    # Still the firm's record: it reads by id.
    assert client.get(f"/v1/cases/{case['id']}", headers=auth(ALICE)).status_code == 200
    assert access_log.events[-2].action == "case.update"


def test_a_restored_case_returns_to_the_working_list(client):
    case, _ = open_case(client)
    url = f"/v1/cases/{case['id']}/archived"
    client.put(url, json={"archived": True}, headers=auth(ALICE))
    restored = client.put(url, json={"archived": False}, headers=auth(ALICE))
    assert "archivedAt" not in restored.get_json()
    assert listed(client) == [case["id"]]


@pytest.mark.parametrize("value", ["yes", "1", "TRUE"])
def test_an_unknown_archive_view_is_refused(client, value):
    response = client.get(f"/v1/cases?archived={value}", headers=auth(ALICE))
    assert response.status_code == 400


def test_archiving_another_firms_case_is_404(client):
    case, _ = open_case(client)
    response = client.put(
        f"/v1/cases/{case['id']}/archived", json={"archived": True}, headers=auth(CAROL)
    )
    assert response.status_code == 404


# ── Delete ─────────────────────────────────────────────────────────


def test_an_admin_deletes_an_unfiled_case_and_it_is_gone_from_every_read(
    client, access_log
):
    case, client_id = open_case(client)

    response = client.delete(f"/v1/cases/{case['id']}", headers=auth(ALICE))

    assert response.status_code == 204
    assert access_log.events[-1].action == "case.delete"
    for path in (
        f"/v1/cases/{case['id']}",
        f"/v1/cases/{case['id']}/debtors",
        f"/v1/cases/{case['id']}/status-history",
    ):
        assert client.get(path, headers=auth(ALICE)).status_code == 404
    assert case["id"] not in listed(client)
    assert case["id"] not in listed(client, query="?archived=true")
    client_cases = client.get(
        f"/v1/firm/clients/{client_id}/cases", headers=auth(ALICE)
    ).get_json()
    assert client_cases["cases"] == []


def test_deleting_is_a_firm_admins_act(client, store):
    case, _ = open_case(client, DANA)
    response = client.delete(f"/v1/cases/{case['id']}", headers=auth(DANA))
    assert response.status_code == 403
    assert not store.cases[case["id"]].deleted


def test_a_filed_case_cannot_be_deleted(client):
    case, _ = open_case(client)
    patch(client, case["id"], FILING)
    assert (
        client.delete(f"/v1/cases/{case['id']}", headers=auth(ALICE)).status_code == 409
    )


def test_deleting_another_firms_case_is_404_and_logged_denied(client, access_log):
    case, _ = open_case(client)
    response = client.delete(f"/v1/cases/{case['id']}", headers=auth(CAROL))
    assert response.status_code == 404
    assert (access_log.events[-1].action, access_log.events[-1].outcome) == (
        "case.delete",
        "denied",
    )


# ── Copy ───────────────────────────────────────────────────────────


def test_a_copy_is_a_new_case_whose_values_name_their_source(client, store):
    source, client_id = open_case(client)
    creditor = client.post(
        f"/v1/cases/{source['id']}/creditors",
        json={"name": "Example Bank", "provenance": {"name": TYPED}},
        headers=auth(ALICE),
    ).get_json()
    patch(client, source["id"], {**FILING, "judge": "Hon. Example"})

    response = client.post(f"/v1/cases/{source['id']}/copy", headers=auth(ALICE))

    assert response.status_code == 201, response.get_json()
    copy = response.get_json()
    assert copy["id"] != source["id"]
    assert copy["status"] == "intake"
    assert "caseNumber" not in copy
    assert "judge" not in copy
    (debtor_1,) = client.get(
        f"/v1/cases/{copy['id']}/debtors", headers=auth(ALICE)
    ).get_json()["debtors"]
    assert debtor_1["client_id"] == client_id
    sources = {e["copied_from_case_id"] for e in debtor_1["provenance"].values()}
    assert sources == {source["id"]}
    (copied,) = client.get(
        f"/v1/cases/{copy['id']}/creditors", headers=auth(ALICE)
    ).get_json()["creditors"]
    assert copied["id"] == creditor["id"]
    assert copied["provenance"]["name"] == {
        "source": "staff_typed",
        "copied_from_case_id": source["id"],
    }
    # The copy appears in its client's list beside the source.
    listed_for_client = client.get(
        f"/v1/firm/clients/{client_id}/cases", headers=auth(ALICE)
    ).get_json()["cases"]
    assert {entry["case"]["id"] for entry in listed_for_client} == {
        source["id"],
        copy["id"],
    }


def test_a_copy_of_a_case_whose_client_is_archived_is_refused(client):
    source, client_id = open_case(client)
    client.put(
        f"/v1/firm/clients/{client_id}/status",
        json={"status": "archived"},
        headers=auth(ALICE),
    )
    response = client.post(f"/v1/cases/{source['id']}/copy", headers=auth(ALICE))
    assert response.status_code == 409


def test_copying_a_case_the_caller_cannot_see_is_404(client):
    source, _ = open_case(client)
    response = client.post(f"/v1/cases/{source['id']}/copy", headers=auth(BOB))
    assert response.status_code == 404


def test_a_copy_carries_the_sealed_tax_id_without_opening_it(client, tax_ids):
    source, _ = open_case(client)
    (stored,) = client.get(
        f"/v1/cases/{source['id']}/debtors", headers=auth(ALICE)
    ).get_json()["debtors"]
    saved = client.put(
        f"/v1/cases/{source['id']}/debtors/debtor_1",
        json={
            "name": stored["name"],
            # 987-65-4321: the SSA's never-issued advertising block.
            "tax_id": {"kind": "ssn", "value": "987-65-4321"},
            "provenance": {**stored["provenance"], "tax_id": TYPED},
        },
        headers=auth(ALICE),
    )
    assert saved.status_code == 200, saved.get_json()

    copy_id = client.post(
        f"/v1/cases/{source['id']}/copy", headers=auth(ALICE)
    ).get_json()["id"]

    (copied,) = client.get(
        f"/v1/cases/{copy_id}/debtors", headers=auth(ALICE)
    ).get_json()["debtors"]
    assert copied["tax_id"] == {"kind": "ssn", "last_four": "4321"}
    assert [case_id for case_id, _ in tax_ids.items] == [source["id"], copy_id]
