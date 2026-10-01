"""The case lifecycle through the real tables (issue 14.3 / #355).

What only this tier can answer: that a status move's TransactWriteItems — the
case record conditioned on the status it was read at, plus its history row —
is accepted by the real table under the running role; that the archive is a
second view the real indexes serve (the fill-the-page loop over a filtered
GSI query); and that copy case's three kinds of write — the collection
records, the sealed tax ids, the case transaction — land, and that soft
delete takes the copy out of every read.

Scratch discipline, and the one exception to it. The docket facts and the
archive are exercised on the scratch case and put back as they were. The
copy cannot be: copying is opening a case, so EACH RUN LEAVES ONE
SOFT-DELETED CASE PARTITION behind (the copy, deleted at the end — nothing
the product can reach, and nothing the scratch-case lookup can find). That is
the cost of proving the copy against the real tables, and it is said here so
nobody mistakes the rows for a leak. The status moves happen on the copy,
never on the scratch case: a filed case cannot go back, and the scratch case
must stay where every other spec expects it.
"""

from __future__ import annotations

from typing import Any

from tests.integration.conftest import Api


def test_docket_facts_are_recorded_and_cleared_on_the_scratch_case(
    admin: Api, scratch_case: dict[str, Any]
):
    case_id = scratch_case["id"]

    recorded = admin.patch(
        f"/v1/cases/{case_id}",
        {"judge": "Hon. Integration Judge", "office_file_number": "INT-0001"},
    )
    cleared = admin.patch(
        f"/v1/cases/{case_id}", {"judge": None, "office_file_number": None}
    )

    assert recorded["judge"] == "Hon. Integration Judge"
    assert recorded["officeFileNumber"] == "INT-0001"
    assert "judge" not in cleared
    assert "officeFileNumber" not in cleared


def test_an_archived_case_leaves_the_working_list_and_reads_in_the_archive(
    admin: Api, scratch_case: dict[str, Any]
):
    case_id = scratch_case["id"]
    url = f"/v1/cases/{case_id}/archived"
    try:
        archived = admin.put(url, {"archived": True}, expect=200)
        working = _all_ids(admin)
        archive = _all_ids(admin, archived="true")
    finally:
        # Back where every other spec expects it, whatever was asserted.
        admin.put(url, {"archived": False}, expect=200)

    assert "archivedAt" in archived
    assert case_id not in working
    assert case_id in archive
    assert case_id in _all_ids(admin)


def test_a_copy_moves_through_the_funnel_names_its_source_and_deletes(
    admin: Api, scratch_case: dict[str, Any]
):
    source_id = scratch_case["id"]
    copy = admin.post(f"/v1/cases/{source_id}/copy", {}, expect=201)
    copy_id = copy["id"]
    try:
        assert copy["status"] == "intake"
        debtors = admin.get(f"/v1/cases/{copy_id}/debtors")["debtors"]
        debtor_1 = next(d for d in debtors if d["filing_role"] == "debtor_1")
        assert {
            entry.get("copied_from_case_id")
            for entry in debtor_1["provenance"].values()
        } == {source_id}

        moved = admin.patch(f"/v1/cases/{copy_id}", {"status": "ready_to_file"})
        assert moved["status"] == "ready_to_file"
        # Filing without its docket facts is refused before anything lands.
        refused = admin.patch(f"/v1/cases/{copy_id}", {"status": "filed"}, expect=400)
        assert set(refused["fields"]) == {"filed_at", "case_number"}
        history = admin.get(f"/v1/cases/{copy_id}/status-history")["history"]
        assert [(h["fromStatus"], h["toStatus"]) for h in history] == [
            ("intake", "ready_to_file")
        ]
    finally:
        admin.delete(f"/v1/cases/{copy_id}", expect=204)

    admin.get(f"/v1/cases/{copy_id}", expect=404)
    assert copy_id not in _all_ids(admin)
    assert copy_id not in _all_ids(admin, archived="true")


def _all_ids(admin: Api, **params: str) -> set[str]:
    ids: set[str] = set()
    cursor: str | None = None
    while True:
        query = {"limit": "50", **params}
        if cursor:
            query["cursor"] = cursor
        page = admin.get("/v1/cases", **query)
        ids.update(case["id"] for case in page.get("cases") or [])
        cursor = page.get("nextCursor")
        if not cursor:
            return ids
