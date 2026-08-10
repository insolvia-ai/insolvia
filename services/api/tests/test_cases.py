"""Case CRUD behind auth, and who may see what (issues 8.3, firms).

Most of what matters here is what the endpoints REFUSE. A case store whose
only tests are happy-path is a store nobody has checked for the bugs that
matter — one firm reading another firm's cases, and one colleague reading a
matter they are not on.

The scenario this file exists for is the one the previous model could not
express at all: TWO USERS, ONE FIRM, ONE CASE. Under `owner_principal` a
colleague got a 404 on their own firm's matter; the whole point of the change
is that they no longer do, and that a colleague who is genuinely restricted
still does.

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
from insolvia_api.adapters.memory.mailer_client import InMemoryMailerClient
from insolvia_api.adapters.memory.waitlist_store import MemoryWaitlistStore
from insolvia_api.api.app_factory import create_app
from insolvia_api.api.dependencies import ApiDependencies
from insolvia_api.core.access import Accessor, may_see_case
from insolvia_api.core.cases import (
    INDEX_BY_ASSIGNEE,
    INDEX_BY_FIRM,
    Case,
    CaseAssignment,
    assign_case,
    case_from_item,
    case_item,
    decode_cursor,
    encode_cursor,
)
from insolvia_api.core.config import load_config
from insolvia_core.adapters.memory.firm_store import MemoryFirmStore
from insolvia_core.adapters.memory.jwks_provider import StaticJwksProvider
from insolvia_core.errors import ValidationError
from insolvia_core.firms import (
    CASES,
    HIDDEN,
    VIEW_ONLY,
    Firm,
    FirmUser,
    default_permissions,
)

ISSUER = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE00"
CLIENT_ID = "exampleappclientid000000"
KID = "test-key-1"

FIRM_A = "00000000-0000-4000-8000-00000000f18a"
FIRM_B = "00000000-0000-4000-8000-00000000f18b"

# Firm A. Four people, chosen to cover every combination that decides a read.
ALICE = "00000000-0000-4000-8000-00000000a11c"  # admin — sees every case
DANA = "00000000-0000-4000-8000-00000000da4a"  # access_all_cases, not admin
BOB = "00000000-0000-4000-8000-00000000b0b0"  # paralegal, linked-only
GREG = "00000000-0000-4000-8000-000000009e69"  # staff — role defaults only
ERIN = "00000000-0000-4000-8000-00000000e21e"  # disabled
# Firm B, and an admin there — the strongest caller the other tenant has.
CAROL = "00000000-0000-4000-8000-0000000ca201"
# In no firm at all: signed up, never provisioned.
FRANK = "00000000-0000-4000-8000-00000000f4a2"

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


def firm(firm_id: str, name: str, status: str = "active") -> Firm:
    return Firm(
        id=firm_id,
        name=name,
        status=status,
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
    permissions: dict[str, str] | None = None,
) -> FirmUser:
    return FirmUser(
        firm_id=firm_id,
        subject=subject,
        email=f"{subject[-4:]}@example.test",
        display_name=f"Person {subject[-4:]}",
        role=role,
        is_admin=is_admin,
        access_all_cases=access_all_cases,
        permissions=permissions or default_permissions(role),
        status=status,
        created_at="2026-01-01T00:00:00.000Z",
        updated_at="2026-01-01T00:00:00.000Z",
    )


@pytest.fixture
def store():
    return MemoryCaseStore()


@pytest.fixture
def access_log():
    return MemoryAccessLog()


@pytest.fixture
def firms():
    store = MemoryFirmStore()
    store.create_firm(firm(FIRM_A, "Example & Partners"))
    store.create_firm(firm(FIRM_B, "Other Firm LLP"))
    store.add_user(member(ALICE, is_admin=True))
    store.add_user(member(DANA, access_all_cases=True))
    store.add_user(member(BOB, role="paralegal"))
    store.add_user(member(GREG, role="staff"))
    store.add_user(member(ERIN, status="disabled"))
    store.add_user(member(CAROL, FIRM_B, is_admin=True))
    # FRANK is deliberately absent: a Cognito user with no firm row.
    return store


@pytest.fixture
def client(store, access_log, firms):
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


def accessor_for(firms, subject: str) -> Accessor:
    user = firms.find_user(subject)
    return Accessor(firm=firms.get_firm(user.firm_id), user=user)


# ── Auth and provisioning ───────────────────────────────────────


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


@pytest.mark.parametrize(
    ("method", "path"),
    [
        ("post", "/v1/cases"),
        ("get", "/v1/cases"),
        ("get", "/v1/cases/any-id"),
        ("patch", "/v1/cases/any-id"),
    ],
)
def test_a_signed_in_user_with_no_firm_is_403_not_401(client, method, path):
    """THE NEW FAILURE STATE. Their token is fine and signing in again will not
    help, so 401 would send them round a loop. It is a fact about their own
    account, so it is not hidden behind a 404 either."""
    response = getattr(client, method)(
        path, json={"chapter": 7, "district": "NDCA"}, headers=auth(FRANK)
    )
    assert response.status_code == 403
    assert response.get_json()["error"] == "ForbiddenError"


def test_a_disabled_user_is_refused(client):
    """Same 403, same message. "You were disabled" and "you were never added"
    are the same instruction to the caller — ask your firm's admin."""
    frank = client.get("/v1/cases", headers=auth(FRANK))
    erin = client.get("/v1/cases", headers=auth(ERIN))
    assert erin.status_code == frank.status_code == 403
    assert erin.get_json() == frank.get_json()


