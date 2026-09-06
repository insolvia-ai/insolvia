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
  5. A CASE IS DERIVED, NOT MINTED. Its id is a function of the target table,
     the firm, the fixture version and the handle, so a second run finds the
     rows it wrote and a fixture change never rewrites a row somebody works
     in. The bytes go through the same confirm step the API's complete route
     uses, so a copy that did not land is a refusal, never a `stored` row.

Every identifier below is obviously fake. This repo is public.
"""

from __future__ import annotations

import json
from pathlib import Path

import pytest
from insolvia_admin.entrypoints.seed import Dependencies, RefusedError, main
from insolvia_core.adapters.memory.firm_store import MemoryFirmStore
from insolvia_core.ports import FirmStore

DEV_TABLE = "insolvia-dev-0123456789ab-firms"
STAGING_TABLE = "insolvia-staging-firms"
POOL = "us-east-1_examplepool"

ALICE = "11111111-2222-3333-4444-555555555555"
BOB = "99999999-8888-7777-6666-555555555555"
DIRECTORY = {"alice@insolvia.test": ALICE, "bob@insolvia.test": BOB}


def user(email: str = "alice@insolvia.test", **overrides: object) -> dict[str, object]:
    return {
        "email": email,
        "firstName": "Example",
        "lastName": "Person",
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
        "load",
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
        "insolvia-prod-firms",
        "insolvia-prod-0123456789ab-firms",
        "insolvia-dev-NOTHEX000000-firms",
        "insolvia-dev-0123456789-firms",
        # The OLD env-last spelling, which the guard must now reject — a stale
        # script or a hand-typed name from before the rename. Written out
        # rather than derived, because it is exactly the string that must NOT
        # match any more.
        "insolvia-firms-staging",
        # The right shape for the wrong store: the guard is per-kind, not a
        # general "looks like a non-prod table" test.
        "insolvia-staging-cases",
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

    Staging's does; dev's does not, because the dev-aws-seed.sh wrapper makes
    the account itself and a fixture must never carry a human's password.
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
        run(one_firm(tmp_path, user()), store, table="insolvia-prod-firms", check=True)
        == 2
    )


# ── cases ───────────────────────────────────────────────────────────


from insolvia_core.adapters.memory.case_entity_store import (  # noqa: E402
    MemoryCaseEntityStore,
)
from insolvia_core.adapters.memory.case_store import MemoryCaseStore  # noqa: E402
from insolvia_core.adapters.memory.debtor_store import MemoryDebtorStore  # noqa: E402
from insolvia_core.adapters.memory.document_blobs import (  # noqa: E402
    MemoryDocumentBlobStore,
)
from insolvia_core.adapters.memory.document_store import (  # noqa: E402
    MemoryDocumentStore,
)
from insolvia_core.case_collections import COLLECTIONS  # noqa: E402

DEV_CASE_TABLE = "insolvia-dev-0123456789ab-cases"
DEV_BUCKET = "insolvia-dev-0123456789ab-case-documents-us-east-1"
FIXTURE_BUCKET = "insolvia-shared-dev-fixtures-us-east-1"
TYPED = {"source": "staff_typed"}
PDF = b"%PDF-1.4\n% fixture bytes\n"


class FakeFixtureObjects:
    """The shared bucket, in memory: `copy` lands bytes in a blob store the
    same way S3's server-side copy would, and `put` fills the bucket."""

    def __init__(
        self, blobs: MemoryDocumentBlobStore, objects: dict[str, bytes]
    ) -> None:
        self.blobs = blobs
        self.objects = dict(objects)
        self.copied: list[str] = []

    def copy(self, source_key: str, *, target_key: str, content_type: str) -> None:
        if source_key not in self.objects:
            raise AssertionError(f"no fixture object {source_key}")
        self.blobs.put_bytes(
            target_key, content=self.objects[source_key], content_type=content_type
        )
        self.copied.append(target_key)

    def put(self, key: str, *, content: bytes, content_type: str) -> None:
        self.objects[key] = content

    def digest(self, key: str) -> str | None:
        import hashlib

        found = self.objects.get(key)
        return hashlib.sha256(found).hexdigest() if found is not None else None


