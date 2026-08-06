"""A firm's staff list and case assignment, behind auth and permissions.

The two things this file exists to hold down:

  1. EVERY ROUTE IS SCOPED TO THE CALLER'S FIRM. There is no firm id in any
     path, so the tests here are about what happens when an admin of one firm
     names a subject belonging to another.
  2. A FIRM CANNOT LOCK ITSELF OUT. Self-signup is disabled on the pool, so a
     firm with no active admin cannot appoint one — it is the single
     irrecoverable mistake this surface makes reachable, and it is reachable by
     accident.

Tokens are signed for real, as everywhere else. Every identifier below is
obviously fake. This repo is public.
"""

from __future__ import annotations

import time

import jwt
import pytest
from cryptography.hazmat.primitives.asymmetric import rsa
from insolvia_api.adapters.memory.access_log import MemoryAccessLog
from insolvia_api.adapters.memory.case_store import MemoryCaseStore
from insolvia_api.adapters.memory.firm_store import MemoryFirmStore
from insolvia_api.adapters.memory.jwks_provider import StaticJwksProvider
from insolvia_api.adapters.memory.mailer_client import InMemoryMailerClient
from insolvia_api.adapters.memory.user_directory import MemoryUserDirectory
from insolvia_api.adapters.memory.waitlist_store import MemoryWaitlistStore
from insolvia_api.api.app_factory import create_app
from insolvia_api.api.dependencies import ApiDependencies
from insolvia_api.core.config import load_config
from insolvia_api.core.firms import (
    ADD_EDIT,
    DOCUMENTS,
    HIDDEN,
    VIEW_ONLY,
    Firm,
    FirmUser,
    default_permissions,
    would_leave_no_admin,
)

ISSUER = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE00"
CLIENT_ID = "exampleappclientid000000"
KID = "test-key-1"

FIRM_A = "00000000-0000-4000-8000-00000000f18a"
FIRM_B = "00000000-0000-4000-8000-00000000f18b"

ALICE = "00000000-0000-4000-8000-00000000a11c"  # firm A admin
DANA = "00000000-0000-4000-8000-00000000da4a"  # firm A, access_all_cases
BOB = "00000000-0000-4000-8000-00000000b0b0"  # firm A, linked-only
CAROL = "00000000-0000-4000-8000-0000000ca201"  # firm B admin

_PRIVATE_KEY = rsa.generate_private_key(public_exponent=65537, key_size=2048)
_PUBLIC_KEY = _PRIVATE_KEY.public_key()


def auth(subject: str) -> dict[str, str]:
    now = int(time.time())
    token = jwt.encode(
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
    return {"Authorization": f"Bearer {token}"}


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
    firm_id: str = FIRM_A,
    *,
    role: str = "attorney",
    is_admin: bool = False,
    access_all_cases: bool = False,
    status: str = "active",
    display_name: str | None = None,
    permissions: dict[str, str] | None = None,
) -> FirmUser:
    return FirmUser(
        firm_id=firm_id,
        subject=subject,
        email=f"{subject[-4:]}@example.test",
        display_name=display_name or f"Person {subject[-4:]}",
        role=role,
        is_admin=is_admin,
        access_all_cases=access_all_cases,
        permissions=permissions or default_permissions(role),
        status=status,
        created_at="2026-01-01T00:00:00.000Z",
        updated_at="2026-01-01T00:00:00.000Z",
    )


@pytest.fixture
def firms():
    store = MemoryFirmStore()
    store.create_firm(firm(FIRM_A, "Example & Partners"))
    store.create_firm(firm(FIRM_B, "Other Firm LLP"))
    store.add_user(member(ALICE, is_admin=True, display_name="Alice Admin"))
    store.add_user(member(DANA, access_all_cases=True, display_name="Dana Attorney"))
    store.add_user(member(BOB, role="paralegal", display_name="Bob Paralegal"))
    store.add_user(member(CAROL, FIRM_B, is_admin=True, display_name="Carol Other"))
    return store


