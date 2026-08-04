"""Case CRUD behind auth (issue 8.3).

Most of what matters here is what the endpoints REFUSE. A case store whose
only tests are happy-path is a store nobody has checked for the one bug that
matters — one firm reading another firm's cases.

Tokens are signed for real, mirroring tests/test_auth.py: a keypair is
generated once, served through the in-memory provider, and used to mint
Cognito-shaped access tokens. No mock verifier, no patched decode.

Every identifier below is obviously fake. This repo is public.
"""

from __future__ import annotations

import time

import jwt
import pytest
from cryptography.hazmat.primitives.asymmetric import rsa
from insolvia_api.adapters.memory.access_log import MemoryAccessLog
from insolvia_api.adapters.memory.case_store import MemoryCaseStore
from insolvia_api.adapters.memory.jwks_provider import StaticJwksProvider
from insolvia_api.adapters.memory.mailer_client import InMemoryMailerClient
from insolvia_api.adapters.memory.waitlist_store import MemoryWaitlistStore
from insolvia_api.api.app_factory import create_app
from insolvia_api.api.dependencies import ApiDependencies
from insolvia_api.core.cases import (
    Case,
    case_from_item,
    case_item,
    decode_cursor,
    encode_cursor,
)
from insolvia_api.core.config import load_config
from insolvia_api.core.errors import ValidationError

ISSUER = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE00"
CLIENT_ID = "exampleappclientid000000"
ALICE = "00000000-0000-4000-8000-00000000a11c"
BOB = "00000000-0000-4000-8000-00000000b0b0"
KID = "test-key-1"

_PRIVATE_KEY = rsa.generate_private_key(public_exponent=65537, key_size=2048)
_PUBLIC_KEY = _PRIVATE_KEY.public_key()


def token_for(subject: str) -> str:
    now = int(time.time())
    return jwt.encode(
        {
            "iss": ISSUER,
            "client_id": CLIENT_ID,
            "token_use": "access",
            "sub": subject,
            "username": subject,
            "iat": now,
            "auth_time": now,
            "exp": now + 3600,
        },
        _PRIVATE_KEY,  # type: ignore[arg-type]
        algorithm="RS256",
        headers={"kid": KID},
    )


def auth(subject: str) -> dict[str, str]:
    return {"Authorization": f"Bearer {token_for(subject)}"}


@pytest.fixture
def store():
    return MemoryCaseStore()


@pytest.fixture
def access_log():
    return MemoryAccessLog()


@pytest.fixture
def client(store, access_log):
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
        )
    )
    return app.test_client()


def open_case(client, subject=ALICE, *, chapter=7, district="NDCA"):
    response = client.post(
        "/v1/cases",
        json={"chapter": chapter, "district": district},
        headers=auth(subject),
    )
    assert response.status_code == 201
    return response.get_json()


# ── Auth ────────────────────────────────────────────────────────


@pytest.mark.parametrize(
    ("method", "path"),
    [
        ("post", "/v1/cases"),
        ("get", "/v1/cases"),
        ("get", "/v1/cases/any-id"),
        ("patch", "/v1/cases/any-id"),
    ],
)
def test_every_case_route_requires_a_token(client, method, path):
    assert getattr(client, method)(path, json={}).status_code == 401


# ── Create ──────────────────────────────────────────────────────


def test_create_returns_the_case(client):
    body = open_case(client)
    assert body["chapter"] == 7
    assert body["district"] == "NDCA"
    assert body["status"] == "intake"
    assert body["id"]
    assert body["createdAt"] == body["updatedAt"]


def test_create_never_returns_the_owner(client):
    # The caller is the owner by construction; echoing the subject back would
    # put an identifier in a body for no reason.
    assert "ownerPrincipal" not in open_case(client)


def test_create_ignores_an_owner_supplied_by_the_client(client, store):
    response = client.post(
        "/v1/cases",
        json={"chapter": 7, "district": "NDCA", "ownerPrincipal": BOB},
        headers=auth(ALICE),
    )
    assert response.status_code == 201
    stored = store.cases[response.get_json()["id"]]
    assert stored.owner_principal == ALICE


def test_create_starts_at_intake_even_if_asked_otherwise(client):
    response = client.post(
        "/v1/cases",
        json={"chapter": 7, "district": "NDCA", "status": "filed"},
        headers=auth(ALICE),
    )
    assert response.get_json()["status"] == "intake"


@pytest.mark.parametrize(
    ("payload", "field"),
    [
        ({"district": "NDCA"}, "chapter"),
        ({"chapter": 9, "district": "NDCA"}, "chapter"),
        ({"chapter": "7", "district": "NDCA"}, "chapter"),
        ({"chapter": True, "district": "NDCA"}, "chapter"),
        ({"chapter": 7}, "district"),
        ({"chapter": 7, "district": "   "}, "district"),
        ({"chapter": 7, "district": "x" * 65}, "district"),
    ],
)
def test_create_rejects_bad_input(client, payload, field):
    response = client.post("/v1/cases", json=payload, headers=auth(ALICE))
    assert response.status_code == 400
    body = response.get_json()
    assert body["error"] == "ValidationError"
    assert field in body["fields"]