class Env:
    """A whole environment's stores, and the fixture folder beside the env file."""

    def __init__(self, tmp_path: Path) -> None:
        import hashlib

        self.firms = MemoryFirmStore()
        self.cases = MemoryCaseStore()
        self.debtors = MemoryDebtorStore()
        self.entities = MemoryCaseEntityStore()
        self.documents = MemoryDocumentStore()
        self.blobs = MemoryDocumentBlobStore()
        self.objects = FakeFixtureObjects(self.blobs, {"v1/objects/stub.pdf": PDF})
        self.accounts = FakeAccounts(known={})
        folder = tmp_path / "fixtures" / "v1"
        (folder / "objects").mkdir(parents=True)
        (folder / "objects" / "stub.pdf").write_bytes(PDF)
        (folder / "manifest.json").write_text(
            json.dumps(
                {
                    "version": "v1",
                    "objects": {
                        "objects/stub.pdf": {
                            "size": len(PDF),
                            "sha256": hashlib.sha256(PDF).hexdigest(),
                        }
                    },
                }
            )
        )
        (folder / "cases.json").write_text(
            json.dumps(
                {
                    "version": "v1",
                    "cases": [
                        {
                            "handle": "sample",
                            "chapter": 7,
                            "district": "NDCA",
                            "debtors": {
                                "debtor_1": {
                                    "name": {"given": "Sample"},
                                    "provenance": {"name.given": TYPED},
                                }
                            },
                            "collections": {
                                "creditors": [
                                    {
                                        "name": "Example Bank",
                                        "provenance": {"name": TYPED},
                                    }
                                ]
                            },
                            "documents": [
                                {
                                    "kind": "pay_stub",
                                    "fileName": "stub.pdf",
                                    "contentType": "application/pdf",
                                    "object": "objects/stub.pdf",
                                }
                            ],
                        }
                    ],
                }
            )
        )
        self.env_fixture = tmp_path / "env.json"
        self.env_fixture.write_text(
            json.dumps(
                {
                    "firms": [
                        {
                            "name": "Example & Partners",
                            "users": [
                                user(
                                    "e2e-admin@insolvia.test", password="hunter2ABCDEF"
                                )
                            ],
                        }
                    ],
                    "cases": [
                        {
                            "fixture": "v1",
                            "case": "sample",
                            "firm": "Example & Partners",
                            "openedBy": "admin",
                        }
                    ],
                }
            )
        )
        (tmp_path / "env.json").write_text(
            (tmp_path / "env.json")
            .read_text()
            .replace('"email": "e2e-admin', '"handle": "admin", "email": "e2e-admin')
        )

    def deps(self) -> Dependencies:
        return Dependencies(
            firm_store=lambda _: self.firms,
            accounts=lambda _: self.accounts,
            case_store=lambda _: self.cases,
            debtor_store=lambda _: self.debtors,
            entity_store=lambda _: self.entities,
            document_store=lambda _: self.documents,
            document_blobs=lambda _: self.blobs,
            fixture_objects=lambda _f, _t: self.objects,
        )

    def load(self, *extra: str, table: str = DEV_TABLE) -> int:
        return main(
            [
                "load",
                "--fixture",
                str(self.env_fixture),
                "--firm-table",
                table,
                "--user-pool-id",
                POOL,
                "--case-table",
                DEV_CASE_TABLE
                if table == DEV_TABLE
                else STAGING_TABLE.replace("firms", "cases"),
                "--document-bucket",
                DEV_BUCKET
                if table == DEV_TABLE
                else "insolvia-staging-case-documents-us-east-1",
                "--fixture-bucket",
                FIXTURE_BUCKET,
                *extra,
            ],
            deps=self.deps(),
        )

    def the_case(self):
        subject = self.accounts.subjects["e2e-admin@insolvia.test"]
        firm_id = self.firms.find_user(subject).firm_id  # type: ignore[union-attr]
        from insolvia_admin.entrypoints.seed import derived_id

        return self.cases.read_for_worker(
            derived_id(DEV_CASE_TABLE, firm_id, "v1", "sample")
        )