def test_a_suspended_firm_locks_out_even_its_admin(client, firms):
    firms.firms[FIRM_A] = firm(FIRM_A, "Example & Partners", status="suspended")
    assert client.get("/v1/cases", headers=auth(ALICE)).status_code == 403


def test_a_hidden_feature_is_403(client, firms):
    """The per-feature layer, on the route that already passed tenancy. Not a
    404: they are in the firm and the case list exists — telling them it does
    not would be a lie their client cannot act on."""
    firms.users[(FIRM_A, GREG)] = member(
        GREG, role="staff", permissions={**default_permissions("staff"), CASES: HIDDEN}
    )
    assert client.get("/v1/cases", headers=auth(GREG)).status_code == 403


def test_staff_cannot_open_a_matter_by_default(client):
    """A consequence of core/firms.default_permissions worth pinning at this
    level: staff get view_only on the case record, so POST /v1/cases is a 403
    for them until an admin grants add_edit. Reading is unaffected."""
    assert (
        client.post(
            "/v1/cases", json={"chapter": 7, "district": "NDCA"}, headers=auth(GREG)
        ).status_code
        == 403
    )
    assert client.get("/v1/cases", headers=auth(GREG)).status_code == 200


def test_view_only_can_read_but_not_write(client, firms):
    firms.users[(FIRM_A, DANA)] = member(
        DANA,
        access_all_cases=True,
        permissions={**default_permissions("attorney"), CASES: VIEW_ONLY},
    )
    case_id = open_case(client, ALICE)["id"]
    assert client.get(f"/v1/cases/{case_id}", headers=auth(DANA)).status_code == 200
    refused = client.patch(
        f"/v1/cases/{case_id}", json={"district": "CACD"}, headers=auth(DANA)
    )
    assert refused.status_code == 403
    assert (
        client.post(
            "/v1/cases", json={"chapter": 7, "district": "X"}, headers=auth(DANA)
        ).status_code
        == 403
    )


# ── Create ──────────────────────────────────────────────────────


def test_create_returns_the_case(client):
    body = open_case(client)
    assert body["chapter"] == 7
    assert body["district"] == "NDCA"
    assert body["status"] == "intake"
    assert body["id"]
    assert body["createdAt"] == body["updatedAt"]
    # Who opened it. Present now that a matter has several colleagues on it,
    # and keyed the same way the firm's staff list is.
    assert body["createdBy"] == ALICE


def test_create_never_returns_the_firm(client):
    """Every caller who can see this case is in that firm by construction, so
    the id would echo their own tenant back at them."""
    assert "firmId" not in open_case(client)


def test_create_ignores_a_firm_supplied_by_the_client(client, store):
    response = client.post(
        "/v1/cases",
        json={"chapter": 7, "district": "NDCA", "firmId": FIRM_B, "createdBy": CAROL},
        headers=auth(ALICE),
    )
    assert response.status_code == 201
    stored = store.cases[response.get_json()["id"]]
    assert (stored.firm_id, stored.created_by) == (FIRM_A, ALICE)


