"""The document endpoints (issue 8.6).

The same emphasis as tests/test_cases.py, for a store that holds more: these
are the routes that hand out a capability to read a client's tax return. Most
of what follows is what they REFUSE — a foreign case, a document id borrowed
from another case, an oversized or disallowed upload, an unauthenticated
caller — because `DocumentStore` enforces no ownership of its own by design and
the case lookup is the only thing between one firm's paperwork and another's.

Tokens are signed for real, mirroring tests/test_cases.py: a keypair is
generated once, served through the in-memory provider, and used to mint
Cognito-shaped access tokens. No mock verifier, no patched decode.

Every identifier below is obviously fake. This repo is public.
"""

from __future__ import annotations

import time
from urllib.parse import quote

import jwt
import pytest
from cryptography.hazmat.primitives.asymmetric import rsa
from insolvia_api.adapters.memory.access_log import MemoryAccessLog
from insolvia_api.adapters.memory.case_store import MemoryCaseStore
from insolvia_api.adapters.memory.document_blobs import MemoryDocumentBlobStore
from insolvia_api.adapters.memory.document_store import MemoryDocumentStore
from insolvia_api.adapters.memory.mailer_client import InMemoryMailerClient
from insolvia_api.adapters.memory.waitlist_store import MemoryWaitlistStore
from insolvia_api.api.app_factory import create_app
from insolvia_api.api.dependencies import ApiDependencies
from insolvia_api.api.routes.documents import (
    DOWNLOAD_URL_TTL_SECONDS,
    UPLOAD_URL_TTL_SECONDS,
)
from insolvia_api.core import documents as core_documents
from insolvia_api.core.config import load_config
from insolvia_api.core.documents import (
    MAX_BYTE_SIZE,
    UPLOAD_TAG,
    UPLOAD_TAG_KEY,
    UPLOAD_TAG_VALUE,
)
from insolvia_core.adapters.memory.firm_store import MemoryFirmStore
from insolvia_core.adapters.memory.jwks_provider import StaticJwksProvider
from insolvia_core.firms import Firm, FirmUser, default_permissions

ISSUER = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE00"
CLIENT_ID = "exampleappclientid000000"
# Two firms. ALICE and DANA are colleagues; BOB administers the OTHER firm,
# which makes him the strongest caller the other tenant has — if his 404s hold,
# a weaker one's do.
FIRM_A = "00000000-0000-4000-8000-00000000f18a"
FIRM_B = "00000000-0000-4000-8000-00000000f18b"
ALICE = "00000000-0000-4000-8000-00000000a11c"
DANA = "00000000-0000-4000-8000-00000000da4a"
BOB = "00000000-0000-4000-8000-00000000b0b0"
KID = "test-key-1"

UPLOAD = {
    "kind": "bank_statement",
    "fileName": "statement.pdf",
    "contentType": "application/pdf",
    "byteSize": 2048,
}

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


def member(subject: str, firm_id: str, **overrides: object) -> FirmUser:
    defaults: dict[str, object] = {
        "firm_id": firm_id,
        "subject": subject,
        "email": f"{subject[-4:]}@example.test",
        "first_name": "Person",
        "last_name": subject[-4:],
        "role": "attorney",
        "is_admin": True,
        "access_all_cases": False,
        "permissions": default_permissions("attorney"),
        "status": "active",
        "created_at": "2026-01-01T00:00:00.000Z",
        "updated_at": "2026-01-01T00:00:00.000Z",
    }
    return FirmUser(**{**defaults, **overrides})  # type: ignore[arg-type]


@pytest.fixture
def firms():
    store = MemoryFirmStore()
    store.create_firm(firm(FIRM_A, "Example & Partners"))
    store.create_firm(firm(FIRM_B, "Other Firm LLP"))
    store.add_user(member(ALICE, FIRM_A))
    # Not an admin and no access_all_cases: a colleague who reaches a matter
    # only by being linked to it. The point of having him here is that the
    # document routes must follow the CASE's rule, not invent one.
    store.add_user(member(DANA, FIRM_A, is_admin=False))
    store.add_user(member(BOB, FIRM_B))
    return store


@pytest.fixture
def access_log():
    return MemoryAccessLog()


@pytest.fixture
def documents():
    return MemoryDocumentStore()