@pytest.fixture
def cases():
    return MemoryCaseStore()


@pytest.fixture
def directory():
    return MemoryUserDirectory()


@pytest.fixture
def access_log():
    return MemoryAccessLog()


@pytest.fixture
def client(firms, cases, directory, access_log):
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
            case_store=cases,
            access_log=access_log,
            firm_store=firms,
            user_directory=directory,
        )
    )
    return app.test_client()


def open_case(client, subject=ALICE):
    response = client.post(
        "/v1/cases", json={"chapter": 7, "district": "NDCA"}, headers=auth(subject)
    )
    assert response.status_code == 201
    return response.get_json()["id"]


# ── Permissions on the surface itself ───────────────────────────


@pytest.mark.parametrize(
    ("method", "path"),
    [
        ("get", "/v1/firm/users"),
        ("post", "/v1/firm/users"),
        ("patch", f"/v1/firm/users/{BOB}"),
        ("delete", f"/v1/firm/users/{BOB}"),
    ],
)
def test_administration_needs_the_admin_flag(client, method, path):
    """Dana has access_all_cases and is not an admin. Seeing every matter is a
    different thing from managing the firm's people, which is why the two are
    separate axes — a supervising attorney should not have to become an
    administrator to read the caseload."""
    response = getattr(client, method)(path, json={"role": "staff"}, headers=auth(DANA))
    assert response.status_code == 403


@pytest.mark.parametrize(
    ("method", "path"),
    [
        ("get", "/v1/firm/users"),
        ("get", "/v1/firm/directory"),
        ("post", "/v1/firm/users"),
    ],
)
def test_administration_requires_a_token(client, method, path):
    assert getattr(client, method)(path, json={}).status_code == 401


# ── The directory ───────────────────────────────────────────────


def test_anyone_in_the_firm_can_resolve_a_colleagues_name(client):
    """Without this a case list reads "opened by 00000000-0000-…". It is gated
    on CASES rather than firm administration because that is what it is for."""
    body = client.get("/v1/firm/directory", headers=auth(BOB)).get_json()
    assert {p["displayName"] for p in body["people"]} == {
        "Alice Admin",
        "Bob Paralegal",
        "Dana Attorney",
    }


def test_the_directory_withholds_everything_administrative(client):
    """Three fields. A paralegal does not need their colleagues' email
    addresses, permission maps, or whether somebody has been disabled."""
    person = client.get("/v1/firm/directory", headers=auth(BOB)).get_json()["people"][0]
    assert set(person) == {"subject", "displayName", "role"}


def test_the_directory_is_firm_scoped(client):
    subjects = {
        p["subject"]
        for p in client.get("/v1/firm/directory", headers=auth(BOB)).get_json()[
            "people"
        ]
    }
    assert CAROL not in subjects


def test_the_directory_keeps_disabled_colleagues(client, firms):
    """A case opened by somebody who has since left still says `createdBy:
    <their subject>`. Dropping them would turn that into an unresolvable id —
    the client is rendering history, not offering a picker."""
    firms.users[(FIRM_A, BOB)] = member(
        BOB, role="paralegal", display_name="Bob Paralegal", status="disabled"
    )
    subjects = {
        p["subject"]
        for p in client.get("/v1/firm/directory", headers=auth(ALICE)).get_json()[
            "people"
        ]
    }
    assert BOB in subjects


# ── The admin list ──────────────────────────────────────────────


def test_the_admin_list_is_the_whole_record(client):
    user = client.get("/v1/firm/users", headers=auth(ALICE)).get_json()["users"][0]
    assert {"subject", "email", "permissions", "status", "isAdmin"} <= set(user)


def test_the_admin_list_is_firm_scoped(client):
    subjects = {
        u["subject"]
        for u in client.get("/v1/firm/users", headers=auth(ALICE)).get_json()["users"]
    }
    assert subjects == {ALICE, DANA, BOB}


