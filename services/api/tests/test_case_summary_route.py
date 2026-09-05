"""GET /v1/cases/<id>/summary — the case overview's one read.

Two things are worth pinning here and nothing else is.

The first is that the numbers ARE the schedules' numbers. The projections
already have goldens (tests/test_form_projections.py) and re-asserting their
arithmetic here would be a second, weaker copy of that; what this file asserts
is the wiring — that a claim entered as secured reaches `secured` and not
`nonpriorityUnsecured`, and that the totals move when the case does. If those
hold, the delegation is right and the projections' own suite covers the rest.

The second is the money's WIRE TYPE. These are Decimals on a bankruptcy
filing, and JSON numbers would hand them to the client as doubles.

Every identifier below is obviously fake; this repo is public.
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
from insolvia_core.adapters.memory.debtor_store import MemoryDebtorStore
from insolvia_core.adapters.memory.firm_store import MemoryFirmStore
from insolvia_core.adapters.memory.jwks_provider import StaticJwksProvider
from insolvia_core.firms import Firm, FirmUser, default_permissions

ISSUER = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE00"
CLIENT_ID = "exampleappclientid000000"
FIRM_A = "00000000-0000-4000-8000-00000000f18a"
FIRM_B = "00000000-0000-4000-8000-00000000f18b"
ALICE = "00000000-0000-4000-8000-00000000a11c"
BOB = "00000000-0000-4000-8000-00000000b0b0"
KID = "test-key-1"

TYPED = {"source": "staff_typed"}

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


def member(subject: str, firm_id: str) -> FirmUser:
    return FirmUser(
        firm_id=firm_id,
        subject=subject,
        email=f"{subject[-4:]}@example.test",
        first_name="Person",
        last_name=subject[-4:],
        role="attorney",
        is_admin=True,
        access_all_cases=False,
        permissions=default_permissions("attorney"),
        status="active",
        created_at="2026-01-01T00:00:00.000Z",
        updated_at="2026-01-01T00:00:00.000Z",
    )


@pytest.fixture
def client():
    firms = MemoryFirmStore()
    firms.create_firm(firm(FIRM_A, "Example & Partners"))
    firms.create_firm(firm(FIRM_B, "Other Firm LLP"))
    firms.add_user(member(ALICE, FIRM_A))
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
            case_store=MemoryCaseStore(),
            firm_store=firms,
            access_log=MemoryAccessLog(),
            debtor_store=MemoryDebtorStore(),
            case_entity_store=MemoryCaseEntityStore(),
        )
    )
    return app.test_client()


def open_case(client, subject=ALICE):
    response = client.post(
        "/v1/cases", json={"chapter": 7, "district": "NDFL"}, headers=auth(subject)
    )
    assert response.status_code == 201
    return response.get_json()["id"]


def add_creditor(client, case_id):
    response = client.post(
        f"/v1/cases/{case_id}/creditors",
        json={
            "name": "Example Bank",
            "address": {
                "line1": "PO Box 15168",
                "city": "Wilmington",
                "state": "DE",
                "postal_code": "19850",
            },
            "provenance": {
                "name": TYPED,
                "address.line1": TYPED,
                "address.city": TYPED,
                "address.state": TYPED,
                "address.postal_code": TYPED,
            },
        },
        headers=auth(ALICE),
    )
    assert response.status_code == 201
    return response.get_json()["id"]


def add_claim(client, case_id, creditor_id, claim_class, **amounts):
    """One claim of `claim_class`, with whichever amount field that class uses.

    The three classes carry their money in different fields — that is the
    schedules' own shape, not this test's: 106E/F Part 1 totals a priority
    claim's `priority_amount` + `nonpriority_amount` (a claim can be partly
    each), Part 2 totals a nonpriority claim's `amount`, and 106D's Column A
    totals a secured claim's `amount`. Passing the wrong one is how this test
    first "passed" a claim the totals then ignored.
    """
    body = {"creditor_id": creditor_id, "claim_class": claim_class, **amounts}
    response = client.post(
        f"/v1/cases/{case_id}/claims",
        json={**body, "provenance": dict.fromkeys(body, TYPED)},
        headers=auth(ALICE),
    )
    assert response.status_code == 201, response.get_json()
    return response.get_json()["id"]


def summary(client, case_id, subject=ALICE):
    return client.get(f"/v1/cases/{case_id}/summary", headers=auth(subject))


# ── Auth and ownership ──────────────────────────────────────────


def test_the_route_refuses_an_unauthenticated_caller(client):
    assert client.get("/v1/cases/any-id/summary").status_code == 401


def test_an_unknown_case_is_not_found(client):
    assert summary(client, "no-such-case").status_code == 404


def test_another_firms_summary_is_the_same_404_as_no_case(client):
    # The id-oracle rule: a caller must not be able to tell "does not exist"
    # from "not yours", or the endpoint becomes a way to enumerate cases.
    case_id = open_case(client, ALICE)
    assert summary(client, case_id, subject=BOB).status_code == 404


def test_the_static_segment_is_not_shadowed_by_the_collection_routes(client):
    # /v1/cases/<id>/<collection> would match "summary" as a collection name if
    # Werkzeug ranked them the other way; it does not, and this is what says so.
    case_id = open_case(client)
    response = summary(client, case_id)
    assert response.status_code == 200
    assert "totals" in response.get_json()


# ── The wire shape ──────────────────────────────────────────────


def test_an_empty_case_totals_zero_rather_than_omitting_the_figures(client):
    case_id = open_case(client)

    body = summary(client, case_id).get_json()

    assert body["totals"] == {
        "realEstate": "0",
        "personalProperty": "0",
        "assets": "0",
        "secured": "0",
        "priorityUnsecured": "0",
        "nonpriorityUnsecured": "0",
        "liabilities": "0",
    }


def test_money_is_a_string_never_a_json_number(client):
    # A Decimal serialised as a JSON number reaches the client as an IEEE
    # double, and these are amounts on a bankruptcy filing. Same reason
    # ClaimBody.amount is a str in the domain.
    case_id = open_case(client)
    creditor_id = add_creditor(client, case_id)
    add_claim(client, case_id, creditor_id, "nonpriority_unsecured", amount="8412.66")

    totals = summary(client, case_id).get_json()["totals"]

    assert totals["nonpriorityUnsecured"] == "8412.66"
    assert all(isinstance(value, str) for value in totals.values())


def test_a_claim_lands_in_the_column_its_kind_names(client):
    case_id = open_case(client)
    creditor_id = add_creditor(client, case_id)
    add_claim(client, case_id, creditor_id, "nonpriority_unsecured", amount="100.00")

    totals = summary(client, case_id).get_json()["totals"]

    assert totals["nonpriorityUnsecured"] == "100.00"
    assert totals["secured"] == "0"
    assert totals["priorityUnsecured"] == "0"


def test_a_secured_claim_reaches_the_secured_column(client):
    # 106D Column A. Kept separate from the unsecured pair because a secured
    # claim's money is in `amount` while a priority one's is in
    # `priority_amount`, and confusing the two silently totals zero.
    case_id = open_case(client)
    creditor_id = add_creditor(client, case_id)
    add_claim(client, case_id, creditor_id, "secured", amount="14500.00")

    totals = summary(client, case_id).get_json()["totals"]

    assert totals["secured"] == "14500.00"
    assert totals["liabilities"] == "14500.00"
    assert totals["nonpriorityUnsecured"] == "0"


def test_liabilities_is_the_sum_of_the_three_it_reports(client):
    # The subtotals are kept alongside the sum so a reader can check it, which
    # only means anything if the sum is actually of those three.
    case_id = open_case(client)
    creditor_id = add_creditor(client, case_id)
    add_claim(client, case_id, creditor_id, "nonpriority_unsecured", amount="100.00")
    add_claim(
        client, case_id, creditor_id, "priority_unsecured", priority_amount="50.00"
    )

    totals = summary(client, case_id).get_json()["totals"]

    assert totals["priorityUnsecured"] == "50.00"
    assert totals["liabilities"] == "150.00"


# ── Readiness ───────────────────────────────────────────────────


def test_a_bare_case_is_not_ready_and_says_why(client):
    case_id = open_case(client)

    body = summary(client, case_id).get_json()

    assert body["readyToFile"] is False
    # A bare case has no Debtor 1, which is the gate's first structural refusal.
    assert any(problem["source"] == "debtors" for problem in body["problems"])


def test_every_problem_names_where_the_fix_belongs(client):
    # The point of `source` is that the client can send someone to the screen
    # that fixes it. A problem without one is a dead end.
    case_id = open_case(client)

    problems = summary(client, case_id).get_json()["problems"]

    assert problems
    assert all(problem["source"] for problem in problems)
    assert all(problem["message"] for problem in problems)


def test_a_problem_names_the_record_when_one_record_owns_the_fix(client):
    # `itemId` is absent, never null, and present only where a single record is
    # the thing to go and edit — the optional-key rule problem_json states.
    case_id = open_case(client)

    problems = summary(client, case_id).get_json()["problems"]

    for problem in problems:
        assert "itemId" not in problem or isinstance(problem["itemId"], str)