@pytest.fixture
def blobs():
    return MemoryDocumentBlobStore()


@pytest.fixture
def client(access_log, documents, blobs, firms):
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
            access_log=access_log,
            document_store=documents,
            document_blobs=blobs,
        )
    )
    return app.test_client()


def open_case(client, subject=ALICE):
    response = client.post(
        "/v1/cases", json={"chapter": 7, "district": "NDCA"}, headers=auth(subject)
    )
    assert response.status_code == 201
    return response.get_json()["id"]


def upload(client, case_id, subject=ALICE, **overrides):
    return client.post(
        f"/v1/cases/{case_id}/documents",
        json={**UPLOAD, **overrides},
        headers=auth(subject),
    )


def added(client, case_id, subject=ALICE, **overrides):
    response = upload(client, case_id, subject, **overrides)
    assert response.status_code == 201
    return response.get_json()["document"]


# ── Auth and ownership: the tests this module exists for ────────


@pytest.mark.parametrize(
    ("method", "path"),
    [
        ("post", "/v1/cases/any-id/documents"),
        ("get", "/v1/cases/any-id/documents"),
        ("get", "/v1/cases/any-id/documents/any-doc/url"),
        ("delete", "/v1/cases/any-id/documents/any-doc"),
    ],
)
def test_every_route_refuses_an_unauthenticated_caller(client, method, path):
    assert getattr(client, method)(path, json=UPLOAD).status_code == 401


def test_another_firms_case_accepts_no_upload(client, documents, blobs):
    case_id = open_case(client, ALICE)
    assert upload(client, case_id, BOB).status_code == 404
    # And nothing was written or minted on the way to that 404.
    assert documents.documents == {}
    assert blobs.minted == []


def test_another_firms_documents_cannot_be_listed(client):
    case_id = open_case(client, ALICE)
    added(client, case_id)
    assert (
        client.get(f"/v1/cases/{case_id}/documents", headers=auth(BOB)).status_code
        == 404
    )


def test_another_firm_gets_no_download_url(client, blobs):
    case_id = open_case(client, ALICE)
    document = added(client, case_id)
    before = len(blobs.minted)
    response = client.get(
        f"/v1/cases/{case_id}/documents/{document['id']}/url", headers=auth(BOB)
    )
    assert response.status_code == 404
    assert len(blobs.minted) == before


def test_another_firm_cannot_delete(client, documents):
    case_id = open_case(client, ALICE)
    document = added(client, case_id)
    response = client.delete(
        f"/v1/cases/{case_id}/documents/{document['id']}", headers=auth(BOB)
    )
    assert response.status_code == 404
    assert (case_id, document["id"]) in documents.documents


def test_a_case_that_does_not_exist_is_indistinguishable(client):
    # Same status and body as someone else's case — otherwise this endpoint is
    # an oracle for case ids.
    missing = client.get("/v1/cases/no-such-case/documents", headers=auth(ALICE))
    case_id = open_case(client, ALICE)
    foreign = client.get(f"/v1/cases/{case_id}/documents", headers=auth(BOB))
    assert missing.status_code == foreign.status_code == 404
    assert missing.get_json() == foreign.get_json()


def test_a_document_id_from_another_case_does_not_resolve(client):
    """Both cases are ALICE's, so the case lookup passes on both. What refuses
    is the store's key: the case id is half of it, so a document id only ever
    resolves inside the case it belongs to."""
    first = open_case(client, ALICE)
    second = open_case(client, ALICE)
    document = added(client, first)

    url = client.get(
        f"/v1/cases/{second}/documents/{document['id']}/url", headers=auth(ALICE)
    )
    delete = client.delete(
        f"/v1/cases/{second}/documents/{document['id']}", headers=auth(ALICE)
    )
    assert url.status_code == delete.status_code == 404


def test_a_document_id_that_does_not_exist_is_404(client):
    case_id = open_case(client)
    response = client.get(
        f"/v1/cases/{case_id}/documents/no-such-document/url", headers=auth(ALICE)
    )
    assert response.status_code == 404


# ── What an upload refuses ──────────────────────────────────────


