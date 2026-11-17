"""Running one store test against both implementations of its port (ADR 0030:
"store tests run against memory and Postgres").

A store test's fixture takes `params=BACKENDS` and, for "postgres", asks
`postgres_url_with_cases` for an empty, migrated database holding the cases
its rows hang off — every case child's foreign key names its case.
"""

from __future__ import annotations

import pytest
from insolvia_core.adapters.memory.case_store import MemoryCaseStore
from insolvia_core.adapters.memory.debtor_store import MemoryDebtorStore
from insolvia_core.adapters.memory.firm_store import MemoryFirmStore
from insolvia_core.adapters.postgres.case_store import PostgresCaseStore
from insolvia_core.adapters.postgres.connection import connect
from insolvia_core.adapters.postgres.debtor_store import PostgresDebtorStore
from insolvia_core.adapters.postgres.firm_store import PostgresFirmStore
from insolvia_core.ports import CaseStore, DebtorStore, FirmStore

BACKENDS = ("memory", "postgres")

SEED_FIRM = "00000000-0000-4000-8000-00000000f1a0"


def seed_cases(database_url: str, *case_ids: str, firm_id: str = SEED_FIRM) -> None:
    """Bare case rows, enough for a child row's foreign key."""
    with connect(database_url) as conn:
        for case_id in case_ids:
            conn.execute(
                "INSERT INTO cases (id, firm_id, created_by, chapter, district,"
                " status, created_at, updated_at)"
                " VALUES (%s, %s, 'seed', 7, 'Seed District', 'intake', now(), now())"
                " ON CONFLICT DO NOTHING",
                (case_id, firm_id),
            )


def postgres_url_with_cases(request: pytest.FixtureRequest, *case_ids: str) -> str:
    request.getfixturevalue("postgres")  # empties every table first
    url: str = request.getfixturevalue("postgres_url")
    seed_cases(url, *case_ids)
    return url


def firm_store(request: pytest.FixtureRequest, *firm_ids: str) -> FirmStore:
    """Where a backend's case stores find the clients they link: the memory
    twin (pass it to `case_stores`), or the same database's firm tables, with
    a bare row for each of `firm_ids` (a client's firm is a foreign key)."""
    if request.param == "memory":
        return MemoryFirmStore()
    url = postgres_url_with_cases(request)
    with connect(url) as conn:
        for firm_id in firm_ids:
            conn.execute(
                "INSERT INTO firms (id, item) VALUES (%s, '{}') ON CONFLICT DO NOTHING",
                (firm_id,),
            )
    return PostgresFirmStore(url)


def case_stores(
    request: pytest.FixtureRequest, *, firm_store: FirmStore | None = None
) -> tuple[CaseStore, DebtorStore]:
    """A case store and the debtor store that shares its data, as the routes
    compose them: one memory debtor store inside the memory case store, or
    two Postgres stores over one database. `firm_store` is the memory twins'
    only; the Postgres stores read the clients from their own database."""
    if request.param == "memory":
        debtors = MemoryDebtorStore(firm_store=firm_store)
        return MemoryCaseStore(debtor_store=debtors), debtors
    url = postgres_url_with_cases(request)
    return PostgresCaseStore(url), PostgresDebtorStore(url)