def test_creating_a_case_links_its_creator(client, store):
    """THE FAILURE THIS PREVENTS IS SILENT AND TOTAL. Bob is staff with no
    access_all_cases, so without the assignment written alongside the case he
    would open a matter he cannot see, cannot list and cannot reach by id —
    indistinguishable from the request having failed."""
    case_id = open_case(client, BOB)["id"]
    assert (case_id, BOB) in store.assignments
    assert client.get(f"/v1/cases/{case_id}", headers=auth(BOB)).status_code == 200
    assert [
        c["id"] for c in client.get("/v1/cases", headers=auth(BOB)).get_json()["cases"]
    ] == [case_id]


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


# ── Tenancy: the tests this module exists for ───────────────────


def test_a_colleague_can_read_the_firms_case(client, store):
    """WHAT THE OLD MODEL COULD NOT DO. Alice opens a matter; Dana, who has
    access_all_cases and was never linked to it, reads it. Under
    owner_principal this was a 404."""
    case_id = open_case(client, ALICE)["id"]
    assert (case_id, DANA) not in store.assignments
    assert client.get(f"/v1/cases/{case_id}", headers=auth(DANA)).status_code == 200


def test_an_admin_sees_a_colleagues_case_without_being_linked(client):
    case_id = open_case(client, BOB)["id"]
    assert client.get(f"/v1/cases/{case_id}", headers=auth(ALICE)).status_code == 200


def test_a_restricted_colleague_cannot_read_an_unlinked_case(client):
    """The other half, and the half that makes per-case linking mean anything.
    Bob is in the right firm and still gets a 404 on a matter he is not on."""
    case_id = open_case(client, ALICE)["id"]
    assert client.get(f"/v1/cases/{case_id}", headers=auth(BOB)).status_code == 404


def test_linking_a_colleague_makes_the_case_reachable(client, store, firms):
    case_id = open_case(client, ALICE)["id"]
    assert client.get(f"/v1/cases/{case_id}", headers=auth(BOB)).status_code == 404

    store.assign(assign_case(store.cases[case_id], subject=BOB, assigned_by=ALICE))

    assert client.get(f"/v1/cases/{case_id}", headers=auth(BOB)).status_code == 200
    # And it appears in the listing in the same write — the assignment row IS
    # the by-assignee index entry.
    listed = client.get("/v1/cases", headers=auth(BOB)).get_json()["cases"]
    assert [case["id"] for case in listed] == [case_id]


def test_another_firms_case_is_invisible_to_its_admin(client):
    """An admin is an admin OF A FIRM. There is no super-admin in this model,
    which is why the tenant check sits above the linkage check rather than
    beside it."""
    case_id = open_case(client, ALICE)["id"]
    assert client.get(f"/v1/cases/{case_id}", headers=auth(CAROL)).status_code == 404


def test_another_firms_case_cannot_be_updated(client, store):
    case_id = open_case(client, ALICE)["id"]
    response = client.patch(
        f"/v1/cases/{case_id}", json={"district": "CACD"}, headers=auth(CAROL)
    )
    assert response.status_code == 404
    assert store.cases[case_id].district == "NDCA"


def test_a_foreign_case_is_indistinguishable_from_a_missing_one(client):
    """404 for both, byte-for-byte — and for the unlinked in-firm case too.
    A different body or status would confirm the id exists: for another firm
    that is an enumeration oracle, and inside a firm it would tell any member
    which matters exist and who is on them."""
    case_id = open_case(client, ALICE)["id"]
    foreign = client.get(f"/v1/cases/{case_id}", headers=auth(CAROL))
    unlinked = client.get(f"/v1/cases/{case_id}", headers=auth(BOB))
    missing = client.get("/v1/cases/does-not-exist", headers=auth(CAROL))
    assert foreign.status_code == unlinked.status_code == missing.status_code == 404
    assert foreign.get_json() == unlinked.get_json() == missing.get_json()


def test_list_shows_only_the_firms_cases(client):
    open_case(client, ALICE)
    open_case(client, DANA)
    open_case(client, CAROL)
    assert len(client.get("/v1/cases", headers=auth(CAROL)).get_json()["cases"]) == 1
    assert len(client.get("/v1/cases", headers=auth(ALICE)).get_json()["cases"]) == 2


def test_list_shows_a_restricted_user_only_their_own_matters(client):
    open_case(client, ALICE)
    mine = open_case(client, BOB)["id"]
    listed = client.get("/v1/cases", headers=auth(BOB)).get_json()["cases"]
    assert [case["id"] for case in listed] == [mine]