@pytest.mark.parametrize(
    ("overrides", "field"),
    [
        ({"byteSize": MAX_BYTE_SIZE + 1}, "byteSize"),
        ({"byteSize": 0}, "byteSize"),
        ({"contentType": "application/zip"}, "contentType"),
        ({"contentType": "image/svg+xml"}, "contentType"),
        ({"kind": "holiday_snaps"}, "kind"),
        ({"fileName": "../escape.pdf"}, "fileName"),
    ],
)
def test_a_bad_upload_is_refused(client, documents, blobs, overrides, field):
    case_id = open_case(client)
    response = upload(client, case_id, **overrides)
    assert response.status_code == 400
    assert field in response.get_json()["fields"]
    # NOTHING was minted. Once a URL exists the only thing still checking
    # anything is S3, so a refusal that still handed one out would be no
    # refusal at all.
    assert blobs.minted == []
    assert documents.documents == {}


def test_a_non_object_body_is_refused(client):
    case_id = open_case(client)
    response = client.post(
        f"/v1/cases/{case_id}/documents", json=["nope"], headers=auth(ALICE)
    )
    assert response.status_code == 400


def test_a_malformed_body_is_refused_before_the_case_is_touched(client, access_log):
    """The body check reveals nothing about the case, so it runs first — which
    is what keeps the access log honest. A row there means the case was
    actually reached."""
    before = len(access_log.events)
    assert upload(client, "no-such-case", byteSize=-1).status_code == 400
    assert len(access_log.events) == before


# ── The upload capability ───────────────────────────────────────


def test_an_upload_returns_the_record_and_a_put_capability(client):
    case_id = open_case(client)
    body = upload(client, case_id).get_json()

    assert body["document"]["fileName"] == "statement.pdf"
    assert body["document"]["kind"] == "bank_statement"
    assert body["upload"]["method"] == "PUT"
    assert body["upload"]["url"]
    assert body["upload"]["expiresAt"].endswith("Z")


def test_the_upload_headers_are_the_ones_the_signature_demands(client):
    case_id = open_case(client)
    headers = upload(client, case_id).get_json()["upload"]["headers"]
    assert headers["Content-Type"] == "application/pdf"
    # Without this the bucket policy's DenyEncryptionDowngrade refuses the PUT
    # even with a valid signature.
    assert headers["x-amz-server-side-encryption"] == "aws:kms"
    # And without this the object is written UNTAGGED, which is worse than a
    # refusal: the PUT succeeds, the bucket's expire-unconfirmed-uploads rule
    # has nothing to match, and an abandoned upload becomes bytes no lifecycle
    # rule and no code path in this service can ever reach.
    assert headers["x-amz-tagging"] == UPLOAD_TAG


def test_the_advertised_headers_are_exactly_the_signed_ones_the_client_can_set(
    client,
):
    """Every header here is one S3 checks, and a header the client does not
    send is a SignatureDoesNotMatch it cannot interpret.

    Content-Length is signed too and is deliberately NOT advertised: browsers
    refuse to let JavaScript set it and every HTTP client sets it anyway.
    Pinning the whole set is what makes that omission a decision rather than
    something that quietly drifted."""
    case_id = open_case(client)
    headers = upload(client, case_id).get_json()["upload"]["headers"]
    assert set(headers) == {
        "Content-Type",
        "x-amz-server-side-encryption",
        "x-amz-tagging",
    }


def test_the_upload_is_tagged_unconfirmed_so_abandoned_bytes_are_reapable(
    client, blobs
):
    """The capability outlives the row that authorised it.

    POST, delete the document, then PUT through the still-valid URL and the
    object lands under a key no row names — `list_for_case` reads the case
    store, a second DELETE has nothing to delete, and the API holds no
    s3:ListBucket. The tag bound into the signature is the only handle
    anything has on those bytes, and the bucket's expire-unconfirmed-uploads
    lifecycle rule is what uses it.
    """
    case_id = open_case(client)
    upload(client, case_id)
    assert blobs.minted[-1].tag == UPLOAD_TAG


def test_the_tag_is_a_wire_safe_key_value_pair(client):
    """`x-amz-tagging` is a URL-encoded query string, and the value below is
    also written into a Terraform lifecycle filter by hand
    (infra/modules/case_documents/main.tf). A character needing escaping in
    either place would make the header and the filter disagree silently — the
    upload would succeed and never be reaped."""
    key, _, value = UPLOAD_TAG.partition("=")
    assert (key, value) == (UPLOAD_TAG_KEY, UPLOAD_TAG_VALUE)
    assert quote(key, safe="") + "=" + quote(value, safe="") == UPLOAD_TAG


