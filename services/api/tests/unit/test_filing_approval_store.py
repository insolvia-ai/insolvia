"""The per-filing approval store (ADR 0024 guardrail 1) held to one contract
by the memory twin and the Postgres adapter (ADR 0030): one current approval
per case, moved only from the one the caller read, and a single use.
Every identifier is obviously fake."""

from __future__ import annotations

from dataclasses import replace
from threading import Barrier, Thread

import pytest
from insolvia_api.adapters.memory.filing_approval_store import (
    MemoryFilingApprovalStore,
)
from insolvia_api.adapters.postgres.filing_approval_store import (
    PostgresFilingApprovalStore,
)
from insolvia_api.core.filing_approval import FilingApproval
from insolvia_core.adapters.postgres.connection import connect
from insolvia_core.errors import ConflictError

CASE = "00000000-0000-4000-8000-0000000000c1"
FIRM = "00000000-0000-4000-8000-00000000f18a"
NOW = 4_102_444_800
BACKENDS = ("memory", "postgres")


@pytest.fixture(params=BACKENDS)
def store(request):
    if request.param == "memory":
        return MemoryFilingApprovalStore()
    request.getfixturevalue("postgres")
    url = request.getfixturevalue("postgres_url")
    with connect(url) as conn:
        conn.execute(
            "INSERT INTO cases (id, firm_id, created_by, chapter, district,"
            " status, created_at, updated_at)"
            " VALUES (%s, %s, 'seed', 7, 'Seed District', 'ready_to_file',"
            " now(), now())",
            (CASE, FIRM),
        )
    return PostgresFilingApprovalStore(url)


def approval(approval_id: str, **changes: object) -> FilingApproval:
    base = FilingApproval(
        approval_id=approval_id,
        filing_id=f"filing-{approval_id}",
        case_id=CASE,
        firm_id=FIRM,
        attorney_id="attorney-1",
        credential_id="credential-1",
        authorization_id="authorization-1",
        digest="digest-1",
        packet_id="packet-1",
        court="flmb",
        division="3",
        registry_release="2026-10-06",
        approved_at="2100-01-01T00:00:00.000Z",
        authenticated_at=NOW,
        expires_at=NOW + 900,
    )
    return replace(base, **changes)  # type: ignore[arg-type]


def consume(store, approval_id: str, *, digest: str = "digest-1", now: int = NOW):
    return store.consume(
        CASE, approval_id, digest=digest, now=now, consumed_at="2100-01-01T00:01:00Z"
    )


def test_the_first_approval_becomes_current_and_round_trips(store):
    first = approval("a1")
    store.create(first, replacing=None, voided_at="x")
    assert store.current(CASE) == first
    assert store.get(CASE, "a1") == first
    assert store.get(CASE, "nope") is None


def test_a_new_approval_supersedes_the_one_it_read(store):
    first = approval("a1")
    store.create(first, replacing=None, voided_at="x")
    second = approval("a2")
    store.create(second, replacing=first, voided_at="2100-01-01T00:02:00Z")
    assert store.current(CASE) == second
    old = store.get(CASE, "a1")
    assert (old.status, old.void_reason) == ("voided", "superseded")
    assert old.voided_at == "2100-01-01T00:02:00Z"


def test_a_lost_race_changes_nothing(store):
    first = approval("a1")
    store.create(first, replacing=None, voided_at="x")
    with pytest.raises(ConflictError):
        store.create(approval("a2"), replacing=None, voided_at="x")
    store.create(approval("a3"), replacing=first, voided_at="x")
    with pytest.raises(ConflictError):
        store.create(approval("a4"), replacing=first, voided_at="x")
    assert store.current(CASE).approval_id == "a3"
    assert store.get(CASE, "a2") is None
    assert store.get(CASE, "a4") is None


def test_an_approval_is_consumed_once(store):
    store.create(approval("a1"), replacing=None, voided_at="x")
    assert not consume(store, "a1", digest="another-digest")
    assert not consume(store, "a1", now=NOW + 900)  # expired
    assert consume(store, "a1")
    assert not consume(store, "a1")
    used = store.get(CASE, "a1")
    assert (used.status, used.consumed_at) == ("consumed", "2100-01-01T00:01:00Z")
    assert not store.void(CASE, "a1", reason="r", voided_at="y")


def test_of_two_concurrent_consumers_exactly_one_wins(store):
    if isinstance(store, MemoryFilingApprovalStore):
        pytest.skip("the memory twin is not built for threads; the database is")
    store.create(approval("a1"), replacing=None, voided_at="x")
    barrier = Barrier(8)
    results: list[bool] = []

    def race() -> None:
        barrier.wait()
        results.append(consume(store, "a1"))

    threads = [Thread(target=race) for _ in range(8)]
    for thread in threads:
        thread.start()
    for thread in threads:
        thread.join()
    assert sorted(results) == [False] * 7 + [True]


def test_a_voided_approval_cannot_be_consumed(store):
    store.create(approval("a1"), replacing=None, voided_at="x")
    assert store.void(CASE, "a1", reason="packet_changed", voided_at="y")
    assert not store.void(CASE, "a1", reason="again", voided_at="z")
    assert not consume(store, "a1")
    assert store.get(CASE, "a1").void_reason == "packet_changed"
