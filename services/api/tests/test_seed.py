"""The fixture loader that opens the tenancy loop on dev and on staging.

What this file holds down, none of which is "seeding works" — that is the easy
part and its failure is loud:

  1. IT CANNOT REACH PROD. A table name is the only thing between this and a
     customer tenant, and the module is importable and runnable by hand, so the
     guard lives here rather than only in the shell that normally calls it.
  2. AN UNSET ${VAR} IS A REFUSAL, NEVER AN EMPTY STRING. This repo is public,
     so the staging fixture cannot carry its user's address and reads it from
     the environment. Substituting "" would seed a firm for nobody and look
     provisioned.
  3. IT CONVERGES RATHER THAN DUPLICATES. Re-running must not give one person
     two firms — `find_user` raises rather than guessing when that happens, so
     the damage would surface much later as a 500 on every request.
  4. A REFUSAL WRITES NOTHING. The firm row is created before its users, so a
     guard that fired between them would leave a firm nobody belongs to.

Every identifier below is obviously fake. This repo is public.
"""

from __future__ import annotations

import json
from pathlib import Path

import pytest
from insolvia_api.adapters.memory.firm_store import MemoryFirmStore
from insolvia_api.core.ports import FirmStore
from insolvia_api.entrypoints.seed import Dependencies, RefusedError, main

DEV_TABLE = "insolvia-firms-dev-0123456789ab"
STAGING_TABLE = "insolvia-firms-staging"
POOL = "us-east-1_examplepool"

ALICE = "11111111-2222-3333-4444-555555555555"
BOB = "99999999-8888-7777-6666-555555555555"
DIRECTORY = {"alice@insolvia.test": ALICE, "bob@insolvia.test": BOB}


def user(email: str = "alice@insolvia.test", **overrides: object) -> dict[str, object]:
    return {
        "email": email,
        "displayName": "Example Person",
        "role": "attorney",
        "isAdmin": True,
        "accessAllCases": True,
        **overrides,
    }


def fixture(tmp_path: Path, body: object, name: str = "seed.json") -> Path:
    path = tmp_path / name
    path.write_text(json.dumps(body))
    return path


def one_firm(tmp_path: Path, *users: dict[str, object]) -> Path:
    return fixture(
        tmp_path, {"firms": [{"name": "Example & Partners", "users": list(users)}]}
    )


def run(
    path: Path,
    store: FirmStore,
    *,
    table: str = DEV_TABLE,
    check: bool = False,
    directory: dict[str, str] | None = None,
) -> int:
    known = DIRECTORY if directory is None else directory

    def resolve(email: str) -> str:
        # RefusedError, not KeyError: the real Cognito resolver turns
        # UserNotFoundException into a refusal, and a fake that threw something
        # else would let a bug through by testing a contract nobody implements.
        if email not in known:
            raise RefusedError(f"no pool user for {email}")
        return known[email]

    argv = [
        "--fixture",
        str(path),
        "--firm-table",
        table,
        "--user-pool-id",
        POOL,
    ]
    if check:
        argv.append("--check")
    return main(
        argv,
        deps=Dependencies(firm_store=lambda _: store, subjects=lambda _: resolve),
    )


# ── the guard ───────────────────────────────────────────────────────


@pytest.mark.parametrize(
    "table",
    [
        "insolvia-firms-prod",
        "insolvia-firms-prod-0123456789ab",
        "insolvia-firms-dev-NOTHEX000000",
        "insolvia-firms-dev-0123456789",
        # The right shape for the wrong store: the guard is per-kind, not a
        # general "looks like a non-prod table" test.
        "insolvia-cases-staging",
    ],
)
def test_an_unseedable_table_is_refused(tmp_path: Path, table: str) -> None:
    store = MemoryFirmStore()

    assert run(one_firm(tmp_path, user()), store, table=table) == 2

    assert store.find_user(ALICE) is None


@pytest.mark.parametrize("table", [DEV_TABLE, STAGING_TABLE])
def test_dev_and_staging_are_both_seedable(tmp_path: Path, table: str) -> None:
    store = MemoryFirmStore()

    assert run(one_firm(tmp_path, user()), store, table=table) == 0

    assert store.find_user(ALICE) is not None


# ── ${VAR} expansion ────────────────────────────────────────────────


