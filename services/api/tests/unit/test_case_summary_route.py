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


def add_asset(client, case_id, **fields):
    body = {"category": "vehicle", **fields}
    response = client.post(
        f"/v1/cases/{case_id}/assets",
        json={**body, "provenance": dict.fromkeys(body, TYPED)},
        headers=auth(ALICE),
    )
    assert response.status_code == 201, response.get_json()
    return response.get_json()["id"]


def add_exemption(client, case_id, **fields):
    body = {**fields}
    response = client.post(
        f"/v1/cases/{case_id}/exemptions",
        json={**body, "provenance": dict.fromkeys(body, TYPED)},
        headers=auth(ALICE),
    )
    assert response.status_code == 201, response.get_json()
    return response.get_json()["id"]


def summary(client, case_id, subject=ALICE):
    return client.get(f"/v1/cases/{case_id}/summary", headers=auth(subject))


def add_debtor_1(client, case_id):
    response = client.put(
        f"/v1/cases/{case_id}/debtors/debtor_1",
        json={"name": {"given": "Ada"}, "provenance": {"name.given": TYPED}},
        headers=auth(ALICE),
    )
    assert response.status_code == 201
    return response.get_json()["id"]


def add_income_summary(client, case_id, debtor_id, **amounts):
    body = {"debtor_id": debtor_id, **amounts}
    response = client.post(
        f"/v1/cases/{case_id}/income_summaries",
        json={**body, "provenance": dict.fromkeys(body, TYPED)},
        headers=auth(ALICE),
    )
    assert response.status_code == 201, response.get_json()
    return response.get_json()["id"]


def add_household(client, case_id, which_household="main"):
    body = {"which_household": which_household}
    response = client.post(
        f"/v1/cases/{case_id}/households",
        json={**body, "provenance": dict.fromkeys(body, TYPED)},
        headers=auth(ALICE),
    )
    assert response.status_code == 201, response.get_json()
    return response.get_json()["id"]


def add_expense(client, case_id, household_id, category, amount):
    body = {"household_id": household_id, "category": category, "amount": amount}
    response = client.post(
        f"/v1/cases/{case_id}/expenses",
        json={**body, "provenance": dict.fromkeys(body, TYPED)},
        headers=auth(ALICE),
    )
    assert response.status_code == 201, response.get_json()
    return response.get_json()["id"]


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
        "totalExempt": "0",
        "totalNonExempt": "0",
        "secured": "0",
        "priorityUnsecured": "0",
        "nonpriorityUnsecured": "0",
        "liabilities": "0",
        "monthlyIncome": "0",
        "monthlyExpenses": "0",
        "monthlyExcess": "0",
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


# ── Exempt / non-exempt totals (issue 13.3 / #344) ──────────────


def test_total_exempt_is_the_plain_sum_of_exemption_amounts(client):
    # NOT the exemption law's arithmetic (the homestead cap, the full-FMV
    # election) — issue #346 owns that. This is deliberately just a sum.
    case_id = open_case(client)
    asset_id = add_asset(
        client, case_id, value_entire="9000.00", value_portion_owned="9000.00"
    )
    add_exemption(client, case_id, asset_id=asset_id, amount="1000.00")
    add_exemption(client, case_id, asset_id=asset_id, amount="2500.00")

    totals = summary(client, case_id).get_json()["totals"]

    assert totals["totalExempt"] == "3500.00"


def test_an_election_with_no_typed_amount_contributes_nothing(client):
    # `claims_full_fmv` has no stored dollar figure to sum; the plain-sum
    # rule means it is 0 here, not an estimate against the asset's value.
    case_id = open_case(client)
    asset_id = add_asset(
        client, case_id, value_entire="265000.00", value_portion_owned="265000.00"
    )
    add_exemption(client, case_id, asset_id=asset_id, claims_full_fmv=True)

    totals = summary(client, case_id).get_json()["totals"]

    assert totals["totalExempt"] == "0"


def test_total_non_exempt_is_property_minus_exempt(client):
    case_id = open_case(client)
    asset_id = add_asset(
        client, case_id, value_entire="9000.00", value_portion_owned="9000.00"
    )
    add_exemption(client, case_id, asset_id=asset_id, amount="3500.00")

    totals = summary(client, case_id).get_json()["totals"]

    assert totals["assets"] == "9000.00"
    assert totals["totalExempt"] == "3500.00"
    assert totals["totalNonExempt"] == "5500.00"