def test_the_capability_is_bound_to_the_validated_size_and_type(client, blobs):
    """The cap is only a cap if it reaches the signature. Unbound, the client's
    declared size is a claim it is free to ignore."""
    case_id = open_case(client)
    upload(client, case_id, byteSize=4096)
    minted = blobs.minted[-1]
    assert (minted.method, minted.byte_size, minted.content_type) == (
        "PUT",
        4096,
        "application/pdf",
    )


def test_the_capability_names_the_documents_own_object(client, blobs):
    case_id = open_case(client)
    document = added(client, case_id)
    assert blobs.minted[-1].storage_ref == f"cases/{case_id}/{document['id']}"


def test_no_url_carries_the_file_name(client, blobs):
    """No PII in an object key, and therefore none in the URL built from it."""
    case_id = open_case(client)
    document = added(client, case_id, fileName="jane-smith-2023-tax-return.pdf")
    url = upload(client, case_id).get_json()["upload"]["url"]
    download = client.get(
        f"/v1/cases/{case_id}/documents/{document['id']}/url", headers=auth(ALICE)
    ).get_json()["url"]
    for candidate in (url, download):
        assert "jane" not in candidate
        assert "smith" not in candidate


@pytest.mark.parametrize(
    "ttl",
    [UPLOAD_URL_TTL_SECONDS, DOWNLOAD_URL_TTL_SECONDS],
    ids=["upload", "download"],
)
def test_a_capability_lives_for_minutes_not_hours(ttl):
    """Pinned deliberately. A URL is a bearer token in a query string, and the
    only real control over a leaked one is how briefly it works."""
    assert 0 < ttl <= 15 * 60


# ── Completing an upload ────────────────────────────────────────
# The half of the mechanism that makes the bucket's expire-unconfirmed-uploads
# rule safe. Every test here is guarding that invariant from one side or
# another: an object nobody completed keeps its tag and gets reaped, and an
# object somebody completed does not.


def complete(client, case_id, document_id, subject=ALICE):
    return client.post(
        f"/v1/cases/{case_id}/documents/{document_id}/complete", headers=auth(subject)
    )


def uploaded(client, blobs, case_id, *, byte_size=2048, etag="abc123", **overrides):
    """Create a document and have the "client" actually PUT its bytes."""
    document = added(client, case_id, **overrides)
    blobs.accept_upload(
        f"cases/{case_id}/{document['id']}", byte_size=byte_size, etag=etag
    )
    return document


def test_completing_an_upload_that_never_landed_is_refused(client, blobs, documents):
    """THE CASE THE ENDPOINT EXISTS FOR.

    Nothing PUT anything, so the client is claiming an upload that did not
    happen. Confirming it would clear the tag on an object that is not there
    and mark the row `stored`, which is the one lie this design must not be
    able to tell — the case would present a document as filed with no bytes
    behind it and no reaper coming for the mistake.
    """
    case_id = open_case(client)
    document = added(client, case_id)

    response = complete(client, case_id, document["id"])

    assert response.status_code == 409
    assert response.get_json()["error"] == "ConflictError"
    # Nothing moved: not the tag, not the row.
    assert blobs.cleared == []
    assert documents.documents[(case_id, document["id"])].status == "pending"


def test_completing_clears_the_tag_that_would_have_reaped_the_object(client, blobs):
    """The whole point. While the object carries `upload=unconfirmed` the
    bucket's lifecycle rule deletes it after a day; this call is the only
    thing in the system that takes it out of that filter."""
    case_id = open_case(client)
    document = uploaded(client, blobs, case_id)
    storage_ref = f"cases/{case_id}/{document['id']}"
    # Precondition, asserted rather than assumed: the PUT tagged it.
    assert storage_ref in blobs.tagged

    assert complete(client, case_id, document["id"]).status_code == 200

    assert blobs.cleared == [storage_ref]
    assert storage_ref not in blobs.tagged