def test_create_rejects_a_non_object_body(client):
    response = client.post(
        "/v1/cases", json=["not", "an", "object"], headers=auth(ALICE)
    )
    assert response.status_code == 400


# ── Ownership: the tests this module exists for ─────────────────


def test_one_users_case_is_invisible_to_another(client):
    case_id = open_case(client, ALICE)["id"]
    assert client.get(f"/v1/cases/{case_id}", headers=auth(BOB)).status_code == 404


def test_another_users_case_cannot_be_updated(client, store):
    case_id = open_case(client, ALICE)["id"]
    response = client.patch(
        f"/v1/cases/{case_id}", json={"district": "CACD"}, headers=auth(BOB)
    )
    assert response.status_code == 404
    assert store.cases[case_id].district == "NDCA"


def test_a_foreign_case_is_indistinguishable_from_a_missing_one(client):
    """404 for both, byte-for-byte. A different body or status would confirm
    the id exists and make the endpoint an enumeration oracle."""
    case_id = open_case(client, ALICE)["id"]
    foreign = client.get(f"/v1/cases/{case_id}", headers=auth(BOB))
    missing = client.get("/v1/cases/does-not-exist", headers=auth(BOB))
    assert foreign.status_code == missing.status_code == 404
    assert foreign.get_json() == missing.get_json()


def test_list_shows_only_the_callers_cases(client):
    open_case(client, ALICE)
    open_case(client, ALICE)
    open_case(client, BOB)
    body = client.get("/v1/cases", headers=auth(BOB)).get_json()
    assert len(body["cases"]) == 1


def test_the_store_enforces_ownership_itself(store):
    """Not only the route. The scoping rule lives in two places on purpose."""
    case = Case(
        id="c1",
        owner_principal=ALICE,
        chapter=7,
        district="NDCA",
        status="intake",
        created_at="2026-01-01T00:00:00.000Z",
        updated_at="2026-01-01T00:00:00.000Z",
    )
    store.create(case)
    assert store.get("c1", owner_principal=BOB) is None
    assert store.update(case_from_item(case_item(case))) is not None


# ── Read and list ───────────────────────────────────────────────


def test_get_returns_the_case(client):
    created = open_case(client)
    fetched = client.get(f"/v1/cases/{created['id']}", headers=auth(ALICE))
    assert fetched.status_code == 200
    assert fetched.get_json() == created


def test_list_is_newest_first(store):
    """Ordering is asserted against explicit timestamps rather than wall-clock
    creation order. Two cases minted in the same instant tie on GSI1SK and
    break on a random uuid, so a timing-dependent version of this test asserts
    something the design does not promise — see core/cases._timestamp."""
    for created_at in ("2026-01-01T00:00:00.000000Z", "2026-06-01T00:00:00.000000Z"):
        store.create(
            Case(
                id=f"case-{created_at}",
                owner_principal=ALICE,
                chapter=7,
                district="NDCA",
                status="intake",
                created_at=created_at,
                updated_at=created_at,
            )
        )
    page = store.list_for_owner(ALICE, limit=10, cursor=None)
    assert [case.created_at for case in page.cases] == [
        "2026-06-01T00:00:00.000000Z",
        "2026-01-01T00:00:00.000000Z",
    ]


def test_list_returns_the_callers_cases_over_http(client):
    first = open_case(client, district="NDCA")
    second = open_case(client, district="CACD")
    ids = {
        case["id"]
        for case in client.get("/v1/cases", headers=auth(ALICE)).get_json()["cases"]
    }
    assert ids == {first["id"], second["id"]}


def test_list_paginates(client):
    for _ in range(3):
        open_case(client)
    first = client.get("/v1/cases?limit=2", headers=auth(ALICE)).get_json()
    assert len(first["cases"]) == 2
    assert "nextCursor" in first

    second = client.get(
        f"/v1/cases?limit=2&cursor={first['nextCursor']}", headers=auth(ALICE)
    ).get_json()
    assert len(second["cases"]) == 1
    # Absent, not null — the client contract distinguishes them.
    assert "nextCursor" not in second


def test_list_omits_the_cursor_on_a_single_page(client):
    open_case(client)
    assert "nextCursor" not in client.get("/v1/cases", headers=auth(ALICE)).get_json()


@pytest.mark.parametrize("limit", ["0", "101", "-1", "abc"])
def test_list_rejects_a_bad_limit(client, limit):
    response = client.get(f"/v1/cases?limit={limit}", headers=auth(ALICE))
    assert response.status_code == 400


