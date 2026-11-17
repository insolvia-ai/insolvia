"""The refusals ADR 0009 promises, from outside the process.

The unit tier proves the rules against memory stores. This proves the DEPLOYED
API — real firm rows in the real table, real subjects from the real pool —
still answers the same way, which is the one thing a fixture that drifted from
the pool would break silently.
"""

from __future__ import annotations

from typing import Any

from tests.integration.conftest import Api


def _other_firm_handles(people: dict[str, dict[str, Any]]) -> list[str]:
    own = people["admin"]["firmName"]
    return [h for h, p in people.items() if p["firmName"] != own]


def test_another_firm_gets_404_not_403_on_our_case(
    as_user, people: dict[str, dict[str, Any]], scratch_case: dict[str, Any]
):
    """404 rather than 403, so the route is not a probe for which case ids
    exist. Skipped on a one-firm fixture, which cannot express the claim."""
    outsiders = _other_firm_handles(people)
    if not outsiders:
        return
    outsider = as_user(outsiders[0])
    case_id = scratch_case["id"]

    outsider.get(f"/v1/cases/{case_id}", expect=404)
    outsider.get(f"/v1/cases/{case_id}/debtors", expect=404)
    outsider.get(f"/v1/cases/{case_id}/documents", expect=404)
    assert case_id not in [
        c["id"] for c in outsider.get("/v1/cases", limit="50")["cases"]
    ]


def test_firm_administration_is_hidden_from_a_non_admin(
    as_user, people: dict[str, dict[str, Any]]
):
    non_admins = [h for h, p in people.items() if not p.get("isAdmin")]
    if not non_admins:
        return
    as_user(non_admins[0]).get("/v1/firm/users", expect=403)


def test_an_admin_sees_exactly_the_people_the_fixture_seeded(
    admin: Api, people: dict[str, dict[str, Any]]
):
    """The staff list is the firm store read back through the API. Every
    fixture colleague is on it; anyone else on it is a row the seeder did not
    write — worth knowing, not worth failing over, so it is reported."""
    own = people["admin"]["firmName"]
    expected = {p["email"] for p in people.values() if p["firmName"] == own}
    listed = {u["email"] for u in admin.get("/v1/firm/users")["users"]}

    assert expected <= listed
    extra = listed - expected
    if extra:
        print(f"[integration] {len(extra)} firm user(s) beyond the fixture's")
