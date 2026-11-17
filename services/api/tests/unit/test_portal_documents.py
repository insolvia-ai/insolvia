"""Document request checklists (ADR 0023 PR 5 / #364): the firm's checklist,
a case's requests, and the client's upload that satisfies one.

The ADR's done-when, held down here against the memory stores: a case shows
requested documents arrived against outstanding, and a client upload
satisfies its request. Around it, the properties that make that safe — the
kind comes from the request, a client sees only their own uploads, a
client's upload is never auto-extracted, and every portal request is on the
access log as the client.

Tokens are signed for real (test_portal_routes.py's helpers). Every
identifier below is obviously fake. This repo is public.
"""

from __future__ import annotations

import inspect

import pytest
from insolvia_api.adapters.memory.job_queue import MemoryJobQueue
from insolvia_api.adapters.memory.job_store import MemoryJobStore
from insolvia_api.adapters.memory.mailer_client import InMemoryMailerClient
from insolvia_api.adapters.memory.waitlist_store import MemoryWaitlistStore
from insolvia_api.api.app_factory import create_app
from insolvia_api.api.dependencies import ApiDependencies
from insolvia_api.api.routes import portal_documents as portal_documents_routes
from insolvia_api.core.config import load_config
from insolvia_core.adapters.memory.access_log import MemoryAccessLog
from insolvia_core.adapters.memory.case_store import MemoryCaseStore
from insolvia_core.adapters.memory.client_binding_store import (
    MemoryClientBindingStore,
)
from insolvia_core.adapters.memory.document_blobs import MemoryDocumentBlobStore
from insolvia_core.adapters.memory.document_request_store import (
    MemoryDocumentRequestStore,
)
from insolvia_core.adapters.memory.document_store import MemoryDocumentStore
from insolvia_core.adapters.memory.firm_store import MemoryFirmStore
from insolvia_core.adapters.memory.jwks_provider import StaticJwksProvider
from insolvia_core.adapters.memory.user_directory import MemoryUserDirectory
from insolvia_core.document_requests import DEFAULT_CHECKLIST
from insolvia_core.firms import ADD_EDIT, CLIENT_PORTAL, FIRM_ADMINISTRATION

from tests.unit.opening import with_client
from tests.unit.test_portal_routes import (
    _PRIVATE_KEY,
    ALICE,
    BOB,
    CAROL,
    FIRM_A,
    FIRM_B,
    ISSUER,
    KID,
    PORTAL_CLIENT_ID,
    STAFF_CLIENT_ID,
    firm,
    member,
    portal,
    staff,
)

UPLOAD = {
    "fileName": "statement.pdf",
    "contentType": "application/pdf",
    "byteSize": 2048,
}