def test_a_completed_document_reports_the_size_s3_gave_not_the_one_asked_for(
    client, blobs
):
    """The declared size is an input to minting a capability; this is the fact.

    They agree in practice because content-length is bound into the signature,
    so S3 refuses a body of any other length — this is defence in depth. What
    it rules out is a record that goes on asserting a number nothing ever
    checked.
    """
    case_id = open_case(client)
    document = uploaded(client, blobs, case_id, byteSize=2048, byte_size=1999)
    assert document["byteSize"] == 2048

    body = complete(client, case_id, document["id"]).get_json()["document"]

    assert body["byteSize"] == 1999
    assert body["status"] == "stored"


def test_another_firms_case_cannot_be_completed(client, blobs):
    """Through _owned_case_or_404 like every other document route. Without it
    this would be a write endpoint with no authorisation on it at all."""
    case_id = open_case(client, ALICE)
    document = uploaded(client, blobs, case_id)

    response = complete(client, case_id, document["id"], subject=BOB)

    assert response.status_code == 404
    # And Bob's failed attempt changed nothing about Alice's object.
    assert blobs.cleared == []
    assert f"cases/{case_id}/{document['id']}" in blobs.tagged


def test_completing_is_idempotent(client, blobs):
    """A client that lost the response retries. It must not be punished with a
    409 for an upload that plainly did land."""
    case_id = open_case(client)
    document = uploaded(client, blobs, case_id)

    first = complete(client, case_id, document["id"])
    second = complete(client, case_id, document["id"])

    assert (first.status_code, second.status_code) == (200, 200)
    assert first.get_json()["document"] == second.get_json()["document"]


def test_completing_a_deleted_document_is_404_and_resurrects_nothing(
    client, blobs, documents
):
    """A row that is gone must not come back as a `stored` document.

    The route refuses at the document lookup here; the store's conditional
    update is what closes the narrower window between that lookup and the
    write, and it is tested where it lives (tests/test_document_store.py).
    """
    case_id = open_case(client)
    document = uploaded(client, blobs, case_id)
    client.delete(
        f"/v1/cases/{case_id}/documents/{document['id']}", headers=auth(ALICE)
    )

    assert complete(client, case_id, document["id"]).status_code == 404
    assert documents.documents == {}


# ── Listing ─────────────────────────────────────────────────────


def test_listing_returns_the_cases_documents_newest_first(client, monkeypatch):
    """The clock is pinned to two distinct instants, because the design does
    not promise creation order within one millisecond: two uploads stamped in
    the same one tie on uploaded_at and fall to list_order's id tiebreak —
    stable, but arbitrary. Left on the wall clock this test failed whenever
    both POSTs landed in one millisecond and the random ids sorted the wrong
    way. Same reasoning as test_cases.test_list_is_newest_first;
    test_documents.test_ordering_is_by_time_then_id pins the tie rule itself.

    The iterator is deliberately exactly two instants long: a third
    _timestamp() call would mean this route stamps more than this test
    believes, and StopIteration says so louder than a flake would."""
    instants = iter(("2026-01-01T00:00:00.000Z", "2026-01-01T00:00:00.001Z"))
    monkeypatch.setattr(core_documents, "_timestamp", lambda: next(instants))
    case_id = open_case(client)
    first = added(client, case_id, fileName="one.pdf")
    second = added(client, case_id, fileName="two.pdf")
    listed = client.get(
        f"/v1/cases/{case_id}/documents", headers=auth(ALICE)
    ).get_json()["documents"]
    assert [d["id"] for d in listed] == [second["id"], first["id"]]


def test_listing_shows_only_this_cases_documents(client):
    first = open_case(client, ALICE)
    second = open_case(client, ALICE)
    added(client, first)
    listed = client.get(
        f"/v1/cases/{second}/documents", headers=auth(ALICE)
    ).get_json()["documents"]
    assert listed == []