# ── Adding a colleague ──────────────────────────────────────────


def test_adding_a_colleague_creates_the_account_then_the_row(client, firms, directory):
    response = client.post(
        "/v1/firm/users",
        json={
            "email": "New.Person@example.test",
            "displayName": "New Person",
            "role": "paralegal",
        },
        headers=auth(ALICE),
    )
    assert response.status_code == 201
    body = response.get_json()

    # The subject is the one the directory minted, not one this service made
    # up — a made-up subject is a row nobody can ever sign in as.
    assert body["subject"] == directory.subjects["new.person@example.test"]
    assert firms.get_user(FIRM_A, body["subject"]) is not None
    # Added to the CALLER'S firm. There is no firm id in the request anywhere.
    assert firms.get_user(FIRM_A, body["subject"]).firm_id == FIRM_A


def test_a_new_colleague_gets_the_role_defaults(client):
    body = client.post(
        "/v1/firm/users",
        json={"email": "clerk@example.test", "displayName": "Clerk", "role": "staff"},
        headers=auth(ALICE),
    ).get_json()
    assert body["permissions"] == default_permissions("staff")
    assert body["isAdmin"] is False
    assert body["accessAllCases"] is False


def test_supplied_permissions_are_merged_over_the_defaults(client):
    body = client.post(
        "/v1/firm/users",
        json={
            "email": "clerk@example.test",
            "displayName": "Clerk",
            "role": "staff",
            "permissions": {DOCUMENTS: HIDDEN},
        },
        headers=auth(ALICE),
    ).get_json()
    assert body["permissions"][DOCUMENTS] == HIDDEN
    # And the rest of the staff defaults survive.
    assert body["permissions"]["cases"] == VIEW_ONLY


def test_an_address_that_already_has_an_account_is_409(client, directory):
    directory.create_user("taken@example.test")
    response = client.post(
        "/v1/firm/users",
        json={"email": "taken@example.test", "displayName": "X", "role": "staff"},
        headers=auth(ALICE),
    )
    assert response.status_code == 409
    assert response.get_json()["error"] == "ConflictError"


def test_a_failed_account_creation_writes_no_row(client, firms, directory):
    """Cognito first, and the failure in the middle leaves a pool user in no
    firm — a state the system already handles. The other order leaves a
    firm-user row keyed on a subject that does not exist, which is invisible to
    everyone and repairs nothing."""
    directory.create_user("taken@example.test")
    before = len(firms.list_users(FIRM_A))
    client.post(
        "/v1/firm/users",
        json={"email": "taken@example.test", "displayName": "X", "role": "staff"},
        headers=auth(ALICE),
    )
    assert len(firms.list_users(FIRM_A)) == before


@pytest.mark.parametrize(
    ("payload", "field"),
    [
        ({"displayName": "X", "role": "staff"}, "email"),
        ({"email": "not-an-email", "displayName": "X", "role": "staff"}, "email"),
        ({"email": "a@b.test", "role": "staff"}, "displayName"),
        ({"email": "a@b.test", "displayName": "X"}, "role"),
        ({"email": "a@b.test", "displayName": "X", "role": "partner"}, "role"),
        (
            {
                "email": "a@b.test",
                "displayName": "X",
                "role": "staff",
                "isAdmin": "true",
            },
            "isAdmin",
        ),
    ],
)
def test_adding_a_colleague_validates(client, payload, field):
    response = client.post("/v1/firm/users", json=payload, headers=auth(ALICE))
    assert response.status_code == 400
    assert field in response.get_json()["fields"]


# ── Changing a colleague ────────────────────────────────────────