def test_the_store_applies_the_rule_itself(store, firms):
    """Not only the route. The scoping rule lives in two places on purpose —
    it is the only thing between one firm's cases and another's, and a rule in
    exactly one place is one refactor from not existing."""
    case = Case(
        id="c1",
        firm_id=FIRM_A,
        created_by=ALICE,
        chapter=7,
        district="NDCA",
        status="intake",
        created_at="2026-01-01T00:00:00.000000Z",
        updated_at="2026-01-01T00:00:00.000000Z",
    )
    store.create(case, assign_case(case, subject=ALICE, assigned_by=ALICE))

    assert store.get("c1", accessor=accessor_for(firms, CAROL)) is None
    assert store.get("c1", accessor=accessor_for(firms, BOB)) is None
    assert store.get("c1", accessor=accessor_for(firms, ALICE)) is not None
    assert store.update(case_from_item(case_item(case))) is not None


def test_update_cannot_move_a_case_to_another_firm(store, firms):
    """The conditional write, closing the window between the route's read and
    this write."""
    case = Case(
        id="c1",
        firm_id=FIRM_A,
        created_by=ALICE,
        chapter=7,
        district="NDCA",
        status="intake",
        created_at="2026-01-01T00:00:00.000000Z",
        updated_at="2026-01-01T00:00:00.000000Z",
    )
    store.create(case, assign_case(case, subject=ALICE, assigned_by=ALICE))
    from dataclasses import replace

    assert store.update(replace(case, firm_id=FIRM_B)) is None
    assert store.cases["c1"].firm_id == FIRM_A


# ── The access rule, directly ───────────────────────────────────


@pytest.mark.parametrize(
    ("subject", "case_firm", "assigned", "visible"),
    [
        (ALICE, FIRM_A, False, True),  # admin, unlinked
        (DANA, FIRM_A, False, True),  # access_all_cases, unlinked
        (BOB, FIRM_A, False, False),  # restricted, unlinked
        (BOB, FIRM_A, True, True),  # restricted, linked
        (ALICE, FIRM_B, False, False),  # admin of the WRONG firm
        (ALICE, FIRM_B, True, False),  # ...even linked to it
    ],
)
def test_may_see_case_covers_every_combination(
    firms, subject, case_firm, assigned, visible
):
    """The whole rule as a table. The last row is the one worth reading twice:
    an assignment row for another firm's case must not grant anything, because
    the tenant check sits ABOVE the linkage check."""
    case = Case(
        id="c1",
        firm_id=case_firm,
        created_by=subject,
        chapter=7,
        district="NDCA",
        status="intake",
        created_at="2026-01-01T00:00:00.000000Z",
        updated_at="2026-01-01T00:00:00.000000Z",
    )
    assert (
        may_see_case(accessor_for(firms, subject), case, assigned=assigned) is visible
    )


# ── Read and list ───────────────────────────────────────────────


def test_get_returns_the_case(client):
    created = open_case(client)
    fetched = client.get(f"/v1/cases/{created['id']}", headers=auth(ALICE))
    assert fetched.status_code == 200
    assert fetched.get_json() == created


def stored_case(case_id: str, created_at: str, firm_id: str = FIRM_A) -> Case:
    return Case(
        id=case_id,
        firm_id=firm_id,
        created_by=ALICE,
        chapter=7,
        district="NDCA",
        status="intake",
        created_at=created_at,
        updated_at=created_at,
    )


def test_list_is_newest_first(store, firms):
    """Ordering is asserted against explicit timestamps rather than wall-clock
    creation order. Two cases minted in the same instant tie on GSI1SK and
    break on a random uuid, so a timing-dependent version of this test asserts
    something the design does not promise — see core/cases._timestamp."""
    for created_at in ("2026-01-01T00:00:00.000000Z", "2026-06-01T00:00:00.000000Z"):
        case = stored_case(f"case-{created_at}", created_at)
        store.create(case, assign_case(case, subject=ALICE, assigned_by=ALICE))
    page = store.list_for_accessor(accessor_for(firms, ALICE), limit=10, cursor=None)
    assert [case.created_at for case in page.cases] == [
        "2026-06-01T00:00:00.000000Z",
        "2026-01-01T00:00:00.000000Z",
    ]


