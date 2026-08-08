"""The seeding CLI that opens the tenancy loop on a developer machine.

Three things this file exists to hold down, and none of them is "seeding works"
— that is the easy part and a developer finds out within seconds.

  1. IT CANNOT REACH A SHARED ENVIRONMENT. A table name is the only thing
     standing between this and writing into staging or prod, and the module is
     importable and runnable by hand, so the guard has to live in the module
     rather than only in the shell script that normally calls it.
  2. IT IS IDEMPOTENT BY SUBJECT. A developer who runs it twice must not end up
     in two firms. `find_user` raises rather than guesses when that happens
     (core/ports.FirmStore), so the damage would surface much later as a 500 on
     every request, with nothing pointing back here.
  3. GUARDS RUN BEFORE WRITES. A refusal must leave the store untouched, not
     half-populated — a firm created and then a failed `add_user` is the
     derelict-firm state this whole command exists to avoid.

Every identifier below is obviously fake. This repo is public.
"""

from __future__ import annotations

import pytest
from insolvia_api.adapters.memory.firm_store import MemoryFirmStore
from insolvia_api.core.ports import FirmStore
from insolvia_api.entrypoints.seed import StoreFactories, main

DEV_TABLE = "insolvia-firms-dev-0123456789ab"
SUBJECT = "11111111-2222-3333-4444-555555555555"


def firm_args(table: str = DEV_TABLE, subject: str = SUBJECT) -> list[str]:
    return [
        "firm",
        "--firm-table",
        table,
        "--subject",
        subject,
        "--firm-name",
        "Example & Partners",
        "--email",
        "dev@insolvia.test",
        "--display-name",
        "Dev User",
    ]


def check_args(table: str = DEV_TABLE, subject: str = SUBJECT) -> list[str]:
    return ["firm", "--firm-table", table, "--subject", subject, "--check"]


def run(argv: list[str], store: FirmStore) -> int:
    return main(argv, stores=StoreFactories(firm=lambda _: store))


@pytest.mark.parametrize(
    "table",
    [
        "insolvia-firms-staging",
        "insolvia-firms-prod",
        # The shapes a typo or a copy-paste actually produces, rather than
        # arbitrary junk: a shared table wearing a dev-looking suffix, a suffix
        # that is not hex, and one that is the wrong length.
        "insolvia-firms-prod-0123456789ab-dev",
        "insolvia-firms-dev-NOTHEX000000",
        "insolvia-firms-dev-0123456789",
        # The right shape for the wrong store — cases are not firms, and the
        # guard is per-kind rather than a general "looks like dev" test.
        "insolvia-cases-dev-0123456789ab",
    ],
)
def test_a_table_outside_this_machine_is_refused(table: str) -> None:
    store = MemoryFirmStore()

    assert run(firm_args(table=table), store) == 2

    assert store.find_user(SUBJECT) is None


def test_seeding_makes_the_subject_an_active_admin_of_an_active_firm() -> None:
    store = MemoryFirmStore()

    assert run(firm_args(), store) == 0

    user = store.find_user(SUBJECT)
    assert user is not None
    assert user.status == "active"
    # All three together are what clears the 403: `current_accessor` needs an
    # active user in an active firm, and `access_all_cases` is what lets the
    # firm's only seat see the cases it opens.
    assert user.is_admin is True
    assert user.access_all_cases is True
    firm = store.get_firm(user.firm_id)
    assert firm is not None
    assert firm.status == "active"
    assert firm.name == "Example & Partners"


def test_seeding_twice_leaves_one_firm_and_one_membership() -> None:
    store = MemoryFirmStore()
    assert run(firm_args(), store) == 0
    first = store.find_user(SUBJECT)
    assert first is not None

    assert run(firm_args(), store) == 0

    # Same firm, not a second one — and `find_user` would raise instead of
    # returning if the second run had written a competing membership.
    again = store.find_user(SUBJECT)
    assert again is not None
    assert again.firm_id == first.firm_id
    assert len(store.list_users(first.firm_id)) == 1


def test_a_refusal_writes_nothing_at_all() -> None:
    """Specifically: no firm row either, not just no user row.

    The firm is created before the user, so a guard that ran between them would
    leave a firm nobody belongs to — invisible until someone wonders why the
    table has more firms than developers.
    """
    store = MemoryFirmStore()

    assert run(firm_args(table="insolvia-firms-prod"), store) == 2

    assert store.find_user(SUBJECT) is None
    assert store.get_firm("any") is None


def test_seeding_without_the_fields_it_would_write_is_refused() -> None:
    store = MemoryFirmStore()

    status = run(["firm", "--firm-table", DEV_TABLE, "--subject", SUBJECT], store)

    assert status == 2
    assert store.find_user(SUBJECT) is None


def test_check_reports_an_unprovisioned_subject_without_writing() -> None:
    store = MemoryFirmStore()

    assert run(check_args(), store) == 1

    assert store.find_user(SUBJECT) is None


def test_check_succeeds_once_the_subject_is_in_a_firm() -> None:
    store = MemoryFirmStore()
    assert run(firm_args(), store) == 0

    assert run(check_args(), store) == 0


def test_check_is_refused_against_a_shared_table_too() -> None:
    """The guard is not a write guard — a read against prod is also refused.

    Worth its own case because `--check` is the flag someone reaches for to
    "just look", which is exactly when a table name gets pasted carelessly.
    """
    store = MemoryFirmStore()

    assert run(check_args(table="insolvia-firms-prod"), store) == 2


def test_a_second_person_seeded_on_the_same_machine_gets_their_own_firm() -> None:
    """Not a supported workflow, but not one that may corrupt the table either.

    `dev-aws-seed.sh` seeds whoever DEV_USER_EMAIL names, so a developer with
    two pool accounts can reach this. One firm each is the honest outcome;
    silently adding the second person to the first firm would be this command
    inventing a colleague relationship nobody asked for.
    """
    store = MemoryFirmStore()
    other = "99999999-8888-7777-6666-555555555555"
    assert run(firm_args(), store) == 0

    assert run(firm_args(subject=other), store) == 0

    first = store.find_user(SUBJECT)
    second = store.find_user(other)
    assert first is not None
    assert second is not None
    assert first.firm_id != second.firm_id