def test_a_role_change_leaves_the_permission_map_alone(client, firms):
    firms.users[(FIRM_A, BOB)] = member(
        BOB,
        role="paralegal",
        permissions={**default_permissions("paralegal"), DOCUMENTS: HIDDEN},
    )
    body = client.patch(
        f"/v1/firm/users/{BOB}", json={"role": "attorney"}, headers=auth(ALICE)
    ).get_json()
    assert body["role"] == "attorney"
    assert body["permissions"][DOCUMENTS] == HIDDEN


def test_another_firms_user_is_404_not_403(client, firms):
    """Same answer as a subject that does not exist. A 403 would confirm the
    subject is real and turn this into a probe for who works where."""
    real = client.patch(
        f"/v1/firm/users/{CAROL}", json={"role": "staff"}, headers=auth(ALICE)
    )
    invented = client.patch(
        "/v1/firm/users/00000000-0000-4000-8000-000000000000",
        json={"role": "staff"},
        headers=auth(ALICE),
    )
    assert real.status_code == invented.status_code == 404
    assert real.get_json() == invented.get_json()
    # And Carol is untouched in her own firm.
    assert firms.get_user(FIRM_B, CAROL).role == "attorney"


def test_disabling_a_colleague_locks_them_out(client, firms):
    assert client.get("/v1/cases", headers=auth(BOB)).status_code == 200
    client.patch(
        f"/v1/firm/users/{BOB}", json={"status": "disabled"}, headers=auth(ALICE)
    )
    assert client.get("/v1/cases", headers=auth(BOB)).status_code == 403


# ── The lock-out rule ───────────────────────────────────────────


def test_the_last_admin_cannot_demote_themselves(client):
    """THE ONE IRRECOVERABLE MISTAKE. Self-signup is off, so a firm with no
    active admin cannot appoint one — nobody inside it can fix it."""
    response = client.patch(
        f"/v1/firm/users/{ALICE}", json={"isAdmin": False}, headers=auth(ALICE)
    )
    assert response.status_code == 409
    assert "administrator" in response.get_json()["message"]


def test_the_last_admin_cannot_disable_themselves(client):
    assert (
        client.patch(
            f"/v1/firm/users/{ALICE}", json={"status": "disabled"}, headers=auth(ALICE)
        ).status_code
        == 409
    )


def test_the_last_admin_cannot_be_removed(client, firms):
    assert (
        client.delete(f"/v1/firm/users/{ALICE}", headers=auth(ALICE)).status_code == 409
    )
    assert firms.get_user(FIRM_A, ALICE) is not None


def test_demoting_is_allowed_once_somebody_else_is_an_admin(client):
    assert (
        client.patch(
            f"/v1/firm/users/{DANA}", json={"isAdmin": True}, headers=auth(ALICE)
        ).status_code
        == 200
    )
    assert (
        client.patch(
            f"/v1/firm/users/{ALICE}", json={"isAdmin": False}, headers=auth(ALICE)
        ).status_code
        == 200
    )


def test_promoting_a_disabled_colleague_does_not_count(client, firms):
    """The subtle version of the same mistake: promote a colleague, demote
    yourself, discover the colleague was disabled. `is_active_admin` requires
    both, which is why the check is not a count of `is_admin`."""
    firms.users[(FIRM_A, DANA)] = member(DANA, is_admin=True, status="disabled")
    assert (
        client.patch(
            f"/v1/firm/users/{ALICE}", json={"isAdmin": False}, headers=auth(ALICE)
        ).status_code
        == 409
    )


def test_an_admin_cannot_demote_the_other_last_admin(client, firms):
    """The rule is about the FIRM, not about the caller. Alice hands the flag
    to Dana and leaves; Dana must not then be demotable by... nobody, which is
    why the check takes the whole staff list rather than comparing to `self`."""
    firms.users[(FIRM_A, DANA)] = member(DANA, is_admin=True)
    client.patch(f"/v1/firm/users/{ALICE}", json={"isAdmin": False}, headers=auth(DANA))
    assert (
        client.patch(
            f"/v1/firm/users/{DANA}", json={"isAdmin": False}, headers=auth(DANA)
        ).status_code
        == 409
    )