def test_total_non_exempt_is_not_floored_at_zero(client):
    # An exemption claimed larger than the property is a fact worth a
    # negative figure showing, not hiding behind a floor.
    case_id = open_case(client)
    asset_id = add_asset(
        client, case_id, value_entire="1000.00", value_portion_owned="1000.00"
    )
    add_exemption(client, case_id, asset_id=asset_id, amount="1500.00")

    totals = summary(client, case_id).get_json()["totals"]

    assert totals["totalNonExempt"] == "-500.00"


# ── The lien figures (issue #345) ───────────────────────────────


def test_an_empty_case_reports_no_liens_rather_than_omitting_the_block(client):
    case_id = open_case(client)

    body = summary(client, case_id).get_json()

    assert body["liens"] == {"claims": [], "assets": []}


def test_linking_a_claim_to_an_asset_derives_both_figures_without_typing_either(
    client,
):
    # The issue's definition of done: the asset's secured total and the
    # claim's deficiency follow from the link, and neither was entered.
    case_id = open_case(client)
    creditor_id = add_creditor(client, case_id)
    asset_id = add_asset(client, case_id, value_entire="9000.00")
    claim_id = add_claim(
        client,
        case_id,
        creditor_id,
        "secured",
        amount="12000.00",
        asset_id=asset_id,
        lien_position=1,
    )

    liens = summary(client, case_id).get_json()["liens"]

    assert liens["assets"] == [
        {"assetId": asset_id, "claimIds": [claim_id], "securedTotal": "12000.00"}
    ]
    (row,) = liens["claims"]
    assert row["claimId"] == claim_id
    assert row["collateralValue"] == "9000.00"
    assert row["securedAmount"] == "9000.00"
    assert row["unsecuredAmount"] == "3000.00"
    assert row["unsecuredSource"] == "derived"
    # Column A is untouched by the link: the summary still owes the whole
    # claim, as B106D line 1 totals it.
    assert summary(client, case_id).get_json()["totals"]["secured"] == "12000.00"


def test_a_typed_override_is_reported_as_manual(client):
    case_id = open_case(client)
    creditor_id = add_creditor(client, case_id)
    claim_id = add_claim(
        client,
        case_id,
        creditor_id,
        "secured",
        amount="12000.00",
        unsecured_amount_override="4000.00",
    )

    (row,) = summary(client, case_id).get_json()["liens"]["claims"]

    assert row["claimId"] == claim_id
    assert row["unsecuredAmount"] == "4000.00"
    assert row["unsecuredSource"] == "manual"


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


# ── Monthly income, expenses, and the I-J excess (issue #348) ───


def test_an_empty_case_has_zero_income_expenses_and_excess(client):
    case_id = open_case(client)

    totals = summary(client, case_id).get_json()["totals"]

    assert totals["monthlyIncome"] == "0"
    assert totals["monthlyExpenses"] == "0"
    assert totals["monthlyExcess"] == "0"


def test_monthly_income_is_the_same_figure_106i_line_12_prints(client):
    # No new arithmetic here — this pins the DELEGATION, the way the asset and
    # liability tests above do; the arithmetic itself is
    # test_form_projections.py's job.
    case_id = open_case(client)
    debtor_id = add_debtor_1(client, case_id)
    add_income_summary(
        client, case_id, debtor_id, wages="3000.00", deduction_tax="500.00"
    )

    totals = summary(client, case_id).get_json()["totals"]

    assert totals["monthlyIncome"] == "2500.00"


def test_monthly_expenses_is_the_same_figure_106j_line_22c_prints(client):
    case_id = open_case(client)
    household_id = add_household(client, case_id)
    add_expense(client, case_id, household_id, "food_and_housekeeping", "600.00")
    add_expense(client, case_id, household_id, "transportation", "200.00")

    totals = summary(client, case_id).get_json()["totals"]

    assert totals["monthlyExpenses"] == "800.00"


def test_monthly_excess_is_income_minus_expenses_and_may_go_negative(client):
    case_id = open_case(client)
    debtor_id = add_debtor_1(client, case_id)
    add_income_summary(client, case_id, debtor_id, wages="1000.00")
    household_id = add_household(client, case_id)
    add_expense(client, case_id, household_id, "food_and_housekeeping", "1500.00")

    totals = summary(client, case_id).get_json()["totals"]

    assert totals["monthlyIncome"] == "1000.00"
    assert totals["monthlyExpenses"] == "1500.00"
    assert totals["monthlyExcess"] == "-500.00"


def test_monthly_figures_are_strings_never_json_numbers(client):
    case_id = open_case(client)
    debtor_id = add_debtor_1(client, case_id)
    add_income_summary(client, case_id, debtor_id, wages="1234.56")

    totals = summary(client, case_id).get_json()["totals"]

    assert isinstance(totals["monthlyIncome"], str)
    assert isinstance(totals["monthlyExcess"], str)
