"""`/v1/cases/{id}/plan-calculation` (issue 16.2 / #366) — the Chapter 13
plan calculator over HTTP.

The arithmetic is test_chapter13_plan.py's. This file pins what only the
route can get wrong: auth and reachability, that the static segment is not
shadowed by the generic collection routes, that the stored `plans` record
is what GET calculates, that the commitment period is the means-test
route's own figure (reused, not recomputed), that scenarios are parsed by
the record's parser and write nothing — and the issue's "done when" on the
reference case filed under Chapter 13: a feasible plan whose unsecured
percentage clears the liquidation floor.

Every identifier below is obviously fake; this repo is public.
"""

from __future__ import annotations

import pytest
from insolvia_core.case_entities import create_entity, parse_entity
from insolvia_core.plans import PLAN

from tests.unit.test_means_test_route import (
    ALICE,
    BOB,
    TYPED,
    Harness,
    auth,
    open_case,
)
from tests.unit.test_packet_assembly import reference_case_data

# A plan for the reference case: $500 a month over the commitment period,
# the mortgage kept current outside the plan, the car paid in full at 7%.
REFERENCE_PLAN: dict[str, object] = {
    "payment_source": "fixed",
    "monthly_payment": "500.00",
    "trustee_percentage": "10",
    "attorney_fees": "3500.00",
    "secured_treatments": [
        {
            "id": "t-home",
            "claim_id": "claim-mortgage",
            "treatment": "cure_and_maintain",
        },
        {
            "id": "t-car",
            "claim_id": "claim-auto",
            "treatment": "cramdown",
            "interest_rate": "7",
        },
    ],
    "unsecured_treatment": "pot",
}

PROVENANCE = {
    "payment_source": TYPED,
    "monthly_payment": TYPED,
    "trustee_percentage": TYPED,
    "attorney_fees": TYPED,
    "secured_treatments[t-home].claim_id": TYPED,
    "secured_treatments[t-home].treatment": TYPED,
    "secured_treatments[t-car].claim_id": TYPED,
    "secured_treatments[t-car].treatment": TYPED,
    "secured_treatments[t-car].interest_rate": TYPED,
    "unsecured_treatment": TYPED,
}


def seed_chapter_13(harness: Harness) -> str:
    """The reference case under Chapter 13, with the records the plan reads
    beyond the means test's: exemptions (Schedule C), the income summaries
    and expenses (Schedules I and J), and the SOFA (the domicile check)."""
    case_id = harness.seed_reference_case(chapter=13)
    data = reference_case_data()
    for name in ("exemptions", "income_summaries", "expenses", "sofa_entries"):
        for entity in getattr(data, name):
            harness.entity_store.create(entity)
    return case_id


def calculation(harness: Harness, case_id: str, subject: str = ALICE):
    return harness.client.get(
        f"/v1/cases/{case_id}/plan-calculation", headers=auth(subject)
    )


def scenarios(harness: Harness, case_id: str, body: object, subject: str = ALICE):
    return harness.client.post(
        f"/v1/cases/{case_id}/plan-calculation", json=body, headers=auth(subject)
    )


@pytest.fixture
def harness() -> Harness:
    return Harness()


# ── auth and reachability ─────────────────────────────────────────


def test_the_route_refuses_an_unauthenticated_caller(harness):
    response = harness.client.get("/v1/cases/any-id/plan-calculation")
    assert response.status_code == 401


def test_another_firms_case_is_the_same_404_as_no_case(harness):
    case_id = seed_chapter_13(harness)

    assert calculation(harness, case_id, subject=BOB).status_code == 404
    assert calculation(harness, "no-such-case").status_code == 404


def test_a_scenario_post_on_an_unreachable_case_is_404_not_400(harness):
    case_id = seed_chapter_13(harness)

    response = scenarios(harness, case_id, {"nonsense": True}, subject=BOB)

    assert response.status_code == 404


def test_the_static_segment_is_not_shadowed_by_the_collection_routes(harness):
    case_id = open_case(harness.client)

    response = calculation(harness, case_id)

    assert response.status_code == 200
    assert "liquidation" in response.get_json()


# ── the stored plan ───────────────────────────────────────────────