class World:
    """Every store, so a test can look behind the API."""

    def __init__(self) -> None:
        self.firms = MemoryFirmStore()
        self.firms.create_firm(firm(FIRM_A, "Example & Partners"))
        self.firms.create_firm(firm(FIRM_B, "Other Firm LLP"))
        self.firms.add_user(
            member(
                ALICE,
                is_admin=True,
                grants={CLIENT_PORTAL: ADD_EDIT, FIRM_ADMINISTRATION: ADD_EDIT},
            )
        )
        self.firms.add_user(member(BOB))
        self.firms.add_user(member(CAROL, FIRM_B, is_admin=True))
        self.access_log = MemoryAccessLog()
        self.documents = MemoryDocumentStore()
        self.blobs = MemoryDocumentBlobStore()
        self.requests = MemoryDocumentRequestStore()
        self.jobs = MemoryJobStore()
        self.queue = MemoryJobQueue()
        self.app = create_app(
            ApiDependencies(
                config=load_config(
                    {
                        "INSOLVIA_ENV": "local",
                        "AUTH_ISSUER_URL": ISSUER,
                        "AUTH_CLIENT_ID": STAFF_CLIENT_ID,
                        "AUTH_PORTAL_CLIENT_ID": PORTAL_CLIENT_ID,
                    }
                ),
                waitlist_store=MemoryWaitlistStore(),
                mailer=InMemoryMailerClient(),
                jwks_provider=StaticJwksProvider({KID: _PRIVATE_KEY.public_key()}),
                case_store=MemoryCaseStore(),
                access_log=self.access_log,
                firm_store=self.firms,
                user_directory=MemoryUserDirectory(),
                client_binding_store=MemoryClientBindingStore(),
                document_store=self.documents,
                document_blobs=self.blobs,
                document_request_store=self.requests,
                job_store=self.jobs,
                job_queue=self.queue,
            )
        )
        self.client = self.app.test_client()

    def open_case(self, subject: str = ALICE) -> str:
        response = self.client.post(
            "/v1/cases",
            json=with_client(
                self.client,
                staff(subject),
                {"chapter": 7, "court": "flmb", "division": "tampa"},
            ),
            headers=staff(subject),
        )
        assert response.status_code == 201, response.get_json()
        return str(response.get_json()["id"])

    def invite(self, case_id: str, email: str = "pat@example.test", **body) -> str:
        response = self.client.post(
            f"/v1/cases/{case_id}/portal/invitation",
            json={"email": email, "displayName": "Pat Example", **body},
            headers=staff(ALICE),
        )
        assert response.status_code == 201, response.get_json()
        return str(response.get_json()["subject"])

    def apply_checklist(self, case_id: str) -> dict:
        response = self.client.post(
            f"/v1/cases/{case_id}/document-requests/from-checklist",
            headers=staff(ALICE),
        )
        assert response.status_code == 200, response.get_json()
        return response.get_json()

    def staff_requests(self, case_id: str) -> dict:
        response = self.client.get(
            f"/v1/cases/{case_id}/document-requests", headers=staff(ALICE)
        )
        assert response.status_code == 200, response.get_json()
        return response.get_json()

    def client_upload(self, subject: str, request_id: str, **body) -> dict:
        """Create, PUT (the blob store's stand-in) and complete."""
        created = self.client.post(
            "/v1/portal/documents",
            json={**UPLOAD, "requestId": request_id, **body},
            headers=portal(subject),
        )
        assert created.status_code == 201, created.get_json()
        document = created.get_json()["document"]
        # The client's answer names no case, so find the row behind the API.
        (stored,) = [
            d for d in self.documents.documents.values() if d.id == document["id"]
        ]
        self.blobs.accept_upload(stored.storage_ref, byte_size=stored.byte_size)
        completed = self.client.post(
            f"/v1/portal/documents/{document['id']}/complete",
            headers=portal(subject),
        )
        assert completed.status_code == 200, completed.get_json()
        return completed.get_json()["document"]


@pytest.fixture
def world() -> World:
    return World()


def by_title(requests: list[dict], title: str) -> dict:
    (found,) = [r for r in requests if r["title"] == title]
    return found


# ── The ADR's done-when ─────────────────────────────────────────


def test_a_client_upload_satisfies_its_request_and_the_case_counts_it(world):
    case_id = world.open_case()
    pat = world.invite(case_id)
    applied = world.apply_checklist(case_id)
    assert applied["added"] == len(DEFAULT_CHECKLIST)
    assert applied["progress"] == {
        "total": 6,
        "received": 0,
        "outstanding": 6,
        "waived": 0,
    }

    seen = world.client.get("/v1/portal/document-requests", headers=portal(pat))
    assert seen.status_code == 200
    bank = by_title(seen.get_json()["requests"], "Bank statements")
    assert bank["status"] == "requested"

    uploaded = world.client_upload(pat, bank["id"])
    assert uploaded["status"] == "stored"

    listed = world.staff_requests(case_id)
    assert by_title(listed["requests"], "Bank statements")["status"] == "received"
    assert by_title(listed["requests"], "Bank statements")["documentIds"] == [
        uploaded["id"]
    ]
    assert listed["progress"] == {
        "total": 6,
        "received": 1,
        "outstanding": 5,
        "waived": 0,
    }

    mine = world.client.get(
        "/v1/portal/document-requests", headers=portal(pat)
    ).get_json()
    assert by_title(mine["requests"], "Bank statements")["status"] == "received"
    assert [
        u["id"] for u in by_title(mine["requests"], "Bank statements")["uploads"]
    ] == [uploaded["id"]]
    assert mine["progress"]["received"] == 1


