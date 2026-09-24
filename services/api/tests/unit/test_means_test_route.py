"""`GET /v1/cases/{id}/means-test` (issue #349) — the § 707(b) trace on
demand.

Same auth/ownership shape as `/summary` (test_case_summary_route.py). This
file's own job is the one claim the issue makes: the endpoint's result IS
what the packet's B122A-1 / B122A-2 print. So the reference case (the same
file the projection goldens pin) is stored into the memory adapters, the
route reads it back over HTTP, and its figures are asserted equal to the
projections' — the engine tests own the arithmetic; this pins that the
route calls the same code path.

Every identifier below is obviously fake; this repo is public.
"""

from __future__ import annotations

import time
from dataclasses import replace

import jwt
import pytest
from cryptography.hazmat.primitives.asymmetric import rsa
from insolvia_api.adapters.memory.mailer_client import InMemoryMailerClient
from insolvia_api.adapters.memory.waitlist_store import MemoryWaitlistStore
from insolvia_api.api.app_factory import create_app
from insolvia_api.api.dependencies import ApiDependencies
from insolvia_api.core.config import load_config
from insolvia_api.core.form_fill import Option, Text
from insolvia_api.core.form_projections import project
from insolvia_api.core.form_templates import latest_form
from insolvia_api.core.packet_assembly import read_case_data, to_case_file
from insolvia_core.adapters.memory.access_log import MemoryAccessLog
from insolvia_core.adapters.memory.case_entity_store import MemoryCaseEntityStore
from insolvia_core.adapters.memory.case_store import MemoryCaseStore
from insolvia_core.adapters.memory.debtor_store import MemoryDebtorStore
from insolvia_core.adapters.memory.firm_store import MemoryFirmStore
from insolvia_core.adapters.memory.jwks_provider import StaticJwksProvider
from insolvia_core.adapters.memory.tax_id_cipher import LocalTaxIdCipher
from insolvia_core.adapters.memory.tax_id_store import MemoryTaxIdStore
from insolvia_core.cases import assign_case
from insolvia_core.firms import Firm, FirmUser, default_permissions
from insolvia_core.means_test_inputs import MEANS_TEST_INPUT, parse_means_test_input

from tests.unit.test_packet_assembly import reference_case_data

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


class Harness:
    """The app plus the stores it was composed over, so a test can seed the
    reference case directly (the way packet assembly's tests do) and then
    read it back over HTTP."""

    def __init__(self) -> None:
        firms = MemoryFirmStore()
        firms.create_firm(firm(FIRM_A, "Example & Partners"))
        firms.create_firm(firm(FIRM_B, "Other Firm LLP"))
        firms.add_user(member(ALICE, FIRM_A))
        firms.add_user(member(BOB, FIRM_B))
        self.case_store = MemoryCaseStore()
        self.debtor_store = MemoryDebtorStore()
        self.entity_store = MemoryCaseEntityStore()
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
                case_store=self.case_store,
                firm_store=firms,
                access_log=MemoryAccessLog(),
                debtor_store=self.debtor_store,
                tax_id_store=MemoryTaxIdStore(),
                tax_id_cipher=LocalTaxIdCipher(),
                case_entity_store=self.entity_store,
            )
        )
        self.client = app.test_client()

    def seed_reference_case(self) -> str:
        """The reference case, owned by Alice's firm, in the stores."""
        data = reference_case_data()
        case = replace(data.case, firm_id=FIRM_A, created_by=ALICE)
        self.case_store.create(
            case, assign_case(case, subject=ALICE, assigned_by=ALICE)
        )
        for debtor in data.debtors:
            self.debtor_store.create(debtor)
        for field_name in (
            "petitions",
            "employments",
            "pay_period_records",
            "other_income_records",
            "means_test_inputs",
            "assets",
            "creditors",
            "claims",
            "households",
            "dependents",
        ):
            for entity in getattr(data, field_name):
                self.entity_store.create(entity)
        return case.id

    def case_file(self, case_id: str):
        case = self.case_store.read_for_worker(case_id)
        assert case is not None
        return to_case_file(
            read_case_data(
                case, debtor_store=self.debtor_store, entity_store=self.entity_store
            )
        )

    def means_test(self, case_id: str, subject: str = ALICE):
        return self.client.get(f"/v1/cases/{case_id}/means-test", headers=auth(subject))


@pytest.fixture
def harness() -> Harness:
    return Harness()


def open_case(client, subject=ALICE):
    response = client.post(
        "/v1/cases",
        json={"chapter": 7, "court": "flmb", "division": "tampa"},
        headers=auth(subject),
    )
    assert response.status_code == 201
    return response.get_json()["id"]


def money(printed: Text) -> str:
    """A projected money box back to the wire's plain two-place string."""
    return printed.value.replace(",", "")


# ── Auth and ownership ──────────────────────────────────────────


def test_the_route_refuses_an_unauthenticated_caller(harness):
    assert harness.client.get("/v1/cases/any-id/means-test").status_code == 401


def test_an_unknown_case_is_not_found(harness):
    assert harness.means_test("no-such-case").status_code == 404