def test_both_listings_order_identically(store, firms):
    """The two indexes have different sort keys on different items, and they
    must agree — otherwise a user's case list reshuffles when an admin grants
    them access_all_cases. core/cases.listing_sort_key is what makes them."""
    for created_at in ("2026-01-01T00:00:00.000000Z", "2026-06-01T00:00:00.000000Z"):
        case = stored_case(f"case-{created_at}", created_at)
        store.create(case, assign_case(case, subject=BOB, assigned_by=ALICE))
    by_firm = store.list_for_accessor(accessor_for(firms, ALICE), limit=10, cursor=None)
    by_assignee = store.list_for_accessor(
        accessor_for(firms, BOB), limit=10, cursor=None
    )
    assert [c.id for c in by_firm.cases] == [c.id for c in by_assignee.cases]


def test_list_returns_the_firms_cases_over_http(client):
    first = open_case(client, ALICE, district="NDCA")
    second = open_case(client, DANA, district="CACD")
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


def test_a_cursor_from_the_other_listing_is_refused(client, firms):
    """THE AWKWARD CONSEQUENCE OF TWO INDEXES, made loud.

    Alice pages through by-firm, an admin revokes her access_all_cases, and her
    next request would resume a by-firm scan position inside a by-assignee
    query. Without the index tag DynamoDB accepts the key and returns whatever
    sorts after it — she silently never sees the cases in between. With it she
    gets a 400 and the client starts the listing again, which is correct,
    because her listing genuinely changed.
    """
    for _ in range(3):
        open_case(client, ALICE)
    cursor = client.get("/v1/cases?limit=2", headers=auth(ALICE)).get_json()[
        "nextCursor"
    ]

    firms.users[(FIRM_A, ALICE)] = member(ALICE, is_admin=False, access_all_cases=False)
    refused = client.get(f"/v1/cases?limit=2&cursor={cursor}", headers=auth(ALICE))
    assert refused.status_code == 400


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


def test_a_colleague_can_update_the_firms_case(client):
    created = open_case(client, ALICE)
    updated = client.patch(
        f"/v1/cases/{created['id']}",
        json={"status": "ready_to_file"},
        headers=auth(DANA),
    )
    assert updated.status_code == 200
    # created_by is not rewritten by whoever edits it — it records who OPENED
    # the matter, and an edit is not an opening.
    assert updated.get_json()["createdBy"] == ALICE


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


def test_the_log_records_the_person_not_the_firm(client, access_log):
    """The firm is the tenant; the person is the actor. This table exists to
    answer "who saw this file", and a firm id is not an answer to that."""
    open_case(client, DANA)
    assert access_log.events[-1].principal == DANA


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
    client.get(f"/v1/cases/{case_id}", headers=auth(CAROL))
    denied = access_log.events[-1]
    assert (denied.action, denied.outcome, denied.principal, denied.case_id) == (
        "case.read",
        "denied",
        CAROL,
        case_id,
    )


def test_an_in_firm_refusal_is_also_recorded_as_denied(client, access_log):
    """A colleague reaching for a matter they are not on is a different event
    from a stranger walking ids, and both belong in the log."""
    case_id = open_case(client, ALICE)["id"]
    client.get(f"/v1/cases/{case_id}", headers=auth(BOB))
    assert (access_log.events[-1].outcome, access_log.events[-1].principal) == (
        "denied",
        BOB,
    )


def test_a_refused_update_is_recorded_as_denied(client, access_log):
    case_id = open_case(client, ALICE)["id"]
    client.patch(f"/v1/cases/{case_id}", json={"district": "CACD"}, headers=auth(CAROL))
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


def test_an_unprovisioned_request_records_nothing(client, access_log):
    """The 403 happens before the route body, so there is no case to key a row
    by — and inventing one would put a row under an id the caller never named."""
    client.get("/v1/cases/anything", headers=auth(FRANK))
    assert access_log.events == []


# ── Item shape and cursors ──────────────────────────────────────


def test_case_item_round_trips(client, store):
    case_id = open_case(client)["id"]
    original = store.cases[case_id]
    assert case_from_item(case_item(original)) == original


def test_case_item_carries_the_by_firm_keys(client, store):
    case_id = open_case(client)["id"]
    item = case_item(store.cases[case_id])
    assert item["PK"] == f"CASE#{case_id}"
    assert item["SK"] == "META"
    assert item["GSI1PK"] == f"FIRM#{FIRM_A}"
    assert item["GSI1SK"] == f"{store.cases[case_id].created_at}#{case_id}"
    # The case itself is NOT in the by-assignee index — its assignments are,
    # which is what makes that index sparse in the other direction.
    assert "GSI2PK" not in item