def test_listing_shows_which_uploads_finished_and_which_did_not(client, blobs):
    """Both are listed, and they are TELLABLE APART.

    A pending row is not noise to filter out: it is the case's own record of a
    file the user tried to add, and the bucket will reap its half-uploaded
    object within a day. Hiding it would leave the user with a document they
    cannot see, cannot open and cannot retry — so the listing carries both and
    `status` is what the UI branches on.
    """
    case_id = open_case(client)
    finished = uploaded(client, blobs, case_id, fileName="arrived.pdf")
    abandoned = added(client, case_id, fileName="never-finished.pdf")
    complete(client, case_id, finished["id"])

    listed = client.get(
        f"/v1/cases/{case_id}/documents", headers=auth(ALICE)
    ).get_json()["documents"]

    assert {d["id"]: d["status"] for d in listed} == {
        finished["id"]: "stored",
        abandoned["id"]: "pending",
    }


def test_a_new_document_is_pending_until_it_is_completed(client):
    """The create response says so too, so a client never has to infer the
    state of a row it just made."""
    case_id = open_case(client)
    assert added(client, case_id)["status"] == "pending"


def test_listing_mints_nothing(client, blobs):
    """A list is cheap and frequent; minting a download capability per row
    would hand out a dozen live tickets to answer a question about names."""
    case_id = open_case(client)
    added(client, case_id)
    minted = len(blobs.minted)
    client.get(f"/v1/cases/{case_id}/documents", headers=auth(ALICE))
    assert len(blobs.minted) == minted


# ── Download and delete ─────────────────────────────────────────


def test_a_download_url_is_a_get_capability(client, blobs):
    case_id = open_case(client)
    document = added(client, case_id)
    body = client.get(
        f"/v1/cases/{case_id}/documents/{document['id']}/url", headers=auth(ALICE)
    ).get_json()
    assert body["method"] == "GET"
    assert body["expiresAt"].endswith("Z")
    assert blobs.minted[-1].method == "GET"
    assert blobs.minted[-1].expires_in == DOWNLOAD_URL_TTL_SECONDS


def test_delete_removes_the_row_and_the_object(client, documents, blobs):
    case_id = open_case(client)
    document = added(client, case_id)
    response = client.delete(
        f"/v1/cases/{case_id}/documents/{document['id']}", headers=auth(ALICE)
    )
    assert response.status_code == 204
    assert response.data == b""
    assert documents.documents == {}
    assert blobs.deleted == [f"cases/{case_id}/{document['id']}"]


def test_deleting_twice_is_a_404_and_deletes_one_object(client, blobs):
    """The second caller lost the race. It must not go on to delete an object
    whose row somebody else already removed."""
    case_id = open_case(client)
    document = added(client, case_id)
    path = f"/v1/cases/{case_id}/documents/{document['id']}"
    assert client.delete(path, headers=auth(ALICE)).status_code == 204
    assert client.delete(path, headers=auth(ALICE)).status_code == 404
    assert len(blobs.deleted) == 1


def test_a_deleted_document_has_no_download_url(client):
    case_id = open_case(client)
    document = added(client, case_id)
    client.delete(
        f"/v1/cases/{case_id}/documents/{document['id']}", headers=auth(ALICE)
    )
    response = client.get(
        f"/v1/cases/{case_id}/documents/{document['id']}/url", headers=auth(ALICE)
    )
    assert response.status_code == 404


# ── The access log ──────────────────────────────────────────────


@pytest.mark.parametrize(
    ("action", "call"),
    [
        ("document.create", lambda c, case, doc: upload(c, case)),
        (
            "document.read",
            lambda c, case, doc: c.get(
                f"/v1/cases/{case}/documents", headers=auth(ALICE)
            ),
        ),
        (
            "document.download",
            lambda c, case, doc: c.get(
                f"/v1/cases/{case}/documents/{doc}/url", headers=auth(ALICE)
            ),
        ),
        (
            "document.delete",
            lambda c, case, doc: c.delete(
                f"/v1/cases/{case}/documents/{doc}", headers=auth(ALICE)
            ),
        ),
    ],
)
def test_every_route_is_recorded_under_its_own_action(client, access_log, action, call):
    """Four verbs, not a reused case.read. Folding a download into the same row
    as opening a case list would make the one disclosure that matters most
    invisible in the log that exists to show it."""
    case_id = open_case(client)
    document = added(client, case_id)
    call(client, case_id, document["id"])
    recorded = access_log.events[-1]
    assert (recorded.action, recorded.outcome, recorded.case_id) == (
        action,
        "allowed",
        case_id,
    )


