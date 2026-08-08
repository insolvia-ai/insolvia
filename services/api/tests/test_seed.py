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


class FakeAccounts:
    """A pool that starts with `known` in it and records what gets created.

    RefusedError rather than KeyError on a miss: the real adapter turns
    UnknownUser into a refusal, and a fake that threw something else would let
    a bug through by honouring a contract nobody implements.
    """

    def __init__(self, known: dict[str, str] | None = None) -> None:
        self.subjects = dict(DIRECTORY if known is None else known)
        self.created: list[str] = []
        self.passwords: dict[str, str] = {}
        self._next = 0

    def subject_of(self, email: str) -> str:
        if email not in self.subjects:
            raise RefusedError(f"no pool user for {email}")
        return self.subjects[email]

    def ensure(self, email: str, password: str) -> str:
        if email not in self.subjects:
            self._next += 1
            self.subjects[email] = f"aaaaaaaa-bbbb-cccc-dddd-{self._next:012d}"
            self.created.append(email)
        self.passwords[email] = password
        return self.subjects[email]


def run(
    path: Path,
    store: FirmStore,
    *,
    table: str = DEV_TABLE,
    check: bool = False,
    accounts: FakeAccounts | None = None,
) -> int:
    pool = accounts if accounts is not None else FakeAccounts()
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
        deps=Dependencies(firm_store=lambda _: store, accounts=lambda _: pool),
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


# ── a fixture that supplies its own subjects ────────────────────────


def test_a_password_in_the_fixture_creates_the_account(tmp_path: Path) -> None:
    """A password is what says "this environment owns its accounts".

    Staging's does; dev's does not, because dev-aws-create-user.sh prompts a
    human for one and a fixture must never carry that.
    """
    store = MemoryFirmStore()
    pool = FakeAccounts(known={})
    path = one_firm(tmp_path, user("e2e-admin@insolvia.test", password="hunter2ABCDEF"))

    assert run(path, store, table=STAGING_TABLE, accounts=pool) == 0

    assert pool.created == ["e2e-admin@insolvia.test"]
    assert pool.passwords["e2e-admin@insolvia.test"] == "hunter2ABCDEF"
    seeded = store.find_user(pool.subjects["e2e-admin@insolvia.test"])
    assert seeded is not None


def test_the_password_is_reset_on_an_account_that_already_exists(
    tmp_path: Path,
) -> None:
    """Converging the password is what makes a rotated secret take effect.

    It also clears FORCE_CHANGE_PASSWORD, which admin_create_user leaves
    behind and which hangs the hosted UI on a screen a browser test cannot
    answer.
    """
    store = MemoryFirmStore()
    pool = FakeAccounts(known={"alice@insolvia.test": ALICE})
    path = one_firm(tmp_path, user(password="rotatedABCDEF1"))

    assert run(path, store, table=STAGING_TABLE, accounts=pool) == 0

    assert pool.created == []
    assert pool.passwords["alice@insolvia.test"] == "rotatedABCDEF1"


def test_without_a_password_the_account_must_already_exist(tmp_path: Path) -> None:
    store = MemoryFirmStore()
    pool = FakeAccounts(known={})

    assert run(one_firm(tmp_path, user()), store, accounts=pool) == 2

    assert pool.created == []
    assert store.get_firm("any") is None


def test_check_never_creates_an_account(tmp_path: Path) -> None:
    """--check promises to write nothing, and a pool account is a write.

    Without this the report itself would provision the thing it is reporting
    on, and the second run would say everything is fine.
    """
    store = MemoryFirmStore()
    pool = FakeAccounts(known={})
    path = one_firm(tmp_path, user("e2e-admin@insolvia.test", password="hunter2ABCDEF"))

    assert run(path, store, table=STAGING_TABLE, check=True, accounts=pool) == 2

    assert pool.created == []
    assert pool.passwords == {}


def test_several_people_across_two_firms_are_all_provisioned(
    tmp_path: Path,
) -> None:
    """The shape seeds/staging.json actually has, and the reason for all this.

    Cross-tenant isolation cannot be tested from inside one firm, and the
    per-user cost of a second firm has to be an edit to a fixture rather than
    two more secrets and another script run.
    """
    store = MemoryFirmStore()
    pool = FakeAccounts(known={})
    path = fixture(
        tmp_path,
        {
            "firms": [
                {
                    "name": "Insolvia E2E",
                    "users": [
                        user("e2e-admin@insolvia.test", password="hunter2ABCDEF"),
                        user(
                            "e2e-paralegal@insolvia.test",
                            password="hunter2ABCDEF",
                            role="paralegal",
                            isAdmin=False,
                            accessAllCases=False,
                        ),
                    ],
                },
                {
                    "name": "Other Firm LLP",
                    "users": [
                        user("e2e-outsider@insolvia.test", password="hunter2ABCDEF")
                    ],
                },
            ]
        },
    )

    assert run(path, store, table=STAGING_TABLE, accounts=pool) == 0

    assert len(pool.created) == 3
    admin = store.find_user(pool.subjects["e2e-admin@insolvia.test"])
    paralegal = store.find_user(pool.subjects["e2e-paralegal@insolvia.test"])
    outsider = store.find_user(pool.subjects["e2e-outsider@insolvia.test"])
    assert admin is not None
    assert paralegal is not None
    assert outsider is not None
    # Colleagues share a firm; the outsider must not, or the 404 that proves
    # cross-tenant isolation would be untestable.
    assert admin.firm_id == paralegal.firm_id
    assert outsider.firm_id != admin.firm_id
    assert paralegal.is_admin is False
    assert paralegal.access_all_cases is False


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