def test_an_unset_variable_is_refused_rather_than_emptied(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.delenv("SEED_TEST_EMAIL", raising=False)
    store = MemoryFirmStore()

    status = run(one_firm(tmp_path, user("${SEED_TEST_EMAIL}")), store)

    assert status == 2
    assert store.get_firm("any") is None


def test_an_empty_variable_is_refused_too(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """Set-but-empty is the shape a missing CI secret actually takes.

    `${{ secrets.MISSING }}` renders as "", not as an unset variable, so a
    loader that only checked for absence would seed a firm for nobody on
    exactly the misconfiguration that is most likely.
    """
    monkeypatch.setenv("SEED_TEST_EMAIL", "")
    store = MemoryFirmStore()

    assert run(one_firm(tmp_path, user("${SEED_TEST_EMAIL}")), store) == 2


def test_a_set_variable_is_expanded(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setenv("SEED_TEST_EMAIL", "alice@insolvia.test")
    store = MemoryFirmStore()

    assert run(one_firm(tmp_path, user("${SEED_TEST_EMAIL}")), store) == 0

    seeded = store.find_user(ALICE)
    assert seeded is not None
    assert seeded.email == "alice@insolvia.test"


# ── what gets written ───────────────────────────────────────────────


def test_the_seeded_person_is_an_active_admin_of_an_active_firm(
    tmp_path: Path,
) -> None:
    store = MemoryFirmStore()

    assert run(one_firm(tmp_path, user()), store) == 0

    seeded = store.find_user(ALICE)
    assert seeded is not None
    assert seeded.status == "active"
    # All three together are what clears the 403.
    assert seeded.is_admin is True
    assert seeded.access_all_cases is True
    firm = store.get_firm(seeded.firm_id)
    assert firm is not None
    assert firm.status == "active"
    assert firm.name == "Example & Partners"


def test_two_people_in_one_fixture_firm_share_it(tmp_path: Path) -> None:
    store = MemoryFirmStore()

    assert run(one_firm(tmp_path, user(), user("bob@insolvia.test")), store) == 0

    alice = store.find_user(ALICE)
    bob = store.find_user(BOB)
    assert alice is not None
    assert bob is not None
    assert alice.firm_id == bob.firm_id


# ── convergence ─────────────────────────────────────────────────────


def test_seeding_twice_leaves_one_firm_and_one_membership(tmp_path: Path) -> None:
    store = MemoryFirmStore()
    path = one_firm(tmp_path, user())
    assert run(path, store) == 0
    first = store.find_user(ALICE)
    assert first is not None

    assert run(path, store) == 0

    again = store.find_user(ALICE)
    assert again is not None
    assert again.firm_id == first.firm_id
    assert len(store.list_users(first.firm_id)) == 1


def test_a_fixture_that_grows_a_colleague_extends_the_existing_firm(
    tmp_path: Path,
) -> None:
    """The reason convergence is per-user rather than per-firm.

    A firm's id is a uuid minted at creation, so there is no natural key to ask
    "is this firm already here?" with. Keying on the people in it is what makes
    a fixture editable after it has been loaded once — otherwise adding a
    colleague creates a second firm beside the first.
    """
    store = MemoryFirmStore()
    assert run(one_firm(tmp_path, user()), store) == 0
    firm_id = store.find_user(ALICE).firm_id  # type: ignore[union-attr]

    grown = one_firm(tmp_path, user(), user("bob@insolvia.test"))
    assert run(grown, store) == 0

    bob = store.find_user(BOB)
    assert bob is not None
    assert bob.firm_id == firm_id
    assert len(store.list_users(firm_id)) == 2


def test_people_already_split_across_firms_is_refused(tmp_path: Path) -> None:
    store = MemoryFirmStore()
    assert run(one_firm(tmp_path, user()), store) == 0
    assert run(one_firm(tmp_path, user("bob@insolvia.test")), store) == 0

    together = one_firm(tmp_path, user(), user("bob@insolvia.test"))

    assert run(together, store) == 2


# ── refusals write nothing ──────────────────────────────────────────


def test_an_unknown_account_is_refused_before_the_firm_is_created(
    tmp_path: Path,
) -> None:
    """Resolution happens up front for exactly this case.

    The firm row is written before its users, so resolving lazily would create
    the firm, then fail on the missing account, and leave a firm nobody belongs
    to — invisible until someone wonders why the table has spare firms.
    """
    store = MemoryFirmStore()
    path = one_firm(tmp_path, user(), user("nobody@insolvia.test"))

    assert run(path, store) == 2

    assert store.find_user(ALICE) is None


def test_a_firm_with_no_users_is_refused(tmp_path: Path) -> None:
    store = MemoryFirmStore()
    path = fixture(tmp_path, {"firms": [{"name": "Empty LLP", "users": []}]})

    assert run(path, store) == 2


def test_a_malformed_user_is_refused_by_the_same_parser_a_route_uses(
    tmp_path: Path,
) -> None:
    store = MemoryFirmStore()
    path = one_firm(tmp_path, user(role="wizard"))

    with pytest.raises(Exception, match=r"[Rr]ole"):
        run(path, store)

    assert store.get_firm("any") is None


def test_a_missing_fixture_is_refused(tmp_path: Path) -> None:
    assert run(tmp_path / "absent.json", MemoryFirmStore()) == 2


def test_invalid_json_is_refused(tmp_path: Path) -> None:
    path = tmp_path / "broken.json"
    path.write_text("{not json")

    assert run(path, MemoryFirmStore()) == 2


# ── --check ─────────────────────────────────────────────────────────


def test_check_reports_missing_rows_without_writing(tmp_path: Path) -> None:
    store = MemoryFirmStore()

    assert run(one_firm(tmp_path, user()), store, check=True) == 1

    assert store.find_user(ALICE) is None


def test_check_succeeds_once_the_fixture_is_loaded(tmp_path: Path) -> None:
    store = MemoryFirmStore()
    path = one_firm(tmp_path, user())
    assert run(path, store) == 0

    assert run(path, store, check=True) == 0


def test_check_is_refused_against_prod_too(tmp_path: Path) -> None:
    """`--check` is the flag someone reaches for to "just look" — which is
    exactly when a table name gets pasted carelessly."""
    store = MemoryFirmStore()

    assert (
        run(one_firm(tmp_path, user()), store, table="insolvia-firms-prod", check=True)
        == 2
    )
