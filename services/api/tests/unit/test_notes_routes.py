"""Case and form notes (issue 14.5 / #357).

Weighted towards the two things this feature adds beyond the generic
case-collection routes (test_case_entity_routes.py already covers the shared
machinery — the case lookup, the 404-as-anti-oracle rule, the static-segment
routing): the `notes` permission feature, and the author-or-admin ownership
rule on edit and delete. Tokens are signed for real, mirroring
test_case_entity_routes.py. Every identifier below is obviously fake; this
repo is public.
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
from insolvia_core.adapters.memory.firm_store import MemoryFirmStore
from insolvia_core.adapters.memory.jwks_provider import StaticJwksProvider
from insolvia_core.firms import Firm, FirmUser, default_permissions

ISSUER = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE00"
CLIENT_ID = "exampleappclientid000000"
FIRM_A = "00000000-0000-4000-8000-00000000f18a"
FIRM_B = "00000000-0000-4000-8000-00000000f18b"
# All three in FIRM_A, so the ownership tests are within one tenant.
ALICE = "00000000-0000-4000-8000-00000000a11c"  # attorney, author of the note
CAROL = "00000000-0000-4000-8000-00000000ca01"  # attorney, firm admin
DAVE = "00000000-0000-4000-8000-00000000dave"  # paralegal, neither
# A lone member of a second firm, for tenant isolation.
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


def firm(firm_id: str, name: str) -> Firm:
    return Firm(
        id=firm_id,
        name=name,
        status="active",
        created_at="2026-01-01T00:00:00.000Z",
        updated_at="2026-01-01T00:00:00.000Z",
    )


def member(
    subject: str,
    firm_id: str,
    *,
    role: str = "attorney",
    is_admin: bool = False,
    access_all_cases: bool = False,
) -> FirmUser:
    return FirmUser(
        firm_id=firm_id,
        subject=subject,
        email=f"{subject[-4:]}@example.test",
        first_name="Person",
        last_name=subject[-4:],
        role=role,
        is_admin=is_admin,
        access_all_cases=access_all_cases,
        permissions=default_permissions(role),
        status="active",
        created_at="2026-01-01T00:00:00.000Z",
        updated_at="2026-01-01T00:00:00.000Z",
    )


@pytest.fixture
def access_log():
    return MemoryAccessLog()


@pytest.fixture
def client(access_log):
    firms = MemoryFirmStore()
    firms.create_firm(firm(FIRM_A, "Example & Partners"))
    firms.create_firm(firm(FIRM_B, "Other Firm LLP"))
    # Alice writes the note; Carol is FIRM_A's admin; Dave is FIRM_A staff
    # holding notes at its ADD_EDIT default but owning nothing. Dave carries
    # `access_all_cases` so the ownership tests below reach the note itself —
    # without it every one of his requests would 404 on the case lookup first
    # (he is not linked to it), which would test case visibility rather than
    # the seam this file is actually about.
    firms.add_user(member(ALICE, FIRM_A, role="attorney"))
    firms.add_user(member(CAROL, FIRM_A, role="attorney", is_admin=True))
    firms.add_user(member(DAVE, FIRM_A, role="paralegal", access_all_cases=True))
    firms.add_user(member(BOB, FIRM_B, role="attorney"))
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
            case_entity_store=MemoryCaseEntityStore(),
        )
    )
    return app.test_client()


def open_case(client, subject=ALICE):
    response = client.post(
        "/v1/cases", json={"chapter": 7, "district": "NDCA"}, headers=auth(subject)
    )
    assert response.status_code == 201
    return response.get_json()["id"]


def add_note(client, case_id, subject=ALICE, text="Called the client.", **extra):
    body = {"text": text, **extra}
    return client.post(f"/v1/cases/{case_id}/notes", json=body, headers=auth(subject))


# ── Auth and permission ─────────────────────────────────────────


@pytest.mark.parametrize(
    ("method", "path"),
    [
        ("post", "/v1/cases/any-id/notes"),
        ("get", "/v1/cases/any-id/notes"),
        ("put", "/v1/cases/any-id/notes/any-note"),
        ("delete", "/v1/cases/any-id/notes/any-note"),
    ],
)
def test_every_route_refuses_an_unauthenticated_caller(client, method, path):
    assert getattr(client, method)(path, json={}).status_code == 401


def test_another_firms_case_is_not_found_on_write(client):
    case_id = open_case(client, ALICE)
    assert add_note(client, case_id, subject=BOB).status_code == 404


def test_a_note_written_by_one_firm_is_invisible_to_another(client):
    case_id = open_case(client, ALICE)
    add_note(client, case_id)
    foreign = client.get(f"/v1/cases/{case_id}/notes", headers=auth(BOB))
    assert foreign.status_code == 404


def test_a_role_without_the_notes_feature_is_refused(client):
    firms = MemoryFirmStore()
    firms.create_firm(firm(FIRM_A, "Example & Partners"))
    hidden_permissions = {**default_permissions("attorney"), "notes": "hidden"}
    firms.add_user(
        FirmUser(
            firm_id=FIRM_A,
            subject=ALICE,
            email="alice@example.test",
            first_name="Alice",
            last_name="Attorney",
            role="attorney",
            is_admin=False,
            access_all_cases=False,
            permissions=hidden_permissions,
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
            case_store=MemoryCaseStore(),
            firm_store=firms,
            access_log=MemoryAccessLog(),
            case_entity_store=MemoryCaseEntityStore(),
        )
    )
    hidden_client = app.test_client()
    case_id = open_case(hidden_client, ALICE)
    response = add_note(hidden_client, case_id, subject=ALICE)
    assert response.status_code == 403


# ── Authorship ───────────────────────────────────────────────────


def test_creating_a_note_stamps_the_caller_as_author(client):
    case_id = open_case(client)
    response = add_note(client, case_id, text="Client called.")
    assert response.status_code == 201
    record = response.get_json()
    assert record["text"] == "Client called."
    assert record["author_subject"] == ALICE
    assert record["author_name"] == "Person a11c"
    assert record["case_id"] == case_id
    assert "provenance" not in record


def test_a_note_can_anchor_to_a_form_series(client):
    case_id = open_case(client)
    response = add_note(
        client, case_id, text="Check the lease term.", form_series="form/b106g"
    )
    assert response.status_code == 201
    assert response.get_json()["form_series"] == "form/b106g"


def test_a_client_supplied_author_is_ignored_on_create(client):
    case_id = open_case(client)
    response = add_note(
        client,
        case_id,
        text="note",
        author_subject=BOB,
        author_name="Bob Impersonator",
    )
    assert response.status_code == 201
    record = response.get_json()
    assert record["author_subject"] == ALICE
    assert record["author_name"] != "Bob Impersonator"


# ── Listing ──────────────────────────────────────────────────────


def test_listing_returns_notes_newest_first(client):
    case_id = open_case(client)
    first = add_note(client, case_id, text="first").get_json()["id"]
    second = add_note(client, case_id, text="second").get_json()["id"]
    listing = client.get(f"/v1/cases/{case_id}/notes", headers=auth(ALICE))
    assert listing.status_code == 200
    assert [note["id"] for note in listing.get_json()["notes"]] == [second, first]


# ── Ownership: edit and delete ────────────────────────────────────


def test_the_author_may_edit_their_own_note(client):
    case_id = open_case(client)
    note_id = add_note(client, case_id, text="first draft").get_json()["id"]
    response = client.put(
        f"/v1/cases/{case_id}/notes/{note_id}",
        json={"text": "corrected draft"},
        headers=auth(ALICE),
    )
    assert response.status_code == 200
    record = response.get_json()
    assert record["text"] == "corrected draft"
    # Authorship survives the edit unchanged.
    assert record["author_subject"] == ALICE


def test_a_firm_admin_may_edit_someone_elses_note(client):
    case_id = open_case(client)
    note_id = add_note(client, case_id, subject=ALICE, text="first draft").get_json()[
        "id"
    ]
    response = client.put(
        f"/v1/cases/{case_id}/notes/{note_id}",
        json={"text": "corrected by an admin"},
        headers=auth(CAROL),
    )
    assert response.status_code == 200
    record = response.get_json()
    assert record["text"] == "corrected by an admin"
    # The admin's edit does NOT reassign authorship.
    assert record["author_subject"] == ALICE
    assert record["author_name"] != "Person ca01"


def test_a_non_author_non_admin_may_not_edit_the_note(client):
    case_id = open_case(client)
    note_id = add_note(client, case_id, subject=ALICE).get_json()["id"]
    response = client.put(
        f"/v1/cases/{case_id}/notes/{note_id}",
        json={"text": "should not land"},
        headers=auth(DAVE),
    )
    assert response.status_code == 403


def test_the_author_may_delete_their_own_note(client):
    case_id = open_case(client)
    note_id = add_note(client, case_id, subject=ALICE).get_json()["id"]
    response = client.delete(
        f"/v1/cases/{case_id}/notes/{note_id}", headers=auth(ALICE)
    )
    assert response.status_code == 204


def test_a_firm_admin_may_delete_someone_elses_note(client):
    case_id = open_case(client)
    note_id = add_note(client, case_id, subject=ALICE).get_json()["id"]
    response = client.delete(
        f"/v1/cases/{case_id}/notes/{note_id}", headers=auth(CAROL)
    )
    assert response.status_code == 204


def test_a_non_author_non_admin_may_not_delete_the_note(client):
    case_id = open_case(client)
    note_id = add_note(client, case_id, subject=ALICE).get_json()["id"]
    response = client.delete(f"/v1/cases/{case_id}/notes/{note_id}", headers=auth(DAVE))
    assert response.status_code == 403
    # Still there.
    listing = client.get(f"/v1/cases/{case_id}/notes", headers=auth(DAVE))
    assert len(listing.get_json()["notes"]) == 1


def test_editing_an_id_the_server_never_minted_is_a_404(client):
    case_id = open_case(client)
    response = client.put(
        f"/v1/cases/{case_id}/notes/never-minted",
        json={"text": "x"},
        headers=auth(ALICE),
    )
    assert response.status_code == 404