def test_a_client_upload_lands_on_the_case_as_the_clients_with_the_requests_kind(
    world,
):
    case_id = world.open_case()
    pat = world.invite(case_id)
    requests = world.apply_checklist(case_id)["requests"]
    tax = by_title(requests, "Tax returns for the last two years")

    uploaded = world.client_upload(pat, tax["id"], kind="credit_report")

    staff_view = world.client.get(
        f"/v1/cases/{case_id}/documents", headers=staff(ALICE)
    ).get_json()["documents"]
    (document,) = staff_view
    assert document["id"] == uploaded["id"]
    assert document["kind"] == "tax_return"  # the request's, not the body's
    assert document["channel"] == "client"
    assert document["requestId"] == tax["id"]
    # The same key scheme and the same signed tag as a staff upload.
    minted = world.blobs.minted[-1]
    assert minted.storage_ref == f"cases/{case_id}/{uploaded['id']}"
    assert world.blobs.cleared == [minted.storage_ref]


def test_a_clients_upload_is_never_auto_extracted(world):
    # Pay stubs are an extractable kind: the staff upload below proves the
    # trigger is live in this app, so the client's silence is the channel.
    case_id = world.open_case()
    pat = world.invite(case_id)
    stubs = by_title(
        world.apply_checklist(case_id)["requests"], "Pay stubs for the last six months"
    )

    world.client_upload(pat, stubs["id"])
    assert world.jobs.list_for_case(case_id) == ()
    assert world.queue.messages == []

    created = world.client.post(
        f"/v1/cases/{case_id}/documents",
        json={**UPLOAD, "kind": "pay_stub", "requestId": stubs["id"]},
        headers=staff(ALICE),
    )
    assert created.status_code == 201, created.get_json()
    document = created.get_json()["document"]
    assert document["channel"] == "staff"
    world.blobs.accept_upload(f"cases/{case_id}/{document['id']}", byte_size=2048)
    done = world.client.post(
        f"/v1/cases/{case_id}/documents/{document['id']}/complete",
        headers=staff(ALICE),
    )
    assert done.status_code == 200
    (job,) = world.jobs.list_for_case(case_id)
    assert job.document_id == document["id"]


# ── What a client sees ──────────────────────────────────────────


def test_a_client_sees_their_own_uploads_and_never_a_staff_one(world):
    case_id = world.open_case()
    pat = world.invite(case_id)
    bank = by_title(world.apply_checklist(case_id)["requests"], "Bank statements")
    created = world.client.post(
        f"/v1/cases/{case_id}/documents",
        json={**UPLOAD, "kind": "bank_statement", "requestId": bank["id"]},
        headers=staff(ALICE),
    )
    document = created.get_json()["document"]
    world.blobs.accept_upload(f"cases/{case_id}/{document['id']}", byte_size=2048)
    world.client.post(
        f"/v1/cases/{case_id}/documents/{document['id']}/complete",
        headers=staff(ALICE),
    )

    body = world.client.get(
        "/v1/portal/document-requests", headers=portal(pat)
    ).get_json()

    seen = by_title(body["requests"], "Bank statements")
    # Received — by the firm's upload — and nothing of that upload shown.
    assert seen["status"] == "received"
    assert seen["uploads"] == []
    assert document["id"] not in str(body)
    assert case_id not in str(body)


def test_a_client_cannot_complete_a_staff_upload(world):
    case_id = world.open_case()
    pat = world.invite(case_id)
    created = world.client.post(
        f"/v1/cases/{case_id}/documents",
        json={**UPLOAD, "kind": "other"},
        headers=staff(ALICE),
    )
    document_id = created.get_json()["document"]["id"]

    response = world.client.post(
        f"/v1/portal/documents/{document_id}/complete", headers=portal(pat)
    )

    assert response.status_code == 404


