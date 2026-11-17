"""Both ClientBindingStore implementations (ADR 0023, ADR 0030).

Every test runs against the memory twin and the Postgres adapter, held to the
same conditions: one live binding per subject per firm, one holder per role
per case, and a refused write that leaves nothing behind.

Every identifier below is obviously fake. This repo is public.
"""

from __future__ import annotations

from dataclasses import replace

import pytest
from insolvia_core.adapters.memory.client_binding_store import (
    MemoryClientBindingStore,
)
from insolvia_core.adapters.postgres.client_binding_store import (
    PostgresClientBindingStore,
)
from insolvia_core.adapters.postgres.connection import connect
from insolvia_core.clients import (
    ACTIVE,
    INVITED,
    REVOKED,
    ClientBinding,
    revoke,
)
from insolvia_core.errors import ConflictError

FIRM = "00000000-0000-4000-8000-00000000f18a"
OTHER_FIRM = "00000000-0000-4000-8000-00000000f18b"
CASE = "00000000-0000-4000-8000-0000000ca5e1"
STAFF = "00000000-0000-4000-8000-00000000a11c"
PAT = "00000000-0000-4000-8000-0000000000a1"
SAM = "00000000-0000-4000-8000-0000000000a2"


@pytest.fixture(params=["memory", "postgres"])
def store(request):
    if request.param == "memory":
        return MemoryClientBindingStore()
    postgres = request.getfixturevalue("postgres")
    for firm_id in (FIRM, OTHER_FIRM):  # `firm_id` is a foreign key
        postgres.execute("INSERT INTO firms (id, item) VALUES (%s, '{}')", (firm_id,))
    return PostgresClientBindingStore(request.getfixturevalue("postgres_url"))


def holder(store, role: str) -> str | None:
    """Who holds `role` on CASE, read from the store's own claims."""
    if isinstance(store, MemoryClientBindingStore):
        return store.claims.get((CASE, role))
    with connect(store.database_url) as conn:
        row = conn.execute(
            "SELECT subject FROM client_role_claims WHERE case_id = %s AND role = %s",
            (CASE, role),
        ).fetchone()
    return None if row is None else row["subject"]


def binding(
    subject: str = PAT,
    roles: tuple[str, ...] = ("debtor_1",),
    status: str = INVITED,
    firm_id: str = FIRM,
) -> ClientBinding:
    return ClientBinding(
        firm_id=firm_id,
        case_id=CASE,
        subject=subject,
        email=f"{subject[-2:]}@example.test",
        display_name="Pat Example",
        roles=roles,
        status=status,
        invited_by=STAFF,
        created_at="2026-09-26T00:00:00.000000Z",
        updated_at="2026-09-26T00:00:00.000000Z",
    )


def test_two_bindings_cannot_both_hold_debtor_2(store):
    """The ADR's rule under a race: the second writer did not see the first
    and so narrowed nobody, and the claim refuses it."""
    store.bind(binding(PAT, roles=("debtor_2",)), narrowed=())

    with pytest.raises(ConflictError):
        store.bind(binding(SAM, roles=("debtor_2",)), narrowed=())

    assert holder(store, "debtor_2") == PAT
    assert store.get(FIRM, SAM) is None


def test_a_narrowing_moves_the_role_and_rewrites_the_first_binding(store):
    first = binding(PAT, roles=("debtor_1", "debtor_2"), status=ACTIVE)
    store.bind(first, narrowed=())

    store.bind(
        binding(SAM, roles=("debtor_2",)),
        narrowed=(replace(first, roles=("debtor_1",)),),
    )

    assert holder(store, "debtor_1") == PAT
    assert holder(store, "debtor_2") == SAM
    assert store.get(FIRM, PAT).roles == ("debtor_1",)  # type: ignore[union-attr]
    assert [b.subject for b in store.list_for_case(CASE)] == [PAT, SAM]


def test_a_live_subject_cannot_be_bound_twice_but_a_revoked_one_can(store):
    store.bind(binding(), narrowed=())
    with pytest.raises(ConflictError):
        store.bind(binding(), narrowed=())

    store.update(revoke(binding()))
    store.bind(binding(), narrowed=())

    assert store.get(FIRM, PAT).status == INVITED  # type: ignore[union-attr]


def test_revoking_releases_the_role_for_someone_else(store):
    store.bind(binding(PAT, roles=("debtor_2",)), narrowed=())

    store.update(revoke(binding(PAT, roles=("debtor_2",))))
    store.bind(binding(SAM, roles=("debtor_2",)), narrowed=())

    assert holder(store, "debtor_2") == SAM


def test_updating_a_binding_that_does_not_exist_returns_none(store):
    assert store.update(binding(status=REVOKED)) is None


def test_a_subject_in_two_firms_raises_rather_than_choosing(store):
    store.bind(binding(firm_id=FIRM), narrowed=())
    store.bind(binding(firm_id=OTHER_FIRM), narrowed=())

    with pytest.raises(RuntimeError):
        store.find(PAT)


def test_the_email_lookup_is_firm_scoped(store):
    store.bind(binding(firm_id=OTHER_FIRM), narrowed=())

    assert store.find_by_email(FIRM, "a1@example.test") is None
    assert store.find_by_email(OTHER_FIRM, "A1@example.test") is not None


def test_a_refused_bind_writes_nothing(store):
    """All of the transaction or none of it: the second subject's binding is
    refused by the claim, so its row must not exist either."""
    store.bind(binding(PAT, roles=("debtor_2",)), narrowed=())
    with pytest.raises(ConflictError):
        store.bind(binding(SAM, roles=("debtor_1", "debtor_2")), narrowed=())

    assert store.get(FIRM, SAM) is None
    assert holder(store, "debtor_1") is None


def test_a_stale_narrowing_is_refused(store):
    """The narrowed binding was revoked meanwhile: the write is refused."""
    first = binding(PAT, roles=("debtor_1", "debtor_2"), status=ACTIVE)
    store.bind(first, narrowed=())
    store.update(revoke(first))

    with pytest.raises(ConflictError):
        store.bind(
            binding(SAM, roles=("debtor_2",)),
            narrowed=(replace(first, roles=("debtor_1",)),),
        )
    assert store.get(FIRM, SAM) is None


def test_a_binding_round_trips_and_is_found_by_subject(store):
    written = binding(roles=("debtor_1", "debtor_2"))
    store.bind(written, narrowed=())
    assert store.get(FIRM, PAT) == written
    assert store.find(PAT) == written
    assert store.find(SAM) is None
    assert store.list_for_case(CASE) == (written,)