def test_the_plan_record_is_written_through_the_generic_collection(harness):
    case_id = seed_chapter_13(harness)

    response = harness.client.post(
        f"/v1/cases/{case_id}/plans",
        json={**REFERENCE_PLAN, "provenance": PROVENANCE},
        headers=auth(ALICE),
    )

    assert response.status_code == 201
    assert calculation(harness, case_id).get_json()["planPresent"] is True


def test_a_plan_value_without_provenance_is_refused(harness):
    case_id = seed_chapter_13(harness)

    response = harness.client.post(
        f"/v1/cases/{case_id}/plans", json=REFERENCE_PLAN, headers=auth(ALICE)
    )

    assert response.status_code == 400


def test_without_a_plan_the_liquidation_floor_is_still_reported(harness):
    case_id = seed_chapter_13(harness)

    body = calculation(harness, case_id).get_json()

    assert body["planPresent"] is False
    assert body["liquidation"]["percentage"] == "39.33"
    assert body["feasibility"]["feasible"] is None
    assert "There is no plan yet: choose a monthly payment." in body["problems"]


def test_the_commitment_period_is_the_means_tests_own(harness):
    case_id = seed_chapter_13(harness)
    means_test = harness.client.get(
        f"/v1/cases/{case_id}/means-test", headers=auth(ALICE)
    ).get_json()

    body = calculation(harness, case_id).get_json()

    assert body["commitmentPeriod"] == {
        "months": means_test["chapter13"]["commitmentPeriodMonths"],
        "source": means_test["chapter13"]["commitmentSource"],
    }


def test_the_reference_case_has_a_feasible_plan_that_clears_the_floor(harness):
    """The issue's "done when", against the reference case."""
    case_id = seed_chapter_13(harness)
    draft = parse_entity(PLAN, {**REFERENCE_PLAN, "provenance": PROVENANCE})
    harness.entity_store.create(create_entity(PLAN, draft, case_id=case_id))

    body = calculation(harness, case_id).get_json()

    assert body["problems"] == []
    assert body["funding"]["termMonths"] == 60
    assert body["feasibility"]["feasible"] is True
    assert body["bestInterests"]["passes"] is True
    assert float(body["unsecured"]["percentage"]) > float(
        body["liquidation"]["percentage"]
    )
    # Every payee row names where its figure came from.
    for summary in body["classes"]:
        for row in summary["rows"]:
            assert row["source"]


def test_two_plan_records_are_read_first_one_first_and_said_so(harness):
    case_id = seed_chapter_13(harness)
    for payment in ("500.00", "900.00"):
        draft = parse_entity(
            PLAN,
            {**REFERENCE_PLAN, "monthly_payment": payment, "provenance": PROVENANCE},
        )
        harness.entity_store.create(create_entity(PLAN, draft, case_id=case_id))

    body = calculation(harness, case_id).get_json()

    assert body["warnings"][0].startswith("This case has 2 plan records")


# ── scenarios ─────────────────────────────────────────────────────


def test_scenarios_are_calculated_side_by_side_and_write_nothing(harness):
    case_id = seed_chapter_13(harness)

    response = scenarios(
        harness,
        case_id,
        {
            "scenarios": [
                {"label": "$500 a month", "plan": REFERENCE_PLAN},
                {"label": "$300", "plan": {**REFERENCE_PLAN, "monthly_payment": "300"}},
            ]
        },
    )

    assert response.status_code == 200
    first, second = response.get_json()["scenarios"]
    assert first["label"] == "$500 a month"
    assert first["calculation"]["feasibility"]["feasible"] is True
    assert second["calculation"]["bestInterests"]["passes"] is False
    assert (
        harness.client.get(
            f"/v1/cases/{case_id}/plans", headers=auth(ALICE)
        ).get_json()["plans"]
        == []
    )


def test_a_malformed_scenario_names_its_path(harness):
    case_id = seed_chapter_13(harness)

    response = scenarios(
        harness, case_id, {"scenarios": [{"plan": {"term_months": 84}}]}
    )

    assert response.status_code == 400
    assert "scenarios[0].plan.term_months" in response.get_json()["fields"]


@pytest.mark.parametrize(
    "body",
    [{}, {"scenarios": []}, {"scenarios": "one"}, {"scenarios": [{}] * 6}],
)
def test_the_scenario_list_is_required_and_bounded(harness, body):
    case_id = seed_chapter_13(harness)

    assert scenarios(harness, case_id, body).status_code == 400