def test_another_firms_means_test_is_the_same_404_as_no_case(harness):
    case_id = harness.seed_reference_case()
    assert harness.means_test(case_id, subject=BOB).status_code == 404


def test_the_static_segment_is_not_shadowed_by_the_collection_routes(harness):
    case_id = open_case(harness.client)
    response = harness.means_test(case_id)
    assert response.status_code == 200
    assert "cmi" in response.get_json()


# ── Too early to answer ──────────────────────────────────────────


def test_a_bare_case_reports_why_rather_than_failing(harness):
    case_id = open_case(harness.client)

    body = harness.means_test(case_id).get_json()

    assert body["outcome"] == "undetermined"
    assert body["determinedBy"] is None
    assert body["comparison"] is None
    assert body["lines"] == []
    assert any("household composition" in problem for problem in body["problems"])
    # The window and the (empty) derivation still land: the screen shows
    # the six months the records will be read over before any exist.
    assert body["cmi"]["columns"] == []
    assert len(body["cmi"]["window"]["months"]) == 6
    assert body["asOfSource"] == "case.created_at"
    assert body["household"]["medianHouseholdSize"] == {"value": None, "source": None}


# ── The reference case: the endpoint equals the forms ───────────


def test_the_trace_equals_what_b122a1_prints(harness):
    case_id = harness.seed_reference_case()
    values = project(latest_form("form/b122a1"), harness.case_file(case_id))

    body = harness.means_test(case_id).get_json()

    assert body["asOf"] == "2026-08-01"
    cmi = body["cmi"]
    assert cmi["combinedMonthlyTotal"] == money(values["total_cmi"])
    assert cmi["annualized"] == money(values["annualized_cmi"])
    columns = {column["column"]: column for column in cmi["columns"]}
    wages = next(line for line in columns["A"]["lines"] if line["category"] == "wages")
    assert wages["monthlyAverage"] == money(values["wages"]["Debto1.Quest2.0"])
    assert len(wages["entries"]) == 6
    unemployment = next(
        line for line in columns["B"]["lines"] if line["category"] == "unemployment"
    )
    assert unemployment["monthlyAverage"] == money(
        values["unemployment"]["Debto2.Quest8.0"]
    )
    # The § 101(10A)(B)(ii) exclusion is shown with its citation, and its
    # figure is the one line 8's contention box prints.
    [ssa] = columns["B"]["excluded"]
    assert ssa["category"] == "social_security_act_benefit"
    assert ssa["monthlyAverage"] == money(values["ssa_contention"]["Debto2.Quest8A"])
    assert "101(10A)(B)(ii)" in ssa["citation"]

    comparison = body["comparison"]
    assert comparison["state"] == values["median_state"].value
    assert str(comparison["householdSize"]) == values["median_household_size"].value
    assert comparison["annualMedian"] == money(values["median_income"])
    assert comparison["aboveMedian"] is True
    assert values["median_comparison"] == Option("12b more than 13")
    assert "ust/census-median-family-income@" in comparison["source"]


def test_the_trace_equals_what_b122a2_prints(harness):
    case_id = harness.seed_reference_case()
    values = project(latest_form("form/b122a2"), harness.case_file(case_id))

    body = harness.means_test(case_id).get_json()

    assert body["outcome"] == "no_presumption"
    assert body["determinedBy"] == "threshold_floor"
    assert values["presumption_thresholds"] == Option("1")
    lines = {line["line"]: line for line in body["lines"]}
    for number, field_id in (
        ("1", "total_cmi"),
        ("4", "adjusted_cmi"),
        ("6", "food_clothing_allowance"),
        ("8", "housing_operating"),
        ("9b", "home_debt_total"),
        ("13b", "vehicle_1_loan_total"),
        ("24", "irs_allowances_total"),
        ("32", "additional_deductions_total"),
        ("33e", "secured_debt_total"),
        ("34", "cure_monthly_total"),
        ("36", "ch13_admin_expense"),
        ("38", "total_deductions"),
        ("39c", "monthly_disposable_income"),
        ("39d", "disposable_income_60_months"),
    ):
        assert lines[number]["amount"] == money(values[field_id]), number
    assert lines["39d"]["amount"] == "-23359.80"
    # Every line names where it came from — a dataset release, an entered
    # field, a derived input, or line arithmetic.
    assert all(line["source"] for line in body["lines"])
    assert "ust/irs-national-standards@" in lines["6"]["source"]
    assert "means_test_input.taxes" in lines["16"]["source"]
    assert body["releaseIds"]["ust/irs-local-standards"].startswith(
        "ust/irs-local-standards@"
    )
    assert body["problems"] == []


def test_the_inputs_are_reported_as_the_engine_read_them(harness):
    case_id = harness.seed_reference_case()

    body = harness.means_test(case_id).get_json()

    assert body["maritalFilingStatus"] == {
        "value": "married_filing_jointly",
        "source": "the case's debtor_2 record",
    }
    household = body["household"]
    assert household["peopleUnder65"] == 3
    assert household["medianHouseholdSize"]["value"] == 3
    assert household["irsFamilySize"]["value"] == 3
    assert household["irsHousingFamilySize"]["value"] == 3
    assert household["childrenUnder18"] == 1
    assert body["exemptions"]["applied"] is None
    assert body["exemptions"]["available"] == [
        "non_consumer_debts",
        "disabled_veteran",
        "reservist_national_guard",
    ]
    assert body["debt"]["priorityTotal"] != "0.00"