def test_list_rejects_a_forged_cursor(client):
    response = client.get("/v1/cases?cursor=not-base64!!", headers=auth(ALICE))
    assert response.status_code == 400


# ── Update ──────────────────────────────────────────────────────


def test_update_changes_only_what_was_sent(client):
    created = open_case(client, chapter=7, district="NDCA")
    updated = client.patch(
        f"/v1/cases/{created['id']}", json={"district": "CACD"}, headers=auth(ALICE)
    ).get_json()
    assert updated["district"] == "CACD"
    assert updated["chapter"] == 7
    assert updated["status"] == "intake"


def test_update_refreshes_updated_at(client):
    created = open_case(client)
    updated = client.patch(
        f"/v1/cases/{created['id']}",
        json={"status": "ready_to_file"},
        headers=auth(ALICE),
    ).get_json()
    assert updated["updatedAt"] >= created["updatedAt"]
    assert updated["createdAt"] == created["createdAt"]


def test_update_rejects_an_empty_body(client):
    created = open_case(client)
    response = client.patch(f"/v1/cases/{created['id']}", json={}, headers=auth(ALICE))
    assert response.status_code == 400


def test_update_rejects_an_unknown_status(client):
    created = open_case(client)
    response = client.patch(
        f"/v1/cases/{created['id']}", json={"status": "shredded"}, headers=auth(ALICE)
    )
    assert response.status_code == 400
    assert "status" in response.get_json()["fields"]


def test_update_of_a_missing_case_is_404(client):
    response = client.patch(
        "/v1/cases/nope", json={"district": "CACD"}, headers=auth(ALICE)
    )
    assert response.status_code == 404


# ── The access log ──────────────────────────────────────────────


def test_creating_a_case_is_recorded(client, access_log):
    case_id = open_case(client)["id"]
    assert [(e.action, e.case_id, e.principal) for e in access_log.events] == [
        ("case.create", case_id, ALICE)
    ]


def test_reading_a_case_is_recorded(client, access_log):
    case_id = open_case(client)["id"]
    client.get(f"/v1/cases/{case_id}", headers=auth(ALICE))
    read = access_log.events[-1]
    assert (read.action, read.outcome, read.principal) == (
        "case.read",
        "allowed",
        ALICE,
    )


def test_a_refused_read_is_recorded_as_denied(client, access_log):
    """The row that matters. Someone walking case ids should be visible."""
    case_id = open_case(client, ALICE)["id"]
    client.get(f"/v1/cases/{case_id}", headers=auth(BOB))
    denied = access_log.events[-1]
    assert (denied.action, denied.outcome, denied.principal, denied.case_id) == (
        "case.read",
        "denied",
        BOB,
        case_id,
    )


def test_a_refused_update_is_recorded_as_denied(client, access_log):
    case_id = open_case(client, ALICE)["id"]
    client.patch(f"/v1/cases/{case_id}", json={"district": "CACD"}, headers=auth(BOB))
    assert access_log.events[-1].outcome == "denied"


def test_listing_is_not_recorded(client, access_log):
    """A list touches no case in particular, and this table is keyed by case."""
    open_case(client)
    before = len(access_log.events)
    client.get("/v1/cases", headers=auth(ALICE))
    assert len(access_log.events) == before


def test_an_unauthenticated_request_records_nothing(client, access_log):
    client.get("/v1/cases/anything")
    assert access_log.events == []


# ── Item shape and cursors ──────────────────────────────────────


def test_case_item_round_trips(client, store):
    case_id = open_case(client)["id"]
    original = store.cases[case_id]
    assert case_from_item(case_item(original)) == original


def test_case_item_carries_the_sparse_index_keys(client, store):
    case_id = open_case(client)["id"]
    item = case_item(store.cases[case_id])
    assert item["PK"] == f"CASE#{case_id}"
    assert item["SK"] == "META"
    assert item["GSI1PK"] == f"OWNER#{ALICE}"
    assert item["GSI1SK"] == f"{store.cases[case_id].created_at}#{case_id}"


def test_case_from_item_rejects_a_malformed_row():
    with pytest.raises(ValidationError):
        case_from_item({"id": "x"})


def test_cursors_round_trip():
    assert decode_cursor(encode_cursor({"GSI1SK": "a#b"})) == {"GSI1SK": "a#b"}


@pytest.mark.parametrize("cursor", ["!!!", "", "eyJhIjogMX0="])
def test_decode_cursor_rejects_junk(cursor):
    # The last one is valid base64 of {"a": 1} — a non-string value, which
    # would otherwise reach DynamoDB as part of an ExclusiveStartKey.
    with pytest.raises(ValidationError):
        decode_cursor(cursor)