def test_the_portal_upload_answer_names_no_case(world):
    case_id = world.open_case()
    pat = world.invite(case_id)
    bank = by_title(world.apply_checklist(case_id)["requests"], "Bank statements")

    response = world.client.post(
        "/v1/portal/documents",
        json={**UPLOAD, "requestId": bank["id"]},
        headers=portal(pat),
    )

    assert response.status_code == 201
    body = response.get_json()
    assert "caseId" not in body["document"]
    assert body["upload"]["method"] == "PUT"
    assert "x-amz-server-side-encryption" not in body["upload"]["headers"]


# ── What a client cannot upload against ─────────────────────────


def test_a_waived_request_takes_no_upload(world):
    case_id = world.open_case()
    pat = world.invite(case_id)
    bank = by_title(world.apply_checklist(case_id)["requests"], "Bank statements")
    waived = world.client.patch(
        f"/v1/cases/{case_id}/document-requests/{bank['id']}",
        json={"status": "waived"},
        headers=staff(ALICE),
    )
    assert waived.status_code == 200

    response = world.client.post(
        "/v1/portal/documents",
        json={**UPLOAD, "requestId": bank["id"]},
        headers=portal(pat),
    )

    assert response.status_code == 400
    assert "requestId" in response.get_json()["fields"]
    assert world.staff_requests(case_id)["progress"]["waived"] == 1


def test_another_cases_request_does_not_resolve_for_the_client(world):
    mine = world.open_case()
    other = world.open_case()
    pat = world.invite(mine)
    foreign = world.apply_checklist(other)["requests"][0]

    response = world.client.post(
        "/v1/portal/documents",
        json={**UPLOAD, "requestId": foreign["id"]},
        headers=portal(pat),
    )

    assert response.status_code == 400
    assert world.documents.documents == {}


def test_an_upload_names_a_request(world):
    case_id = world.open_case()
    pat = world.invite(case_id)

    response = world.client.post(
        "/v1/portal/documents", json=UPLOAD, headers=portal(pat)
    )

    assert response.status_code == 400
    assert "requestId" in response.get_json()["fields"]


def test_a_revoked_client_cannot_upload(world):
    case_id = world.open_case()
    pat = world.invite(case_id)
    bank = by_title(world.apply_checklist(case_id)["requests"], "Bank statements")
    world.client.delete(
        f"/v1/cases/{case_id}/portal/clients/{pat}", headers=staff(ALICE)
    )

    response = world.client.post(
        "/v1/portal/documents",
        json={**UPLOAD, "requestId": bank["id"]},
        headers=portal(pat),
    )

    assert response.status_code == 403


# ── The access log ──────────────────────────────────────────────


def test_every_portal_document_request_is_logged_as_the_client(world):
    case_id = world.open_case()
    pat = world.invite(case_id)
    bank = by_title(world.apply_checklist(case_id)["requests"], "Bank statements")
    before = len(world.access_log.events)

    world.client.get("/v1/portal/document-requests", headers=portal(pat))
    world.client_upload(pat, bank["id"])

    rows = [
        (e.case_id, e.principal, e.action, e.outcome)
        for e in world.access_log.events[before:]
    ]
    assert rows == [
        (case_id, pat, "portal.read", "allowed"),
        (case_id, pat, "document.create", "allowed"),
        (case_id, pat, "document.create", "allowed"),
    ]


def test_the_portal_documents_module_reads_the_case_only_through_the_binding():
    """ADR 0023 decision 4 at the source, test_portal_routes.py's check for
    this module."""
    source = inspect.getsource(portal_documents_routes)
    assert "accessor=" not in source
    assert "read_for_worker" not in source
    assert ".public_status(" in source


# ── Staff: a case's requests ────────────────────────────────────


def test_applying_the_checklist_twice_adds_nothing_the_second_time(world):
    case_id = world.open_case()
    world.apply_checklist(case_id)

    again = world.apply_checklist(case_id)

    assert again["added"] == 0
    assert len(again["requests"]) == len(DEFAULT_CHECKLIST)


