"""Document request checklists (ADR 0023 PR 5 / #364), from outside the process.

THE DONE-WHEN, against the deployed stack: a case shows requested documents
arrived against outstanding, and a client upload satisfies its request. The
unit tier proves the rules against memory stores; this proves the parts only
a deployed environment has — the seeded client's real portal token, the
presigned PUT into the real case-documents bucket with no encryption header
(the bucket default is the case key, #392), the API role's HeadObject and
tag clear on a client's object, and the request row in the real case table.

Scratch discipline. The upload has to land on the case the SEEDED CLIENT is
bound to — the fixture case — because that binding is the only way a portal
token reaches a case. So the test adds ONE ad-hoc request there (never the
whole checklist), and in a finally deletes the client's document and the
request. What remains per run: the access-log rows (append-only by design)
and the object's noncurrent version, which the bucket's lifecycle rule
expires after 30 days. The checklist test works in the suite's scratch case
and withdraws every request it added. Neither test writes the firm's
checklist, so nothing firm-wide needs putting back.
"""

from __future__ import annotations

import urllib.error
import urllib.request
import uuid
from typing import Any

import pytest

from tests.integration.cognito_srp import SignInError, sign_in
from tests.integration.conftest import Api, _required
from tests.integration.test_portal import _fixture_case_id, _seeded_client

BODY = b"%PDF-1.4\n% insolvia integration tier: a client's upload against a request\n"
CONTENT_TYPE = "application/pdf"


def _send(
    url: str, method: str, *, headers: dict[str, str], body: bytes | None
) -> tuple[int, bytes]:
    request = urllib.request.Request(url, data=body, method=method)
    for name, value in headers.items():
        request.add_header(name, value)
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            return response.status, response.read()
    except urllib.error.HTTPError as error:
        return error.code, error.read()


@pytest.fixture(scope="module")
def seeded_client(fixture: dict[str, Any]) -> dict[str, Any]:
    found = _seeded_client(fixture)
    if found is None:
        pytest.skip("the seed fixture binds no portal client to a case")
    return found


@pytest.fixture(scope="module")
def as_client(base_url: str, seeded_client: dict[str, Any]) -> Api:
    try:
        result = sign_in(
            pool_id=_required("INTEGRATION_AUTH_POOL_ID"),
            client_id=_required("INTEGRATION_AUTH_PORTAL_CLIENT_ID"),
            email=str(seeded_client["email"]),
            password=_required("E2E_TEST_USER_PASSWORD"),
        )
    except SignInError as refusal:
        pytest.fail(f"could not sign the seeded client in: {refusal}")
    return Api(base_url, str(result["AccessToken"]))


def test_a_client_upload_satisfies_its_request_and_the_case_counts_it(
    admin: Api, as_client: Api, seeded_client: dict[str, Any]
):
    case_id = _fixture_case_id(admin, str(seeded_client["email"]))
    title = f"Integration probe {uuid.uuid4()}"
    request = admin.post(
        f"/v1/cases/{case_id}/document-requests",
        {"title": title, "kind": "bank_statement"},
        expect=201,
    )
    document_id: str | None = None
    try:
        before = admin.get(f"/v1/cases/{case_id}/document-requests")["progress"]

        # The client sees the request — and no case id anywhere in the answer.
        listed = as_client.get("/v1/portal/document-requests")
        assert case_id not in str(listed)
        seen = next(r for r in listed["requests"] if r["id"] == request["id"])
        assert (seen["title"], seen["status"], seen["uploads"]) == (
            title,
            "requested",
            [],
        )

        # Authorise → PUT the bytes with exactly the signed headers → complete.
        created = as_client.post(
            "/v1/portal/documents",
            {
                "requestId": request["id"],
                "fileName": "integration-client-probe.pdf",
                "contentType": CONTENT_TYPE,
                "byteSize": len(BODY),
            },
            expect=201,
        )
        document_id = str(created["document"]["id"])
        upload = created["upload"]
        assert "x-amz-server-side-encryption" not in upload["headers"]
        status, reply = _send(
            upload["url"], "PUT", headers=upload["headers"], body=BODY
        )
        assert status == 200, (
            f"the presigned PUT was refused ({status}): {reply[:300]!r}"
        )
        completed = as_client.post(
            f"/v1/portal/documents/{document_id}/complete", expect=200
        )["document"]
        assert completed["status"] == "stored"
        assert completed["byteSize"] == len(BODY)

        # The request reads received, for staff and for the client…
        after = admin.get(f"/v1/cases/{case_id}/document-requests")
        mine = next(r for r in after["requests"] if r["id"] == request["id"])
        assert mine["status"] == "received"
        assert mine["documentIds"] == [document_id]
        # …and the case's progress counts it.
        assert after["progress"]["received"] == before["received"] + 1
        assert after["progress"]["outstanding"] == before["outstanding"] - 1
        assert after["progress"]["total"] == before["total"]

        client_view = as_client.get("/v1/portal/document-requests")
        answered = next(r for r in client_view["requests"] if r["id"] == request["id"])
        assert answered["status"] == "received"
        assert [u["id"] for u in answered["uploads"]] == [document_id]

        # Staff see it on the case as the client's upload, filed under the
        # request's kind — and nothing auto-extracted it.
        documents = admin.get(f"/v1/cases/{case_id}/documents")["documents"]
        stored = next(d for d in documents if d["id"] == document_id)
        assert (stored["channel"], stored["kind"], stored["requestId"]) == (
            "client",
            "bank_statement",
            request["id"],
        )
    finally:
        if document_id is not None:
            admin.delete(
                f"/v1/cases/{case_id}/documents/{document_id}", expect=(204, 404)
            )
        admin.delete(
            f"/v1/cases/{case_id}/document-requests/{request['id']}", expect=(204, 404)
        )


def test_the_firms_checklist_applies_to_a_case_once(
    admin: Api, scratch_case: dict[str, Any]
):
    case_id = scratch_case["id"]
    checklist = admin.get("/v1/firm/document-checklist")
    assert {"isDefault", "items", "defaultItems"} <= set(checklist)
    existing = {
        r["id"] for r in admin.get(f"/v1/cases/{case_id}/document-requests")["requests"]
    }

    applied = admin.post(
        f"/v1/cases/{case_id}/document-requests/from-checklist", {}, expect=200
    )
    added = [r for r in applied["requests"] if r["id"] not in existing]
    try:
        assert applied["added"] == len(added)
        again = admin.post(
            f"/v1/cases/{case_id}/document-requests/from-checklist", {}, expect=200
        )
        assert again["added"] == 0
        # `received` is not settable by hand.
        if added:
            admin.patch(
                f"/v1/cases/{case_id}/document-requests/{added[0]['id']}",
                {"status": "received"},
                expect=400,
            )
    finally:
        for made in added:
            admin.delete(
                f"/v1/cases/{case_id}/document-requests/{made['id']}", expect=(204, 404)
            )


def test_the_portal_document_routes_refuse_a_staff_token(admin: Api):
    admin.get("/v1/portal/document-requests", expect=401)
    admin.post("/v1/portal/documents", {}, expect=401)
