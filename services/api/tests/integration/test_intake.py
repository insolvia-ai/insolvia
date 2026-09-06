"""The intake's one promise, through the real store: a debtor saved with
provenance is a debtor read back with it.

This is the seam `e2e/tests/intake-persists.spec.ts` closes through the
browser; here it is closed one layer down, without one, so a provenance
regression in the API is named by the API rather than by an autosave that
never says "Saved".
"""

from __future__ import annotations

import time
from typing import Any

from tests.integration.conftest import Api

TYPED = {"source": "staff_typed"}


def test_a_debtor_saved_with_provenance_is_read_back(
    admin: Api, scratch_case: dict[str, Any]
):
    case_id = scratch_case["id"]
    # Distinct per run so a stale value from a previous run cannot pass this.
    given = f"Probe{int(time.time()):x}"

    saved = admin.put(
        f"/v1/cases/{case_id}/debtors/debtor_1",
        {"name": {"given": given}, "provenance": {"name.given": TYPED}},
        expect=(200, 201),
    )
    assert saved["name"]["given"] == given
    assert saved["provenance"]["name.given"]["source"] == "staff_typed"

    listed = admin.get(f"/v1/cases/{case_id}/debtors")["debtors"]
    debtor = next(d for d in listed if d["filing_role"] == "debtor_1")
    assert debtor["name"]["given"] == given
    assert debtor["id"] == saved["id"]


def test_a_populated_field_without_provenance_is_refused(
    admin: Api, scratch_case: dict[str, Any]
):
    """Invariant 1 of the data model, enforced by the running service: this is
    the 400 the app's mock cannot produce and the one that once broke every
    autosave."""
    case_id = scratch_case["id"]
    admin.put(
        f"/v1/cases/{case_id}/debtors/debtor_1",
        {"name": {"given": "Nobody"}, "provenance": {}},
        expect=400,
    )
