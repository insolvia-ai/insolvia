"""The seeded fixture cases answer what their fixture version was built to
prove (seeds/fixtures/v3, the milestone 16 staging proof).

The unit tier proves the means test, the plan calculator and the creditor
matrix on in-memory reference data. What only a seeded environment can
answer is whether the SAME case, written by the seed loader into real
tables — its cross references resolved to ids that exist nowhere but this
target — still reads back as a Chapter 13 case with a feasible plan, and
whether every fixture creditor has an address the matrix can print. A loader
bug that dropped a `$ref` would pass every unit test and fail here.

READ-ONLY. Every route below computes and writes nothing, so the fixture
case is left as the seed left it. Packet assembly is deliberately absent:
each run would store another multi-megabyte packet on a case nobody prunes
(re-assembly creates, never replaces), and the job needs a worker the dev
tier does not start. The PR that introduced v3 records the assembly by hand.

Fixture cases are found, not addressed: their ids are derived per target
(seeds/README.md), so a case is the one whose chapter, court, division,
first debtor's name and creditor list match its spec. That is also what
tells a v3 case from the v2 case an environment still holds beside it.
"""

from __future__ import annotations

import json
from typing import Any

import pytest

from tests import paths
from tests.integration.conftest import Api


def _fixture_cases(fixture: dict[str, Any]) -> list[dict[str, Any]]:
    """The case specs `seeds/<target>.json` names, from their versions."""
    specs: list[dict[str, Any]] = []
    for entry in fixture.get("cases") or []:
        version = str(entry.get("fixture"))
        folder = paths.REPO_ROOT / "seeds" / "fixtures" / version
        cases = json.loads((folder / "cases.json").read_text())
        for spec in cases.get("cases") or []:
            if spec.get("handle") == entry.get("case"):
                specs.append(spec)
    return specs


def _creditor_keys(items: list[dict[str, Any]]) -> list[tuple[str, str]]:
    return sorted(
        (str(item.get("name")), str((item.get("address") or {}).get("postal_code")))
        for item in items
    )


def _find(admin: Api, spec: dict[str, Any]) -> str | None:
    name = (spec.get("debtors") or {}).get("debtor_1", {}).get("name")
    creditors = _creditor_keys((spec.get("collections") or {}).get("creditors") or [])
    cursor: str | None = None
    while True:
        params: dict[str, str] = {"limit": "50"}
        if cursor:
            params["cursor"] = cursor
        page = admin.get("/v1/cases", **params)
        for case in page.get("cases") or []:
            if (case.get("chapter"), case.get("court"), case.get("division")) != (
                spec.get("chapter"),
                spec.get("court"),
                spec.get("division"),
            ):
                continue
            debtors = admin.get(f"/v1/cases/{case['id']}/debtors").get("debtors")
            first = next(
                (d for d in debtors or [] if d.get("filing_role") == "debtor_1"), {}
            )
            if first.get("name") != name:
                continue
            listed = admin.get(f"/v1/cases/{case['id']}/creditors")
            if _creditor_keys(listed.get("creditors") or []) == creditors:
                return str(case["id"])
        cursor = page.get("nextCursor")
        if not cursor:
            return None


@pytest.fixture(scope="module")
def fixture_cases(fixture: dict[str, Any], admin: Api) -> dict[str, tuple[str, dict]]:
    """handle -> (this target's case id, the spec it was seeded from)."""
    found: dict[str, tuple[str, dict]] = {}
    for spec in _fixture_cases(fixture):
        case_id = _find(admin, spec)
        if case_id is None:
            pytest.fail(
                f"fixture case '{spec.get('handle')}' is not in this target — "
                "seed it (scripts/dev-aws-seed.sh, or the seed-staging action)"
            )
        found[str(spec["handle"])] = (case_id, spec)
    return found


def _chapter_13(fixture_cases: dict[str, tuple[str, dict]]) -> str:
    for case_id, spec in fixture_cases.values():
        if spec.get("chapter") == 13:
            return case_id
    pytest.skip("this target's fixture seeds no Chapter 13 case")


def test_every_fixture_creditor_prints_on_the_matrix(
    admin: Api, fixture_cases: dict[str, tuple[str, dict]]
) -> None:
    for handle, (case_id, spec) in fixture_cases.items():
        creditors = (spec.get("collections") or {}).get("creditors") or []
        matrix = admin.get(f"/v1/cases/{case_id}/creditor-matrix")
        assert matrix["problems"] == [], handle
        assert matrix["creditorCount"] == len(creditors), handle


def test_the_chapter_13_case_is_above_median_over_five_years(
    admin: Api, fixture_cases: dict[str, tuple[str, dict]]
) -> None:
    case_id = _chapter_13(fixture_cases)

    trace = admin.get(f"/v1/cases/{case_id}/means-test")

    assert trace["chapter"] == 13
    assert trace["problems"] == []
    assert trace["outcome"] == "above_median"
    assert trace["chapter13"]["commitmentPeriodMonths"] == 60
    # Above the median, B122C-2 files — the form the packet adds.
    assert trace["chapter13"]["disposableIncomeRequired"] is True


def test_the_chapter_13_plan_is_feasible_and_passes_1325a4(
    admin: Api, fixture_cases: dict[str, tuple[str, dict]]
) -> None:
    case_id = _chapter_13(fixture_cases)

    plan = admin.get(f"/v1/cases/{case_id}/plan-calculation")

    assert plan["planPresent"] is True
    assert plan["problems"] == []
    assert plan["feasibility"]["feasible"] is True
    assert plan["bestInterests"]["passes"] is True
    assert plan["commitmentPeriod"]["months"] == 60