def test_a_fixture_case_lands_with_its_debtor_items_and_documents(
    tmp_path: Path,
) -> None:
    env = Env(tmp_path)

    assert env.load() == 0

    case = env.the_case()
    assert case is not None
    assert case.chapter == 7
    assert case.district == "NDCA"
    debtor = env.debtors.get(case.id, filing_role="debtor_1")
    assert debtor is not None
    assert debtor.name.given == "Sample"
    creditors = env.entities.list_for_case(case.id, COLLECTIONS["creditors"])
    assert [c.body.name for c in creditors] == ["Example Bank"]
    documents = env.documents.list_for_case(case.id)
    assert [d.status for d in documents] == ["stored"]
    assert documents[0].byte_size == len(PDF)
    # The bytes went through the target bucket, keyed the way the API keys them.
    assert env.blobs.get_bytes(documents[0].storage_ref) == PDF
    assert documents[0].storage_ref == f"cases/{case.id}/{documents[0].id}"


def test_the_opener_is_assigned_so_the_case_is_reachable(tmp_path: Path) -> None:
    env = Env(tmp_path)
    assert env.load() == 0
    case = env.the_case()
    assert case is not None

    subjects = [a.subject for a in env.cases.assignees(case.id)]
    assert subjects == [case.created_by]


def test_loading_twice_writes_nothing_new(tmp_path: Path) -> None:
    env = Env(tmp_path)
    assert env.load() == 0
    first = env.the_case()
    copied = list(env.objects.copied)

    assert env.load() == 0

    assert env.the_case() == first
    assert env.objects.copied == copied
    assert len(env.documents.list_for_case(first.id)) == 1  # type: ignore[union-attr]
    assert len(env.entities.list_for_case(first.id, COLLECTIONS["creditors"])) == 1  # type: ignore[union-attr]


def test_the_same_fixture_seeds_different_ids_into_different_targets(
    tmp_path: Path,
) -> None:
    """Two environments must not share an id: a row copied between them by
    hand would otherwise look like the same case."""
    from insolvia_admin.entrypoints.seed import derived_id

    assert derived_id(
        "insolvia-dev-0123456789ab-cases", "f", "v1", "sample"
    ) != derived_id("insolvia-staging-cases", "f", "v1", "sample")


def test_check_reports_the_missing_case_and_writes_nothing(tmp_path: Path) -> None:
    env = Env(tmp_path)
    env.accounts.subjects["e2e-admin@insolvia.test"] = ALICE

    assert env.load("--check") == 1

    assert env.cases.read_for_worker("anything") is None
    assert env.objects.copied == []


def test_a_copy_that_did_not_land_is_refused_before_the_row(tmp_path: Path) -> None:
    env = Env(tmp_path)
    env.objects.objects.clear()  # the bucket is empty

    with pytest.raises(AssertionError, match="no fixture object"):
        env.load()

    assert env.documents.list_for_case("anything") == ()


def test_a_manifest_size_mismatch_is_refused(tmp_path: Path) -> None:
    env = Env(tmp_path)
    env.objects.objects["v1/objects/stub.pdf"] = PDF + b"extra"

    assert env.load() == 2

    case = env.the_case()
    assert case is not None
    assert env.documents.list_for_case(case.id) == ()


def test_a_case_needs_its_firm_seeded_first(tmp_path: Path) -> None:
    env = Env(tmp_path)
    body = json.loads(env.env_fixture.read_text())
    body["cases"][0]["firm"] = "Nobody LLP"
    env.env_fixture.write_text(json.dumps(body))

    assert env.load() == 2


def test_a_prod_looking_case_table_is_refused(tmp_path: Path) -> None:
    env = Env(tmp_path)
    status = main(
        [
            "load",
            "--fixture",
            str(env.env_fixture),
            "--firm-table",
            DEV_TABLE,
            "--user-pool-id",
            POOL,
            "--case-table",
            "insolvia-prod-cases",
            "--document-bucket",
            DEV_BUCKET,
            "--fixture-bucket",
            FIXTURE_BUCKET,
        ],
        deps=env.deps(),
    )
    assert status == 2


