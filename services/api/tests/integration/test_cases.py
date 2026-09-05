"""A case written through the real API lands in the real table and comes back.

Everything below works inside the suite's one scratch case (conftest.py's
scratch discipline) — nothing here opens a case of its own.
"""

from __future__ import annotations

from typing import Any

from tests.integration.conftest import SCRATCH_DISTRICT, Api


def test_the_scratch_case_is_listed_for_its_firm(
    admin: Api, scratch_case: dict[str, Any]
):
    listed = admin.get("/v1/cases", limit="50")

    ids = [case["id"] for case in listed["cases"]]
    assert scratch_case["id"] in ids
    assert scratch_case["chapter"] == 7
    assert scratch_case["district"] == SCRATCH_DISTRICT
    assert scratch_case["status"] == "intake"


def test_an_update_is_read_back_from_the_store_not_the_response(
    admin: Api, scratch_case: dict[str, Any]
):
    """PATCH then GET, and the GET is the assertion: it proves the item DynamoDB
    holds is the one the API said it wrote."""
    case_id = scratch_case["id"]
    admin.patch(f"/v1/cases/{case_id}", {"chapter": 13})
    try:
        fetched = admin.get(f"/v1/cases/{case_id}")
        assert fetched["chapter"] == 13
        assert fetched["district"] == SCRATCH_DISTRICT
        assert fetched["updatedAt"] >= scratch_case["updatedAt"]
    finally:
        # Back to how the fixture finds it, however the assertions went.
        admin.patch(f"/v1/cases/{case_id}", {"chapter": 7})


def test_a_colleague_reaches_the_case_only_while_linked_to_it(
    admin: Api, as_user, people: dict[str, dict[str, Any]], scratch_case: dict[str, Any]
):
    """ADR 0009's per-case linking, end to end: a paralegal without
    access_all_cases sees a matter when assigned and not otherwise. Both halves
    are asserted, because either alone passes for the wrong reason."""
    linked = [
        handle
        for handle, person in people.items()
        if person["firmName"] == people["admin"]["firmName"]
        and not person.get("accessAllCases")
        and handle != "admin"
    ]
    if not linked:
        # The dev fixture has one seat; only staging can exercise this.
        return
    handle = linked[0]
    colleague = as_user(handle)
    subject = colleague.get("/v1/me")["subject"]
    case_id = scratch_case["id"]

    admin.delete(f"/v1/cases/{case_id}/assignees/{subject}", expect=(204, 404))
    colleague.get(f"/v1/cases/{case_id}", expect=404)

    admin.put(f"/v1/cases/{case_id}/assignees/{subject}", expect=204)
    try:
        assert colleague.get(f"/v1/cases/{case_id}")["id"] == case_id
        listed = [
            case["id"] for case in colleague.get("/v1/cases", limit="50")["cases"]
        ]
        assert case_id in listed
    finally:
        admin.delete(f"/v1/cases/{case_id}/assignees/{subject}", expect=(204, 404))
