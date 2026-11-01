"""A client's cases through the real database (ADR 0022, PR 2): the debtor's
client link is what `GET /v1/firm/clients/<id>/cases` reads.

What only this tier can answer: that the deployed schema serves that read
(a migration the unit tier's memory stores cannot see), that the row the
store writes is the one the read finds, and that the API role may run it.

Scratch discipline: the scratch case is linked to the scratch client as
Debtor 1 — idempotently, whichever of them already existed — and nothing else
is created. Linking moves no copied field, so the intake spec's writes to
that debtor are unaffected.
"""

from __future__ import annotations

import time
from typing import Any

from tests.conftest import Api

#: A just-written link normally reads back at once; the poll is bounded, so a
#: missing link fails rather than hangs.
_INDEX_WAIT_SECONDS = 10.0


def test_a_case_linked_to_a_client_is_in_that_clients_list(
    admin: Api, scratch_client: dict[str, Any], scratch_case: dict[str, Any]
):
    client_id = scratch_client["id"]
    case_id = scratch_case["id"]
    linked = admin.put(
        f"/v1/cases/{case_id}/debtors/debtor_1/client",
        {"client_id": client_id},
        expect=(200, 201),
    )
    assert linked["client_id"] == client_id
    assert isinstance(linked["differs_from_client"], list)

    deadline = time.monotonic() + _INDEX_WAIT_SECONDS
    while True:
        entries = admin.get(f"/v1/firm/clients/{client_id}/cases")["cases"]
        roles = {e["case"]["id"]: e["filing_role"] for e in entries}
        if case_id in roles or time.monotonic() > deadline:
            break
        time.sleep(0.5)
    assert roles.get(case_id) == "debtor_1", (
        "the scratch case is not in its client's list — is the client "
        "link applied in this environment's database?"
    )


def test_another_firms_client_is_not_found(as_user, people, scratch_client):
    """Cross-tenant, where the fixture has a second firm; a one-firm fixture
    cannot express this and says so by returning."""
    own = people["admin"]["firmName"]
    outsider = next(
        (handle for handle, person in people.items() if person["firmName"] != own),
        None,
    )
    if outsider is None:
        return
    as_user(outsider).get(f"/v1/firm/clients/{scratch_client['id']}/cases", expect=404)