def test_a_malformed_fixture_row_fails_with_the_apis_own_error(tmp_path: Path) -> None:
    """The parser is the route's, so a populated field without provenance is
    the same 400-shaped refusal the API gives."""
    env = Env(tmp_path)
    folder = tmp_path / "fixtures" / "v1" / "cases.json"
    body = json.loads(folder.read_text())
    body["cases"][0]["debtors"]["debtor_1"]["provenance"] = {}
    folder.write_text(json.dumps(body))

    with pytest.raises(Exception, match="provenance"):
        env.load()


# ── publish ─────────────────────────────────────────────────────────


def test_publish_uploads_only_what_the_bucket_lacks(tmp_path: Path) -> None:
    env = Env(tmp_path)
    env.objects.objects.clear()
    argv = [
        "publish",
        "--version",
        str(tmp_path / "fixtures" / "v1"),
        "--fixture-bucket",
        FIXTURE_BUCKET,
    ]

    assert main([*argv, "--check"], deps=env.deps()) == 1
    assert env.objects.objects == {}

    assert main(argv, deps=env.deps()) == 0
    assert env.objects.objects["v1/objects/stub.pdf"] == PDF
    assert "v1/manifest.json" in env.objects.objects
    assert main([*argv, "--check"], deps=env.deps()) == 0


def test_publish_refuses_a_file_that_disagrees_with_its_manifest(
    tmp_path: Path,
) -> None:
    env = Env(tmp_path)
    (tmp_path / "fixtures" / "v1" / "objects" / "stub.pdf").write_bytes(
        b"not what the manifest says"
    )
    argv = [
        "publish",
        "--version",
        str(tmp_path / "fixtures" / "v1"),
        "--fixture-bucket",
        FIXTURE_BUCKET,
    ]

    assert main(argv, deps=env.deps()) == 2


def test_publish_refuses_a_bucket_outside_the_fixture_family(tmp_path: Path) -> None:
    env = Env(tmp_path)
    argv = [
        "publish",
        "--version",
        str(tmp_path / "fixtures" / "v1"),
        "--fixture-bucket",
        DEV_BUCKET,
    ]

    assert main(argv, deps=env.deps()) == 2


# ── capture ─────────────────────────────────────────────────────────


def test_capture_round_trips_a_loaded_case_into_a_new_version(tmp_path: Path) -> None:
    env = Env(tmp_path)
    assert env.load() == 0
    case = env.the_case()
    assert case is not None
    target = tmp_path / "fixtures" / "v2"

    status = main(
        [
            "capture",
            "--version",
            str(target),
            "--case",
            case.id,
            "--handle",
            "captured",
            "--case-table",
            DEV_CASE_TABLE,
            "--document-bucket",
            DEV_BUCKET,
        ],
        deps=env.deps(),
    )

    assert status == 0
    written = json.loads((target / "cases.json").read_text())
    spec = written["cases"][0]
    assert spec["handle"] == "captured"
    assert spec["debtors"]["debtor_1"]["name"] == {"given": "Sample"}
    assert "id" not in spec["debtors"]["debtor_1"]
    assert spec["collections"]["creditors"][0]["name"] == "Example Bank"
    assert (target / spec["documents"][0]["object"]).read_bytes() == PDF
    manifest = json.loads((target / "manifest.json").read_text())
    assert manifest["objects"][spec["documents"][0]["object"]]["size"] == len(PDF)


def test_capture_refuses_a_staging_source(tmp_path: Path) -> None:
    env = Env(tmp_path)
    status = main(
        [
            "capture",
            "--version",
            str(tmp_path / "fixtures" / "v2"),
            "--case",
            "any",
            "--handle",
            "x",
            "--case-table",
            "insolvia-staging-cases",
            "--document-bucket",
            "insolvia-staging-case-documents-us-east-1",
        ],
        deps=env.deps(),
    )
    assert status == 2