def test_a_refused_download_is_recorded_as_denied(client, access_log):
    """The row that matters: someone walking case ids after a document."""
    case_id = open_case(client, ALICE)
    document = added(client, case_id)
    client.get(f"/v1/cases/{case_id}/documents/{document['id']}/url", headers=auth(BOB))
    denied = access_log.events[-1]
    assert (denied.action, denied.outcome, denied.principal) == (
        "document.download",
        "denied",
        BOB,
    )


def test_an_unauthenticated_request_records_nothing(client, access_log):
    client.get("/v1/cases/anything/documents")
    assert access_log.events == []


# ── What firms changed here ─────────────────────────────────────


def test_a_colleague_on_the_matter_can_open_its_documents(client):
    """WHAT THE OLD MODEL COULD NOT DO. Alice uploads a bank statement; Dana,
    linked to the same matter, downloads it. Under `owner_principal` she got a
    404 on her own firm's file.

    The document routes did not learn anything new to allow this — they resolve
    the case, and the case's rule changed underneath them. That is the whole
    argument for `_reachable_case_or_404` being the only check they make.
    """
    case_id = open_case(client, ALICE)
    document_id = added(client, case_id, ALICE)["id"]

    # Dana is neither an admin nor access_all_cases, so she needs the link.
    assert (
        client.get(f"/v1/cases/{case_id}/documents", headers=auth(DANA)).status_code
        == 404
    )

    assert (
        client.put(
            f"/v1/cases/{case_id}/assignees/{DANA}", headers=auth(ALICE)
        ).status_code
        == 204
    )

    listed = client.get(f"/v1/cases/{case_id}/documents", headers=auth(DANA))
    assert listed.status_code == 200
    assert [d["id"] for d in listed.get_json()["documents"]] == [document_id]
    assert (
        client.get(
            f"/v1/cases/{case_id}/documents/{document_id}/url", headers=auth(DANA)
        ).status_code
        == 200
    )


def test_the_other_firms_admin_reaches_nothing(client):
    """Bob administers firm B. An admin is an admin OF A FIRM — there is no
    super-admin — so the strongest caller the other tenant has still gets the
    same 404 as a stranger."""
    case_id = open_case(client, ALICE)
    document_id = added(client, case_id, ALICE)["id"]
    for path in (
        f"/v1/cases/{case_id}/documents",
        f"/v1/cases/{case_id}/documents/{document_id}/url",
    ):
        assert client.get(path, headers=auth(BOB)).status_code == 404


def test_documents_hidden_means_403_not_404(client, firms):
    """The per-feature layer, on a case the caller CAN see. 404 would be a lie
    their client cannot act on — the matter is in their listing."""
    case_id = open_case(client, ALICE)
    firms.users[(FIRM_A, ALICE)] = member(
        ALICE,
        FIRM_A,
        is_admin=False,
        access_all_cases=True,
        permissions={**default_permissions("attorney"), "documents": "hidden"},
    )
    assert (
        client.get(f"/v1/cases/{case_id}/documents", headers=auth(ALICE)).status_code
        == 403
    )


def test_view_only_can_read_documents_but_not_upload_or_delete(client, firms):
    case_id = open_case(client, ALICE)
    document_id = added(client, case_id, ALICE)["id"]
    firms.users[(FIRM_A, ALICE)] = member(
        ALICE,
        FIRM_A,
        is_admin=False,
        access_all_cases=True,
        permissions={**default_permissions("attorney"), "documents": "view_only"},
    )
    assert (
        client.get(f"/v1/cases/{case_id}/documents", headers=auth(ALICE)).status_code
        == 200
    )
    # Reading the list AND getting a download capability are both views. The
    # capability is the sharper of the two and it is deliberately on the same
    # level: `view_only` on documents means being able to open them.
    assert (
        client.get(
            f"/v1/cases/{case_id}/documents/{document_id}/url", headers=auth(ALICE)
        ).status_code
        == 200
    )
    assert (
        client.post(
            f"/v1/cases/{case_id}/documents", json=UPLOAD, headers=auth(ALICE)
        ).status_code
        == 403
    )
    assert (
        client.delete(
            f"/v1/cases/{case_id}/documents/{document_id}", headers=auth(ALICE)
        ).status_code
        == 403
    )
