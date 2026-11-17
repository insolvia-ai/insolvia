"""The prospect funnel on the firm client (issue 14.3 / #355, the
maintainer's decision of 2026-10-01): a client is a prospect until retained,
`prospect_stage` is their funnel position, and the retained transition
stamps `first_retained_at` and clears the stage in one conditional write.

Route-level behaviour is services/api's test_client_funnel_routes.py; this
file pins the domain rule and both stores.
"""

from __future__ import annotations

from dataclasses import replace

import pytest
from insolvia_core.adapters.memory.firm_store import MemoryFirmStore
from insolvia_core.adapters.postgres.firm_store import PostgresFirmStore
from insolvia_core.errors import FieldValidationError
from insolvia_core.firm_clients import (
    PROSPECT_STAGES,
    FirmClient,
    create_firm_client,
    firm_client_from_item,
    firm_client_item,
    firm_client_json,
    parse_firm_client,
    parse_prospect_stage,
)
from insolvia_core.firms import Firm

FIRM_ID = "00000000-0000-4000-8000-00000000f18a"
ALICE = "00000000-0000-4000-8000-00000000a11c"


def a_client(**body: object) -> FirmClient:
    draft = parse_firm_client({"name": {"surname": "Example"}, **body})
    return create_firm_client(draft, firm_id=FIRM_ID, created_by=ALICE)


# ── The rule ───────────────────────────────────────────────────────


def test_a_client_is_a_prospect_until_retained():
    assert a_client().prospect
    assert not a_client(first_retained_at="2026-07-20").prospect


@pytest.mark.parametrize("stage", [*PROSPECT_STAGES, None])
def test_a_stage_or_null_parses(stage):
    assert parse_prospect_stage({"prospect_stage": stage}) == stage


@pytest.mark.parametrize(
    "payload", [{}, {"prospect_stage": "won"}, {"prospect_stage": 3}]
)
def test_an_unknown_or_missing_stage_is_refused(payload):
    with pytest.raises(FieldValidationError):
        parse_prospect_stage(payload)


def test_the_stage_round_trips_the_item_and_reaches_the_wire_only_for_a_prospect():
    prospect = replace(a_client(), prospect_stage="consultation_scheduled")
    assert firm_client_from_item(firm_client_item(prospect)) == prospect
    assert firm_client_json(prospect)["prospect_stage"] == "consultation_scheduled"
    retained = replace(prospect, first_retained_at="2026-09-01")
    assert "prospect_stage" not in firm_client_json(retained)


def test_the_whole_record_put_cannot_set_a_stage():
    # Server-owned: the draft has no such field, so a body naming one is
    # simply not read as one.
    draft = parse_firm_client({"name": {"surname": "X"}, "prospect_stage": "possible"})
    assert not hasattr(draft, "prospect_stage")


# ── Both stores (memory and Postgres) ───────────────────────────────────────────────


@pytest.fixture(params=["memory", "postgres"])
def memory(request):
    """Each FirmStore (the name is historical), holding the test firm."""
    if request.param == "memory":
        store = MemoryFirmStore()
    else:
        request.getfixturevalue("postgres")
        store = PostgresFirmStore(request.getfixturevalue("postgres_url"))
    store.create_firm(
        Firm(
            id=FIRM_ID,
            name="Example & Partners",
            status="active",
            created_at="2026-01-01T00:00:00.000Z",
            updated_at="2026-01-01T00:00:00.000Z",
        )
    )
    return store


def test_a_prospects_stage_is_set_and_cleared(memory):
    client = a_client()
    memory.create_client(client)

    staged = memory.set_client_prospect_stage(FIRM_ID, client.id, "possible")
    cleared = memory.set_client_prospect_stage(FIRM_ID, client.id, None)

    assert staged is not None
    assert staged.prospect_stage == "possible"
    assert cleared is not None
    assert cleared.prospect_stage is None


def test_a_retained_client_takes_no_stage(memory):
    client = a_client(first_retained_at="2026-07-20")
    memory.create_client(client)
    assert memory.set_client_prospect_stage(FIRM_ID, client.id, "possible") is None


def test_the_retained_transition_stamps_and_clears_the_stage(memory):
    client = replace(a_client(), prospect_stage="awaiting_signed_agreement")
    memory.create_client(client)

    retained = memory.mark_client_retained(FIRM_ID, client.id, retained_on="2026-10-01")

    assert retained is not None
    assert retained.first_retained_at == "2026-10-01"
    assert retained.prospect_stage is None


def test_the_retained_transition_keeps_an_earlier_date(memory):
    client = a_client(first_retained_at="2020-01-02")
    memory.create_client(client)
    retained = memory.mark_client_retained(FIRM_ID, client.id, retained_on="2026-10-01")
    assert retained is not None
    assert retained.first_retained_at == "2020-01-02"


def test_a_whole_record_save_leaves_the_stage_as_stored(memory):
    client = a_client()
    memory.create_client(client)
    memory.set_client_prospect_stage(FIRM_ID, client.id, "possible")

    memory.update_client(replace(client, lead_source="Referral"))

    stored = memory.get_client(FIRM_ID, client.id)
    assert stored is not None
    assert stored.prospect_stage == "possible"