@pytest.mark.parametrize(
    ("admins", "change", "expected"),
    [
        # one admin, demoted -> nobody left
        ((True,), "demote", True),
        # one admin, removed -> nobody left
        ((True,), "remove", True),
        # two admins, one demoted -> one left
        ((True, True), "demote", False),
        # two admins, one of them disabled, the active one demoted
        ((True, False), "demote", True),
    ],
)
def test_would_leave_no_admin_directly(admins, change, expected):
    """The rule as a table, at the level it is actually decided. `admins` is a
    tuple of (is_admin AND active) for the firm's people; the first is the one
    being changed."""
    people = [
        member(
            f"00000000-0000-4000-8000-00000000000{i}",
            is_admin=True,
            status="active" if active else "disabled",
        )
        for i, active in enumerate(admins)
    ]
    target = people[0]
    if change == "demote":
        from dataclasses import replace

        assert (
            would_leave_no_admin(people, changed=replace(target, is_admin=False))
            is expected
        )
    else:
        assert would_leave_no_admin(people, removed=target.subject) is expected


# ── Case assignment ─────────────────────────────────────────────


def test_linking_a_colleague_is_case_work_not_administration(client, cases):
    """`cases: add_edit`, not `firm_administration`. The attorney running a
    matter puts people on it; they should not have to be able to manage user
    accounts to do it."""
    case_id = open_case(client, DANA)
    assert (
        client.put(
            f"/v1/cases/{case_id}/assignees/{BOB}", headers=auth(DANA)
        ).status_code
        == 204
    )
    assert client.get(f"/v1/cases/{case_id}", headers=auth(BOB)).status_code == 200


def test_linking_is_idempotent(client):
    case_id = open_case(client, ALICE)
    first = client.put(f"/v1/cases/{case_id}/assignees/{BOB}", headers=auth(ALICE))
    second = client.put(f"/v1/cases/{case_id}/assignees/{BOB}", headers=auth(ALICE))
    assert first.status_code == second.status_code == 204


def test_a_subject_from_another_firm_cannot_be_linked(client, cases):
    """Without this the endpoint writes an assignment row naming somebody who
    is not our tenant's — which grants nothing today, and would put them on the
    case the moment they joined some firm."""
    case_id = open_case(client, ALICE)
    response = client.put(f"/v1/cases/{case_id}/assignees/{CAROL}", headers=auth(ALICE))
    assert response.status_code == 404
    assert [a.subject for a in cases.assignees(case_id)] == [ALICE]


def test_a_case_you_cannot_see_cannot_be_assigned(client):
    """Bob is a paralegal without access_all_cases, so Alice's matter is a 404
    to him — and so is putting himself on it."""
    case_id = open_case(client, ALICE)
    assert (
        client.put(
            f"/v1/cases/{case_id}/assignees/{BOB}", headers=auth(BOB)
        ).status_code
        == 404
    )


def test_listing_assignees_needs_only_a_view(client):
    case_id = open_case(client, ALICE)
    client.put(f"/v1/cases/{case_id}/assignees/{BOB}", headers=auth(ALICE))
    body = client.get(f"/v1/cases/{case_id}/assignees", headers=auth(BOB)).get_json()
    assert {a["subject"] for a in body["assignees"]} == {ALICE, BOB}
    # Subjects, not names — resolving one is the directory's job, and a copy of
    # the display name here would go stale the moment somebody is renamed.
    assert set(body["assignees"][0]) == {"subject", "assignedAt", "assignedBy"}