def test_saving_an_exemption_changes_the_verdict(harness):
    case_id = harness.seed_reference_case()
    [existing] = harness.entity_store.list_for_case(case_id, MEANS_TEST_INPUT)
    payload = {"reservist_national_guard": True, "people_under_65": 3}
    response = harness.client.put(
        f"/v1/cases/{case_id}/means_test_inputs/{existing.id}",
        json={**payload, "provenance": dict.fromkeys(payload, TYPED)},
        headers=auth(ALICE),
    )
    assert response.status_code == 200, response.get_json()

    body = harness.means_test(case_id).get_json()

    assert body["outcome"] == "exempt"
    assert body["determinedBy"] == "reservist_national_guard"
    assert body["exemptions"]["applied"] == "reservist_national_guard"
    assert "707(b)(2)(D)(ii)" in body["exemptions"]["rule"]
    assert body["lines"] == []


def put_expected_filing_date(harness, case_id, expected):
    [petition] = harness.entity_store.list_for_case(
        case_id, reference_case_data().petitions[0].kind
    )
    payload = {"expected_filing_date": expected}
    response = harness.client.put(
        f"/v1/cases/{case_id}/petitions/{petition.id}",
        json={**payload, "provenance": dict.fromkeys(payload, TYPED)},
        headers=auth(ALICE),
    )
    assert response.status_code == 200, response.get_json()


def test_the_window_follows_the_petitions_expected_filing_date(harness):
    case_id = harness.seed_reference_case()
    put_expected_filing_date(harness, case_id, "2026-10-15")

    body = harness.means_test(case_id).get_json()

    assert body["asOf"] == "2026-10-15"
    assert body["asOfSource"] == "petition.expected_filing_date"
    assert body["cmi"]["window"]["months"] == [
        "2026-04",
        "2026-05",
        "2026-06",
        "2026-07",
        "2026-08",
        "2026-09",
    ]
    # Ada's February and March checks fall out of the window; the figure
    # moves with the planned date, as the form's would.
    assert body["cmi"]["combinedMonthlyTotal"] != "8700.00"


def test_a_planned_date_before_the_datasets_begin_is_a_problem_not_a_500(harness):
    case_id = harness.seed_reference_case()
    put_expected_filing_date(harness, case_id, "2026-04-15")

    response = harness.means_test(case_id)

    assert response.status_code == 200
    body = response.get_json()
    assert body["outcome"] == "undetermined"
    assert body["releaseIds"] == {}
    assert any("no release of" in problem for problem in body["problems"])
    # The window is a calendar fact and still lands.
    assert body["cmi"]["window"]["months"][0] == "2025-10"


def test_money_is_a_string_never_a_json_number(harness):
    case_id = harness.seed_reference_case()

    body = harness.means_test(case_id).get_json()

    assert isinstance(body["cmi"]["combinedMonthlyTotal"], str)
    assert isinstance(body["comparison"]["annualMedian"], str)
    assert all(isinstance(line["amount"], str) for line in body["lines"])


def test_the_sample_input_body_round_trips_through_the_collection_route(harness):
    # The screen writes the whole record through the generic collection
    # route; the new members must survive that trip unchanged.
    case_id = open_case(harness.client)
    payload = {
        "median_household_size": 4,
        "marital_filing_status": "married_not_filing_separated",
        "income_overrides": [
            {
                "id": "ov1",
                "column": "A",
                "category": "wages",
                "monthly_amount": "5000.00",
            }
        ],
        "other_secured_payments": [
            {
                "id": "sp1",
                "claim_id": "claim-1",
                "bucket": "vehicle_1",
                "monthly_payment": "400.00",
                "cure_total": "1200.00",
            }
        ],
    }
    provenance = {
        "median_household_size": TYPED,
        "marital_filing_status": TYPED,
        "income_overrides[ov1].id": TYPED,
        "income_overrides[ov1].column": TYPED,
        "income_overrides[ov1].category": TYPED,
        "income_overrides[ov1].monthly_amount": TYPED,
        "other_secured_payments[sp1].id": TYPED,
        "other_secured_payments[sp1].claim_id": TYPED,
        "other_secured_payments[sp1].bucket": TYPED,
        "other_secured_payments[sp1].monthly_payment": TYPED,
        "other_secured_payments[sp1].cure_total": TYPED,
    }
    response = harness.client.post(
        f"/v1/cases/{case_id}/means_test_inputs",
        json={**payload, "provenance": provenance},
        headers=auth(ALICE),
    )
    assert response.status_code == 201, response.get_json()
    stored = response.get_json()
    assert stored["income_overrides"] == payload["income_overrides"]
    assert stored["other_secured_payments"] == payload["other_secured_payments"]
    assert parse_means_test_input(payload).median_household_size == 4