def test_the_case_applies_its_firms_own_checklist(world):
    saved = world.client.put(
        "/v1/firm/document-checklist",
        json={"items": [{"title": "Lease", "kind": "other"}]},
        headers=staff(ALICE),
    )
    assert saved.status_code == 200
    case_id = world.open_case()

    applied = world.apply_checklist(case_id)

    assert [r["title"] for r in applied["requests"]] == ["Lease"]


def test_one_request_can_be_added_and_withdrawn(world):
    case_id = world.open_case()
    made = world.client.post(
        f"/v1/cases/{case_id}/document-requests",
        json={"title": "Divorce decree", "kind": "court_notice"},
        headers=staff(ALICE),
    )
    assert made.status_code == 201
    request_id = made.get_json()["id"]

    gone = world.client.delete(
        f"/v1/cases/{case_id}/document-requests/{request_id}", headers=staff(ALICE)
    )

    assert gone.status_code == 204
    assert world.staff_requests(case_id)["requests"] == []


def test_received_cannot_be_set_by_hand(world):
    case_id = world.open_case()
    request_id = world.apply_checklist(case_id)["requests"][0]["id"]

    response = world.client.patch(
        f"/v1/cases/{case_id}/document-requests/{request_id}",
        json={"status": "received"},
        headers=staff(ALICE),
    )

    assert response.status_code == 400
    assert world.staff_requests(case_id)["progress"]["received"] == 0


def test_another_firms_case_requests_are_a_404(world):
    case_id = world.open_case()

    assert (
        world.client.get(
            f"/v1/cases/{case_id}/document-requests", headers=staff(CAROL)
        ).status_code
        == 404
    )
    assert (
        world.client.post(
            f"/v1/cases/{case_id}/document-requests/from-checklist",
            headers=staff(CAROL),
        ).status_code
        == 404
    )


def test_a_staff_upload_naming_a_waived_request_is_refused(world):
    case_id = world.open_case()
    request_id = world.apply_checklist(case_id)["requests"][0]["id"]
    world.client.patch(
        f"/v1/cases/{case_id}/document-requests/{request_id}",
        json={"status": "waived"},
        headers=staff(ALICE),
    )

    response = world.client.post(
        f"/v1/cases/{case_id}/documents",
        json={**UPLOAD, "kind": "other", "requestId": request_id},
        headers=staff(ALICE),
    )

    assert response.status_code == 400
    assert "requestId" in response.get_json()["fields"]


# ── Staff: the firm's checklist ─────────────────────────────────


def test_a_firm_that_saved_nothing_reads_the_default(world):
    body = world.client.get(
        "/v1/firm/document-checklist", headers=staff(ALICE)
    ).get_json()

    assert body["isDefault"] is True
    assert [i["title"] for i in body["items"]] == [i.title for i in DEFAULT_CHECKLIST]
    assert body["defaultItems"] == body["items"]


def test_reset_returns_the_firm_to_the_default(world):
    world.client.put(
        "/v1/firm/document-checklist",
        json={"items": []},
        headers=staff(ALICE),
    )
    assert world.firms.get_document_checklist(FIRM_A) is not None

    reset = world.client.delete("/v1/firm/document-checklist", headers=staff(ALICE))

    assert reset.status_code == 200
    assert reset.get_json()["isDefault"] is True
    assert world.firms.get_document_checklist(FIRM_A) is None


def test_another_firms_checklist_does_not_reach_this_one(world):
    world.client.put(
        "/v1/firm/document-checklist",
        json={"items": [{"title": "Lease", "kind": "other"}]},
        headers=staff(CAROL),
    )

    body = world.client.get(
        "/v1/firm/document-checklist", headers=staff(ALICE)
    ).get_json()

    assert body["isDefault"] is True


@pytest.mark.parametrize(("method", "status"), [("get", 403), ("put", 403)])
def test_the_checklist_is_a_firm_administration_setting(world, method, status):
    response = getattr(world.client, method)(
        "/v1/firm/document-checklist", json={"items": []}, headers=staff(BOB)
    )
    assert response.status_code == status
