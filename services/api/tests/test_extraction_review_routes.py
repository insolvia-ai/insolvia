"""The extraction review endpoints (issue #89 / 8.9).

Three families, in the order the issue weighs them: WHO may confirm (the
`extraction_review` permission — hidden is 403, view_only reads and confirms
nothing, add_edit reviews), WHAT confirmation writes (the case record with
minted provenance the store's own invariants accept — machine source,
confirming human, source pointers), and the FULL LOOP (extraction worker →
queue → accept → case data), pinned end to end against the memory adapters.

Tokens are signed for real, mirroring tests/test_job_routes.py. Every
identifier and every value below is synthetic; this repo is public.
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
from insolvia_core.adapters.memory.candidate_store import MemoryCandidateStore
from insolvia_core.adapters.memory.case_entity_store import MemoryCaseEntityStore
from insolvia_core.adapters.memory.case_store import MemoryCaseStore
from insolvia_core.adapters.memory.firm_store import MemoryFirmStore
from insolvia_core.adapters.memory.jwks_provider import StaticJwksProvider
from insolvia_core.candidates import (
    CandidateOrigin,
    ProposalDraft,
    create_candidate,
)
from insolvia_core.creditors import CREDITOR
from insolvia_core.firms import Firm, FirmUser, default_permissions

ISSUER = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE00"
CLIENT_ID = "exampleappclientid000000"
FIRM_A = "00000000-0000-4000-8000-00000000f18a"
FIRM_B = "00000000-0000-4000-8000-00000000f18b"
ALICE = "00000000-0000-4000-8000-00000000a11c"  # attorney: add_edit
VERA = "00000000-0000-4000-8000-000000000e4a"  # explicit view_only
STAN = "00000000-0000-4000-8000-000000005taf"  # staff: hidden (the default)
BOB = "00000000-0000-4000-8000-00000000b0b0"  # the other firm's admin
KID = "test-key-1"

EXTRACTION_ORIGIN = CandidateOrigin(
    channel="extraction",
    client_id="scripted-extractor",
    subject=ALICE,
)
MCP_ORIGIN = CandidateOrigin(
    channel="mcp", client_id="examplemcpclaudeclient00", subject=ALICE
)

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


def make_firm(firm_id: str, name: str) -> Firm:
    return Firm(
        id=firm_id,
        name=name,
        status="active",
        created_at="2026-01-01T00:00:00.000Z",
        updated_at="2026-01-01T00:00:00.000Z",
    )


def member(subject: str, firm_id: str, role="attorney", permissions=None) -> FirmUser:
    return FirmUser(
        firm_id=firm_id,
        subject=subject,
        email=f"{subject[-4:]}@example.test",
        first_name="Person",
        last_name=subject[-4:],
        role=role,
        is_admin=role == "attorney",
        # Everyone here can SEE the case, so every 403 below is unambiguously
        # the extraction_review level, never linkage.
        access_all_cases=True,
        permissions=permissions
        if permissions is not None
        else default_permissions(role),
        status="active",
        created_at="2026-01-01T00:00:00.000Z",
        updated_at="2026-01-01T00:00:00.000Z",
    )


@pytest.fixture
def stores():
    return {
        "case_store": MemoryCaseStore(),
        "candidate_store": MemoryCandidateStore(),
        "case_entity_store": MemoryCaseEntityStore(),
        "access_log": MemoryAccessLog(),
    }


@pytest.fixture
def client(stores):
    firms = MemoryFirmStore()
    firms.create_firm(make_firm(FIRM_A, "Example & Partners"))
    firms.create_firm(make_firm(FIRM_B, "Other Firm LLP"))
    firms.add_user(member(ALICE, FIRM_A))
    firms.add_user(
        member(
            VERA,
            FIRM_A,
            # Paralegal, NOT admin — is_admin would trump the level and turn
            # this fixture into a test of nothing.
            role="paralegal",
            permissions={
                **default_permissions("paralegal"),
                "extraction_review": "view_only",
            },
        )
    )
    firms.add_user(member(STAN, FIRM_A, role="staff"))
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
            firm_store=firms,
            **stores,
        )
    )
    return app.test_client()


def open_case(client, subject=ALICE):
    response = client.post(
        "/v1/cases", json={"chapter": 7, "district": "NDCA"}, headers=auth(subject)
    )
    assert response.status_code == 201
    return response.get_json()["id"]


def plant(
    stores,
    case_id,
    *,
    entity_type="creditors",
    payload=None,
    origin=EXTRACTION_ORIGIN,
    document_id="00000000-0000-4000-8000-00000000d0c1",
    confidence=0.9,
    page=2,
):
    candidate = create_candidate(
        ProposalDraft(
            entity_type=entity_type,
            payload=payload
            if payload is not None
            else {"name": "First Example Bank", "address": {"city": "Exampleville"}},
            external_ref=None,
            note=None,
        ),
        case_id=case_id,
        origin=origin,
        document_id=document_id if origin.channel == "extraction" else None,
        confidence=confidence if origin.channel == "extraction" else None,
        locator={"document_id": document_id, "page": page}
        if origin.channel == "extraction"
        else None,
    )
    stores["candidate_store"].create(candidate)
    return candidate


def list_queue(client, case_id, subject=ALICE, query=""):
    return client.get(
        f"/v1/cases/{case_id}/extraction/candidates{query}", headers=auth(subject)
    )


def review(client, case_id, candidate_id, body, subject=ALICE):
    return client.post(
        f"/v1/cases/{case_id}/extraction/candidates/{candidate_id}/review",
        json=body,
        headers=auth(subject),
    )


# ── Who may confirm is a permission, not an assumption ──────────


@pytest.mark.parametrize(
    ("method", "path"),
    [
        ("get", "/v1/cases/any-id/extraction/candidates"),
        ("post", "/v1/cases/any-id/extraction/candidates/any-cand/review"),
    ],
)
def test_every_route_refuses_an_unauthenticated_caller(client, method, path):
    assert getattr(client, method)(path, json={}).status_code == 401


def test_hidden_is_a_403_on_every_route(client, stores):
    # STAN's staff defaults leave extraction_review at hidden — the feature
    # simply does not exist for him, even to read.
    case_id = open_case(client)
    candidate = plant(stores, case_id)
    assert list_queue(client, case_id, subject=STAN).status_code == 403
    response = review(client, case_id, candidate.id, {"action": "accept"}, subject=STAN)
    assert response.status_code == 403


def test_view_only_reads_the_queue_and_confirms_nothing(client, stores):
    case_id = open_case(client)
    candidate = plant(stores, case_id)
    assert list_queue(client, case_id, subject=VERA).status_code == 200
    for action in ({"action": "accept"}, {"action": "reject"}):
        assert (
            review(client, case_id, candidate.id, action, subject=VERA).status_code
            == 403
        )
    # Nothing entered the case, and the candidate is still pending.
    assert stores["candidate_store"].get(case_id, candidate.id).status == "pending"
    assert stores["case_entity_store"].list_for_case(case_id, CREDITOR) == ()


def test_another_firms_queue_is_not_found(client, stores):
    case_id = open_case(client)
    candidate = plant(stores, case_id)
    assert list_queue(client, case_id, subject=BOB).status_code == 404
    assert (
        review(
            client, case_id, candidate.id, {"action": "accept"}, subject=BOB
        ).status_code
        == 404
    )


# ── The queue ───────────────────────────────────────────────────


def test_the_queue_lists_what_a_reviewer_needs(client, stores):
    case_id = open_case(client)
    candidate = plant(stores, case_id)

    body = list_queue(client, case_id, query="?status=pending").get_json()

    (row,) = body["candidates"]
    assert row["id"] == candidate.id
    assert row["entityType"] == "creditors"
    assert row["status"] == "pending"
    assert row["payload"]["name"] == "First Example Bank"
    # The source context the issue demands: which surface, which document,
    # where on it, and how sure the model was.
    assert row["origin"]["channel"] == "extraction"
    assert row["origin"]["clientId"] == "scripted-extractor"
    assert row["documentId"] == candidate.document_id
    assert row["locator"]["page"] == 2
    assert row["confidence"] == 0.9


def test_the_status_filter_narrows_and_validates(client, stores):
    case_id = open_case(client)
    kept = plant(stores, case_id)
    rejected = plant(stores, case_id)
    review(client, case_id, rejected.id, {"action": "reject"})

    pending = list_queue(client, case_id, query="?status=pending").get_json()
    assert [row["id"] for row in pending["candidates"]] == [kept.id]
    everything = list_queue(client, case_id).get_json()
    assert len(everything["candidates"]) == 2
    assert list_queue(client, case_id, query="?status=maybe").status_code == 400


# ── Acceptance: the one door into the case ──────────────────────


def test_accepting_writes_the_record_with_confirmed_machine_provenance(client, stores):
    case_id = open_case(client)
    candidate = plant(stores, case_id)

    response = review(client, case_id, candidate.id, {"action": "accept"})

    assert response.status_code == 200
    body = response.get_json()
    assert body["candidate"]["status"] == "accepted"
    assert body["candidate"]["resultingRecordId"] == body["record"]["id"]

    (record,) = stores["case_entity_store"].list_for_case(case_id, CREDITOR)
    assert record.body.name == "First Example Bank"
    entry = record.provenance["name"]
    # The data model's vocabulary, followed exactly: extraction confirms
    # into ai_extracted, with the confirming human, the moment, and the
    # source pointers — which is precisely what the store's invariant 2
    # demands before a machine value may exist on a case record.
    assert entry.source == "ai_extracted"
    assert entry.confirmed_by == ALICE
    assert entry.confirmed_at is not None
    assert entry.extraction_id == candidate.id
    assert entry.document_id == candidate.document_id
    assert entry.confidence == 0.9
    assert entry.locator["page"] == 2

    stored = stores["candidate_store"].get(case_id, candidate.id)
    assert stored.status == "accepted"
    assert stored.confirmed_by == ALICE
    assert stored.resulting_record_id == record.id


def test_a_corrected_field_is_staff_typed_and_the_rest_stays_machine(client, stores):
    case_id = open_case(client)
    candidate = plant(stores, case_id)

    response = review(
        client,
        case_id,
        candidate.id,
        {
            "action": "accept",
            "correctedPayload": {
                "name": "First Example Bank NA",  # the human fixed the name
                "address": {"city": "Exampleville"},  # and kept the address
            },
        },
    )

    assert response.status_code == 200
    assert response.get_json()["candidate"]["status"] == "corrected"
    (record,) = stores["case_entity_store"].list_for_case(case_id, CREDITOR)
    assert record.body.name == "First Example Bank NA"
    # The human authored the name; the model authored the city.
    assert record.provenance["name"].source == "staff_typed"
    assert record.provenance["name"].confirmed_by == ALICE
    assert record.provenance["address.city"].source == "ai_extracted"
    # The correction is RETAINED — it is the quality feedback loop.
    stored = stores["candidate_store"].get(case_id, candidate.id)
    assert stored.status == "corrected"
    assert stored.corrected_payload["name"] == "First Example Bank NA"


def test_an_mcp_proposal_confirms_into_imported(client, stores):
    case_id = open_case(client)
    candidate = plant(stores, case_id, origin=MCP_ORIGIN)
    assert (
        review(client, case_id, candidate.id, {"action": "accept"}).status_code == 200
    )
    (record,) = stores["case_entity_store"].list_for_case(case_id, CREDITOR)
    assert record.provenance["name"].source == "imported"
    assert record.provenance["name"].confirmed_by == ALICE


def test_rejecting_retains_the_candidate_and_writes_nothing(client, stores):
    case_id = open_case(client)
    candidate = plant(stores, case_id)
    response = review(client, case_id, candidate.id, {"action": "reject"})
    assert response.status_code == 200
    assert response.get_json()["candidate"]["status"] == "rejected"
    assert stores["case_entity_store"].list_for_case(case_id, CREDITOR) == ()
    stored = stores["candidate_store"].get(case_id, candidate.id)
    assert stored.status == "rejected"
    assert stored.confirmed_by == ALICE


def test_a_reviewed_candidate_refuses_a_second_review(client, stores):
    case_id = open_case(client)
    candidate = plant(stores, case_id)
    assert (
        review(client, case_id, candidate.id, {"action": "accept"}).status_code == 200
    )
    assert (
        review(client, case_id, candidate.id, {"action": "reject"}).status_code == 409
    )
    # Still exactly one record — the loser overwrote nothing.
    assert len(stores["case_entity_store"].list_for_case(case_id, CREDITOR)) == 1


def test_an_unknown_candidate_is_not_found(client):
    case_id = open_case(client)
    assert (
        review(client, case_id, "no-such-cand", {"action": "accept"}).status_code == 404
    )


@pytest.mark.parametrize(
    "body",
    [
        {},
        {"action": "maybe"},
        {"action": "reject", "correctedPayload": {"name": "X"}},
        {"action": "accept", "correctedPayload": "not-an-object"},
    ],
)
def test_a_malformed_review_is_rejected(client, stores, body):
    case_id = open_case(client)
    candidate = plant(stores, case_id)
    assert review(client, case_id, candidate.id, body).status_code == 400


def test_a_debtor_proposal_is_not_acceptable_in_app_yet(client, stores):
    case_id = open_case(client)
    candidate = plant(
        stores,
        case_id,
        entity_type="debtors",
        payload={"filing_role": "debtor_1"},
        origin=MCP_ORIGIN,
    )
    response = review(client, case_id, candidate.id, {"action": "accept"})
    assert response.status_code == 400
    # But it CAN be rejected, so the queue does not silt up.
    assert (
        review(client, case_id, candidate.id, {"action": "reject"}).status_code == 200
    )


# ── Candidate-id indirection ────────────────────────────────────


def test_a_claim_waits_for_its_creditor_and_then_links_to_the_real_record(
    client, stores
):
    from insolvia_core.claims import CLAIM

    case_id = open_case(client)
    creditor = plant(stores, case_id)
    claim = plant(
        stores,
        case_id,
        entity_type="claims",
        payload={
            "creditor_id": creditor.id,  # the CANDIDATE's id — indirection
            "claim_class": "nonpriority_unsecured",
            "amount": "310.00",
        },
    )

    # Accepting the claim first refuses, and says why.
    early = review(client, case_id, claim.id, {"action": "accept"})
    assert early.status_code == 400
    assert "accepted yet" in early.get_json()["fields"]["creditor_id"]

    # Creditor first, then the claim — and the reference now names the REAL
    # record, not the candidate.
    accepted = review(client, case_id, creditor.id, {"action": "accept"}).get_json()
    assert review(client, case_id, claim.id, {"action": "accept"}).status_code == 200
    (claim_record,) = stores["case_entity_store"].list_for_case(case_id, CLAIM)
    assert claim_record.body.creditor_id == accepted["record"]["id"]
    # Mechanical rewrite, not authorship: still the machine's value.
    assert claim_record.provenance["creditor_id"].source == "ai_extracted"


# ── The full loop: upload → extract → review → case data ────────


def test_the_full_extraction_loop_ends_in_confirmed_case_data(client, stores):
    """8.9's done-when, end to end on the memory adapters: the worker fills
    the queue, the API reviews it, and the case holds confirmed records with
    no unreviewed path anywhere in between."""
    from insolvia_api.adapters.memory.extraction_model import ScriptedExtractionModel
    from insolvia_api.core.extraction import (
        DocumentExtractionDeps,
        run_document_extraction,
    )
    from insolvia_api.core.jobs import new_job
    from insolvia_core.adapters.memory.document_blobs import MemoryDocumentBlobStore
    from insolvia_core.adapters.memory.document_store import MemoryDocumentStore
    from insolvia_core.claims import CLAIM
    from insolvia_core.documents import (
        StoredBlob,
        confirm_document,
        create_document,
        parse_document_upload,
    )

    from tests.test_extraction import CREDIT_REPORT_RAW

    case_id = open_case(client)

    # Upload (planted at the store level; the HTTP upload flow is
    # test_document_routes.py's subject).
    blobs = MemoryDocumentBlobStore()
    document_store = MemoryDocumentStore()
    document = create_document(
        parse_document_upload(
            {
                "kind": "credit_report",
                "fileName": "synthetic.pdf",
                "contentType": "application/pdf",
                "byteSize": 100,
            }
        ),
        case_id=case_id,
        uploaded_by=ALICE,
    )
    blobs.accept_upload(document.storage_ref, byte_size=100, content=b"%PDF-1.7 x")
    document_store.create(
        confirm_document(document, StoredBlob(byte_size=100, etag="e" * 32))
    )

    # Extract — the worker writes into the SAME candidate store the API
    # serves, which is the whole point of the shared queue.
    run_document_extraction(
        new_job(
            "document_extraction",
            case_id=case_id,
            created_by=ALICE,
            document_id=document.id,
        ),
        DocumentExtractionDeps(
            case_store=stores["case_store"],
            document_store=document_store,
            blobs=blobs,
            candidate_store=stores["candidate_store"],
            access_log=stores["access_log"],
            model=ScriptedExtractionModel(CREDIT_REPORT_RAW),
        ),
    )

    # Review: accept every creditor, then every claim, through the API.
    queue = list_queue(client, case_id, query="?status=pending").get_json()
    assert len(queue["candidates"]) == 5
    for row in queue["candidates"]:
        if row["entityType"] == "creditors":
            assert (
                review(client, case_id, row["id"], {"action": "accept"}).status_code
                == 200
            )
    for row in queue["candidates"]:
        if row["entityType"] == "claims":
            assert (
                review(client, case_id, row["id"], {"action": "accept"}).status_code
                == 200
            )

    # Case data: two creditors, three claims, every claim pointing at a real
    # creditor record, every field's provenance confirmed machine data.
    creditors = stores["case_entity_store"].list_for_case(case_id, CREDITOR)
    claims = stores["case_entity_store"].list_for_case(case_id, CLAIM)
    assert len(creditors) == 2
    assert len(claims) == 3
    creditor_ids = {record.id for record in creditors}
    assert {claim.body.creditor_id for claim in claims} <= creditor_ids
    for record in (*creditors, *claims):
        for entry in record.provenance.values():
            assert entry.confirmed_by == ALICE
            assert entry.confirmed_at is not None
    # And the queue remembers everything.
    remaining = list_queue(client, case_id, query="?status=pending").get_json()
    assert remaining["candidates"] == []
    history = list_queue(client, case_id).get_json()
    assert all(row["status"] == "accepted" for row in history["candidates"])