def test_an_assignment_carries_the_by_assignee_keys(client, store):
    from insolvia_api.core.cases import assignment_from_item, assignment_item

    case_id = open_case(client)["id"]
    assignment = store.assignments[(case_id, ALICE)]
    item = assignment_item(assignment)
    assert item["PK"] == f"CASE#{case_id}"
    assert item["SK"] == f"ASSIGNEE#{ALICE}"
    assert item["GSI2PK"] == f"ASSIGNEE#{ALICE}"
    # The CASE's timestamp, not the assignment's — otherwise somebody's list
    # would order by when they were added to matters rather than by when the
    # matters were opened.
    assert item["GSI2SK"] == f"{store.cases[case_id].created_at}#{case_id}"
    assert assignment_from_item(item) == assignment


def test_case_from_item_rejects_a_malformed_row():
    with pytest.raises(ValidationError):
        case_from_item({"id": "x"})


def test_case_from_item_refuses_a_pre_firm_row():
    """A row written under owner_principal has no firm, and the tempting
    reading — treat the old owner as the firm — would put a Cognito subject
    where a firm id goes and build a one-person tenant whose id is somebody's
    identity."""
    with pytest.raises(ValidationError):
        case_from_item(
            {
                "id": "c1",
                "ownerPrincipal": ALICE,
                "chapter": 7,
                "district": "NDCA",
                "status": "intake",
                "createdAt": "2026-01-01T00:00:00.000000Z",
                "updatedAt": "2026-01-01T00:00:00.000000Z",
            }
        )


def test_cursors_round_trip():
    cursor = encode_cursor({"GSI1SK": "a#b"}, index=INDEX_BY_FIRM)
    assert decode_cursor(cursor, index=INDEX_BY_FIRM) == {"GSI1SK": "a#b"}


def test_a_cursor_is_refused_by_the_other_index():
    cursor = encode_cursor({"GSI1SK": "a#b"}, index=INDEX_BY_FIRM)
    with pytest.raises(ValidationError):
        decode_cursor(cursor, index=INDEX_BY_ASSIGNEE)


@pytest.mark.parametrize("cursor", ["!!!", "", "eyJhIjogMX0="])
def test_decode_cursor_rejects_junk(cursor):
    # The last one is valid base64 of {"a": 1} — a non-string value, which
    # would otherwise reach DynamoDB as part of an ExclusiveStartKey.
    with pytest.raises(ValidationError):
        decode_cursor(cursor, index=INDEX_BY_FIRM)


def test_an_assignment_is_idempotent(store, firms):
    """The firm-admin UI cannot tell whether its first request landed."""
    case = stored_case("c1", "2026-01-01T00:00:00.000000Z")
    store.create(case, assign_case(case, subject=ALICE, assigned_by=ALICE))
    store.assign(assign_case(case, subject=BOB, assigned_by=ALICE))
    store.assign(assign_case(case, subject=BOB, assigned_by=ALICE))
    assert len(store.assignees("c1")) == 2


def test_unassigning_twice_reports_the_truth(store):
    case = stored_case("c1", "2026-01-01T00:00:00.000000Z")
    store.create(case, assign_case(case, subject=ALICE, assigned_by=ALICE))
    assert store.unassign("c1", ALICE) is True
    assert store.unassign("c1", ALICE) is False


def test_unassigned_is_a_real_state(store, firms):
    """Unlinking the last person leaves a case only the firm's admins and
    access_all_cases users can reach. That is a state the model allows on
    purpose — a matter with nobody on it is still the firm's."""
    case = stored_case("c1", "2026-01-01T00:00:00.000000Z")
    store.create(case, assign_case(case, subject=BOB, assigned_by=ALICE))
    store.unassign("c1", BOB)
    assert store.get("c1", accessor=accessor_for(firms, BOB)) is None
    assert store.get("c1", accessor=accessor_for(firms, ALICE)) is not None


def test_case_assignment_denormalises_the_cases_timestamp():
    case = stored_case("c1", "2026-01-01T00:00:00.000000Z")
    assignment = assign_case(case, subject=BOB, assigned_by=ALICE)
    assert isinstance(assignment, CaseAssignment)
    assert assignment.case_created_at == case.created_at
    # Its own stamp is a different fact and is recorded separately.
    assert assignment.assigned_at != case.created_at