def test_unlinking_the_last_person_is_allowed(client, cases):
    """Unlike a firm's last admin, and the asymmetry is the point: a case with
    nobody on it is still the firm's, so its admins can always assign somebody
    new. A firm with no admin has no route back."""
    case_id = open_case(client, BOB)
    assert (
        client.delete(
            f"/v1/cases/{case_id}/assignees/{BOB}", headers=auth(ALICE)
        ).status_code
        == 204
    )
    assert cases.assignees(case_id) == ()
    assert client.get(f"/v1/cases/{case_id}", headers=auth(ALICE)).status_code == 200


def test_a_caller_can_unlink_themselves_and_lose_the_case(client):
    """The honest consequence of "I am no longer on this matter". Refusing it
    would leave somebody unable to hand a case over without an admin."""
    case_id = open_case(client, BOB)
    assert (
        client.delete(
            f"/v1/cases/{case_id}/assignees/{BOB}", headers=auth(BOB)
        ).status_code
        == 204
    )
    assert client.get(f"/v1/cases/{case_id}", headers=auth(BOB)).status_code == 404


def test_unlinking_somebody_who_is_not_on_the_case_is_404(client):
    case_id = open_case(client, ALICE)
    assert (
        client.delete(
            f"/v1/cases/{case_id}/assignees/{DANA}", headers=auth(ALICE)
        ).status_code
        == 404
    )


def test_a_view_only_caller_cannot_change_assignments(client, firms):
    firms.users[(FIRM_A, DANA)] = member(
        DANA,
        access_all_cases=True,
        permissions={**default_permissions("attorney"), "cases": VIEW_ONLY},
    )
    case_id = open_case(client, ALICE)
    assert (
        client.put(
            f"/v1/cases/{case_id}/assignees/{BOB}", headers=auth(DANA)
        ).status_code
        == 403
    )


def test_assignment_changes_are_recorded(client, access_log):
    """Linking changes who may read the file, so it belongs in the log that
    answers who saw it — with the person doing the linking as the actor, not
    the person being linked."""
    case_id = open_case(client, ALICE)
    client.put(f"/v1/cases/{case_id}/assignees/{BOB}", headers=auth(ALICE))
    linked = access_log.events[-1]
    assert (linked.action, linked.case_id, linked.principal) == (
        "case.update",
        case_id,
        ALICE,
    )

    client.delete(f"/v1/cases/{case_id}/assignees/{BOB}", headers=auth(ALICE))
    assert access_log.events[-1].action == "case.update"


def test_cors_allows_the_methods_the_routes_use(client):
    """PUT and DELETE arrived with assignment. Both are non-simple methods, so
    a browser preflights them — a missing entry is not a subtle degradation, it
    is the request never being sent, with a CORS error in the console and a
    200 in our logs for the OPTIONS."""
    response = client.get(
        "/v1/firm/directory",
        headers={**auth(BOB), "Origin": "http://localhost:3000"},
    )
    allowed = response.headers["Access-Control-Allow-Methods"]
    for method in ("GET", "POST", "PATCH", "PUT", "DELETE", "OPTIONS"):
        assert method in allowed


def test_permission_levels_on_the_administration_surface(client, firms):
    """`view_only` on firm_administration reads the staff list and changes
    nothing — the level distinction has to mean something on this surface too,
    not only on cases."""
    firms.users[(FIRM_A, DANA)] = member(
        DANA,
        permissions={
            **default_permissions("attorney"),
            "firm_administration": VIEW_ONLY,
        },
    )
    assert client.get("/v1/firm/users", headers=auth(DANA)).status_code == 200
    assert (
        client.patch(
            f"/v1/firm/users/{BOB}", json={"role": "staff"}, headers=auth(DANA)
        ).status_code
        == 403
    )
    firms.users[(FIRM_A, DANA)] = member(
        DANA,
        permissions={
            **default_permissions("attorney"),
            "firm_administration": ADD_EDIT,
        },
    )
    assert (
        client.patch(
            f"/v1/firm/users/{BOB}", json={"role": "staff"}, headers=auth(DANA)
        ).status_code
        == 200
    )
