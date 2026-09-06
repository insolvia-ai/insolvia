"""Load a seed fixture into an environment's data stores.

## Why anything outside the API writes rows at all

`core/firms.py` gave the service a tenancy model, and every route behind
`current_accessor()` answers 403 until the caller resolves to an active user of
an active firm. Nothing creates that first pair, and nothing can:
`POST /v1/firm/users` is itself behind `FIRM_ADMINISTRATION`, so it needs an
admin to add an admin; self-signup is off on every pool
(`allow_admin_create_user_only`); and ADR 0009 refuses any edit that would leave
a firm without an active administrator. All three are correct, and together they
mean the first firm has to be written from OUTSIDE the API.

That is true of a laptop and of staging alike. A developer hits it as "I signed
in and everything 403s"; CI hits it as `intake-persists.spec.ts` failing on a
case list that can never populate.

## Why a fixture file rather than flags

Because the next thing to seed is cases, and the one after that is whatever the
test needs. A flag per field stops scaling at about six fields, and it puts the
shape of the data in a shell script — where nobody reviews it and nothing
validates it. A fixture is a reviewable artefact: `seeds/dev.json` is the answer
to "what is on a developer's machine", in one place, in a diff.

It also makes the environments comparable. Dev and staging differ only in which
fixture is loaded and which tables it is loaded into — not in which code path
built the rows, which is what would otherwise drift.

## Why the rows still go through core/

The item shapes live in `core/` precisely so the DynamoDB and in-memory stores
cannot drift apart. A loader that turned fixture JSON straight into
`{"PK": "FIRM#…"}` would be a THIRD writer of that shape and the one nobody
re-reads when a field is added. So the fixture is parsed by the same
`parse_firm_creation` / `parse_firm_user_creation` a route uses — which means a
malformed fixture fails with the same field errors the API would give, and a new
required field breaks seeding loudly instead of writing rows the service has
stopped agreeing with.

## Accounts as well as rows, and why that is the same job

A firm row is keyed on a Cognito `sub`, so seeding a firm means knowing who its
people are in the pool. A fixture person carrying a `password` says "this
environment owns its accounts" and the loader creates them; one without says
the account must already exist. Staging owns its accounts. Dev does not —
`dev-aws-seed.sh` (the shell wrapper) creates the dev account before invoking
this loader, from `~/.config/insolvia/dev.env` or an interactive prompt, and
no fixture should ever carry a password that a human typed.

Doing both here is what makes a second test user cost a fixture edit rather
than a script run, two secrets and a slot in `e2e/support/env.ts`. That matters
because the tenancy model is *about* multiple people: `may_see_case`,
`access_all_cases`, another firm's case answering 404, and the 409 that stops a
firm removing its last admin are all untestable with one account.

SUBJECTS ARE RESOLVED, NEVER PINNED. An earlier version stored the sub as a CI
secret to avoid granting `AdminGetUser`. That tied the secret to the pool's
lifetime — replace the pool and the sub changes, the secret rots in silence,
and seeding writes a firm for somebody who cannot sign in. One scoped grant
beats a stale secret per person.

## `${VAR}` in a fixture, and why it is not a convenience

THIS REPO IS PUBLIC, so nothing here may carry a credential. Passwords are
`${E2E_TEST_USER_PASSWORD}` and arrive from the environment at load time.
Expansion FAILS on an unset variable rather than substituting empty: a fixture
that set an account's password to "" would be worse than one that refused.

ADDRESSES, though, are committed on purpose. Every seeded address ends in
`@insolvia.test` — a reserved TLD (RFC 2606) that can never be a real mailbox,
which is what `e2e/CLAUDE.md`'s rule is protecting against. Nothing is emailed
to them anyway (`MessageAction=SUPPRESS`). Keeping them in the fixture is what
lets the e2e suite and the seeder agree on who exists by reading one file,
instead of drifting through two sets of secrets.

## WHAT THIS IS NOT: a unit-test fixture factory

pytest builds its own data through `core/` constructors and `adapters/memory/`
— no files, no AWS, no subprocess. Reaching for this module from a unit test
would trade an in-process object for a network round trip.

Its consumers are the two places that need real rows in real tables: a
developer machine (`scripts/dev-aws-seed.sh`) and the staging e2e run.

## Why it is dev/staging only

A tool whose target can be changed by one argument is a tool that eventually is
— the reasoning `dev-aws-seed.sh` records. `_require_seedable_table`
refuses anything that is not this machine's dev table or a staging table, and
prod is refused outright: provisioning a real customer firm wants an audit
trail and a review, which is #178 and is not a CLI.

## Cases, and the fixture bucket

The second entity, and the one that needed a bucket. A seeded case is rows
(the case, its assignment, debtors, collection items, document records) AND
bytes (the documents themselves), and the bytes have a home of their own:
`seeds/fixtures/<version>/` in git holds the reviewable half — `cases.json`
(what each case contains, in the API's own body shapes), `manifest.json`
(every object's size and digest) and the small synthetic objects themselves —
and `s3://insolvia-shared-dev-fixtures-<region>/<version>/` holds the copy the
loader actually reads (`infra/modules/dev_fixtures`). A load is a server-side
`copy_object` per document into the target's own case-documents bucket, so
neither a laptop nor a CI runner ever handles the bytes, and the day a fixture
is a realistic scan rather than a two-kilobyte PDF nothing here changes.

Three commands, and the direction each moves data:

    seed load     git fixture + fixture bucket  -->  an environment  (dev, staging)
    seed publish  git fixture                   -->  the fixture bucket
    seed capture  a DEV stack                   -->  git fixture (a new version)

IDS ARE DERIVED, NOT PUBLISHED. A case's id is `uuid5` over the target table,
the firm it lands in, the fixture version and the case's `handle`; a document's
and a collection item's over the case id and their own position. So two
environments seed different ids from one fixture, and a second run finds every
row it wrote last time instead of writing it twice — which is what makes
"seed on every staging deploy" a convergence rather than an accumulation.
Rows that exist are LEFT ALONE, even if the fixture has since changed: a
seeded environment is somewhere people work, and a re-seed that rewrote a
debtor under them would be the mystery regression this module exists to
prevent. A changed fixture is a new version.

`capture` refuses any source that is not a dev table, and that refusal is the
whole of hard rule "nothing real in a fixture": a dev stack holds nothing but
what a developer typed, so a fixture captured from one is synthetic by
construction. A reviewer of a `seeds/fixtures/` diff should still ask.

## Adding an entity

A `_seed_<entity>` function that takes its slice of the fixture and the stores
it needs, a key in the fixture schema, and a field on `Dependencies` so tests
can pass a memory adapter.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
import uuid
from collections.abc import Callable, Mapping
from dataclasses import dataclass, replace
from pathlib import Path
from typing import Any, Final, Protocol

from insolvia_core.adapters.aws.case_entity_store import DynamoDbCaseEntityStore
from insolvia_core.adapters.aws.case_store import DynamoDbCaseStore
from insolvia_core.adapters.aws.debtor_store import DynamoDbDebtorStore
from insolvia_core.adapters.aws.document_blobs import S3DocumentBlobStore
from insolvia_core.adapters.aws.document_store import DynamoDbDocumentStore
from insolvia_core.adapters.aws.firm_store import DynamoDbFirmStore
from insolvia_core.case_collections import COLLECTIONS
from insolvia_core.case_entities import create_entity, entity_json, parse_entity
from insolvia_core.cases import assign_case, create_case, parse_case_creation
from insolvia_core.debtors import create_debtor, debtor_json, parse_debtor
from insolvia_core.documents import (
    STATUS_STORED,
    confirm_document,
    create_document,
    object_key,
    parse_document_upload,
)
from insolvia_core.firms import (
    create_firm,
    create_firm_user,
    parse_firm_creation,
    parse_firm_user_creation,
)
from insolvia_core.ports import (
    CaseEntityStore,
    CaseStore,
    DebtorStore,
    DocumentBlobStore,
    DocumentStore,
    FirmStore,
)

# What infra/modules/* name a table, per environment: insolvia-<env>-<kind>,
# environment SECOND (the insolvia-aws-naming skill). Dev carries the machine
# short id from scripts/dev-aws-common.sh inside the env segment; staging is
# flat. Prod matches neither, which is the point.
_SEEDABLE_TABLE: Final = r"^insolvia-(dev-[0-9a-f]{{12}}|staging)-{kind}\Z"
# The case-documents bucket, per infra/modules/case_documents: the same env
# segment, then the component and the region suffix S3 names carry.
_SEEDABLE_BUCKET: Final = (
    r"^insolvia-(dev-[0-9a-f]{12}|staging)-case-documents-[a-z0-9-]+\Z"
)
# Only a dev stack may be captured FROM: what a developer typed is synthetic
# by construction, and nothing else is.
_CAPTURABLE_TABLE: Final = r"^insolvia-dev-[0-9a-f]{12}-cases\Z"
# The shared fixture bucket, infra/modules/dev_fixtures.
_FIXTURE_BUCKET: Final = r"^insolvia-shared-dev-fixtures-[a-z0-9-]+\Z"

# Derived ids hang off one namespace so a fixture row's id is a pure function
# of (target, firm, version, handle) — see the module docstring.
_SEED_NAMESPACE: Final = uuid.UUID("6f1c2b8e-3d4a-4f5b-9c6d-7e8f9a0b1c2d")

#: Where fixture versions live, relative to the environment fixture's folder.
_FIXTURES_DIR: Final = "fixtures"

_VAR: Final = re.compile(r"\$\{([A-Z_][A-Z0-9_]*)\}")

_OK: Final = 0
_NOT_SEEDED: Final = 1
_REFUSED: Final = 2


class RefusedError(Exception):
    """A guard or the fixture rejected the run. Nothing has been written."""


class Accounts(Protocol):
    """The pool half of seeding: make sure a person can sign in, and say who
    they are.

    Not `core.ports.UserDirectory`. That port is deliberately AdminCreateUser
    and NOTHING else — no password, no lookup — because it is the API's grant,
    and a service that could set a password would be an impersonation
    primitive rather than an invitation mechanism. This is a different
    principal with different needs, so widening that port to serve a seeder
    would quietly hand the API a capability it was designed not to have.

    `RefusedError` is part of the contract, not whatever an implementation
    happens to throw, so a fake that raises KeyError is a broken fake rather
    than a passing test.
    """

    def subject_of(self, email: str) -> str:
        """The Cognito sub, raising RefusedError if there is no such account."""
        ...

    def ensure(self, email: str, password: str) -> str:
        """Create the account if absent, then return its sub.

        Converges rather than creates: re-running must not fail on an account
        that is already there, because this runs on every staging deploy.
        """
        ...


class CognitoAccounts:
    """`Accounts` against a real pool.

    RESOLVED FRESH, NEVER PINNED. An earlier design stored the sub as a CI
    secret to avoid granting AdminGetUser. That coupled the secret to the
    pool's lifetime: replace the pool and the sub changes, the secret goes
    stale in silence, and seeding writes a firm for somebody who cannot sign
    in — which presents as the 403 this whole module exists to prevent, from a
    value nobody would suspect. One scoped grant is cheaper than that failure.
    """

    def __init__(self, pool_id: str) -> None:
        import boto3

        self.pool_id = pool_id
        self.client = boto3.client("cognito-idp")

    def _sub(self, user: dict[str, Any]) -> str:
        for attribute in user["UserAttributes"]:
            if attribute["Name"] == "sub":
                return str(attribute["Value"])
        raise RefusedError("pool user has no sub attribute")

    def subject_of(self, email: str) -> str:
        try:
            return self._sub(
                self.client.admin_get_user(UserPoolId=self.pool_id, Username=email)
            )
        except self.client.exceptions.UserNotFoundException:
            raise RefusedError(
                f"no pool user for {email}, and the fixture gives no password "
                "to create one with"
            ) from None

    def ensure(self, email: str, password: str) -> str:
        try:
            existing = self.client.admin_get_user(
                UserPoolId=self.pool_id, Username=email
            )
        except self.client.exceptions.UserNotFoundException:
            existing = None

        if existing is None:
            # SUPPRESS is not optional. Without it Cognito emails a temporary
            # password to the address; these are `.test` addresses that can
            # never receive it, and SES is still sandboxed, so the mail would
            # only ever bounce. email_verified spares the account a
            # verification step nothing can complete.
            self.client.admin_create_user(
                UserPoolId=self.pool_id,
                Username=email,
                UserAttributes=[
                    {"Name": "email", "Value": email},
                    {"Name": "email_verified", "Value": "true"},
                ],
                MessageAction="SUPPRESS",
            )

        # ALWAYS, even for an account that already existed. admin_create_user
        # leaves the user in FORCE_CHANGE_PASSWORD, and the hosted UI answers
        # that with a "set a new password" screen a browser test cannot
        # complete — it hangs until the job times out. Setting it every run
        # also makes a rotated password secret converge instead of drifting
        # away from the account it names.
        self.client.admin_set_user_password(
            UserPoolId=self.pool_id,
            Username=email,
            Password=password,
            Permanent=True,
        )
        return self._sub(
            self.client.admin_get_user(UserPoolId=self.pool_id, Username=email)
        )


class FixtureObjects(Protocol):
    """The bytes half of a fixture: the shared bucket's objects, copied into
    a target's own case-documents bucket without passing through here."""

    def copy(self, source_key: str, *, target_key: str, content_type: str) -> None:
        """Server-side copy `source_key` (in the fixture bucket) to
        `target_key` (in the target bucket), re-encrypted under the target's
        default key and carrying `content_type` as the object's own."""
        ...

    def put(self, key: str, *, content: bytes, content_type: str) -> None:
        """Write one object INTO the fixture bucket (publish)."""
        ...

    def digest(self, key: str) -> str | None:
        """sha256 of an object in the fixture bucket, or None if absent."""
        ...


class S3FixtureObjects:
    """`FixtureObjects` against the real shared bucket."""

    def __init__(self, fixture_bucket: str, target_bucket: str | None) -> None:
        import boto3

        self.fixture_bucket = fixture_bucket
        self.target_bucket = target_bucket
        self.client = boto3.client("s3")

    def copy(self, source_key: str, *, target_key: str, content_type: str) -> None:
        if self.target_bucket is None:
            raise RefusedError("no document bucket to copy into")
        # NO ENCRYPTION HEADERS, DELIBERATELY, and the first staging run is why.
        # `ServerSideEncryption="aws:kms"` WITHOUT a key id does not mean "the
        # bucket's default key": S3 fills in the AWS-managed `aws/s3` key, and
        # the target bucket's DenyForeignEncryptionKey statement
        # (modules/case_documents) then refuses the copy with an explicit
        # deny — which is exactly what happened. Sending no header at all is
        # the one shape that lands on the bucket's default encryption, the
        # case key, and the policy's DenyEncryptionDowngrade is written to
        # allow an absent header for precisely that reason. REPLACE so the
        # content type is the document's, not the fixture upload's.
        self.client.copy_object(
            Bucket=self.target_bucket,
            Key=target_key,
            CopySource={"Bucket": self.fixture_bucket, "Key": source_key},
            ContentType=content_type,
            MetadataDirective="REPLACE",
        )

    def put(self, key: str, *, content: bytes, content_type: str) -> None:
        self.client.put_object(
            Bucket=self.fixture_bucket,
            Key=key,
            Body=content,
            ContentType=content_type,
        )

    def digest(self, key: str) -> str | None:
        try:
            body = self.client.get_object(Bucket=self.fixture_bucket, Key=key)["Body"]
        except self.client.exceptions.NoSuchKey:
            return None
        return hashlib.sha256(body.read()).hexdigest()


@dataclass(frozen=True)
class Dependencies:
    """The seams tests replace. Factories, because neither a table name nor a
    pool id is legitimate until the guards below have accepted it."""

    firm_store: Callable[[str], FirmStore] = DynamoDbFirmStore
    accounts: Callable[[str], Accounts] = CognitoAccounts
    case_store: Callable[[str], CaseStore] = DynamoDbCaseStore
    debtor_store: Callable[[str], DebtorStore] = DynamoDbDebtorStore
    entity_store: Callable[[str], CaseEntityStore] = DynamoDbCaseEntityStore
    document_store: Callable[[str], DocumentStore] = DynamoDbDocumentStore
    document_blobs: Callable[[str], DocumentBlobStore] = S3DocumentBlobStore
    # (fixture bucket, target document bucket or None)
    fixture_objects: Callable[[str, str | None], FixtureObjects] = S3FixtureObjects


def _require_seedable_table(table: str, *, kind: str) -> None:
    if not re.match(_SEEDABLE_TABLE.format(kind=kind), table):
        raise RefusedError(
            f"'{table}' is not a seedable {kind} table (expected "
            f"insolvia-dev-<machine short id>-{kind} or insolvia-staging-{kind})"
        )


def _expand(value: str, env: Mapping[str, str]) -> str:
    def replace(match: re.Match[str]) -> str:
        name = match.group(1)
        found = env.get(name)
        if not found:
            raise RefusedError(
                f"fixture references ${{{name}}}, which is unset or empty. "
                "It has no default on purpose — this repo is public."
            )
        return found

    return _VAR.sub(replace, value)


def _expanded(node: Any, env: Mapping[str, str]) -> Any:
    if isinstance(node, str):
        return _expand(node, env)
    if isinstance(node, list):
        return [_expanded(item, env) for item in node]
    if isinstance(node, dict):
        return {key: _expanded(item, env) for key, item in node.items()}
    return node


def load_fixture(path: Path, env: Mapping[str, str]) -> dict[str, Any]:
    try:
        raw = json.loads(path.read_text())
    except FileNotFoundError:
        raise RefusedError(f"no fixture at {path}") from None
    except json.JSONDecodeError as error:
        raise RefusedError(f"{path} is not valid JSON: {error}") from None
    if not isinstance(raw, dict):
        raise RefusedError(f"{path} must be a JSON object")
    return dict(_expanded(raw, env))


# ── firms ───────────────────────────────────────────────────────────


def _subject_for(user: Mapping[str, Any], accounts: Accounts, *, check: bool) -> str:
    """The pool sub for one fixture person, creating the account if asked to.

    A `password` in the fixture is what says "this environment owns its
    accounts, make them". Without one the account must already exist — which is
    how dev works, where the `dev-aws-seed.sh` wrapper creates the account
    before this loader runs and no fixture should ever carry a human's
    password.

    Under --check nothing is created; an absent account reports as missing
    rather than being provisioned by a command whose whole promise is that it
    writes nothing.
    """
    email = str(user["email"])
    password = user.get("password")
    if not password or check:
        return accounts.subject_of(email)
    return accounts.ensure(email, str(password))


def _seed_firms(
    entries: list[Any], store: FirmStore, accounts: Accounts, *, check: bool
) -> int:
    """Converge each fixture firm. Returns 0 if nothing is missing.

    IDEMPOTENT, and per USER rather than per firm, because a firm has no
    natural key — its id is a uuid minted at creation, so "is this firm already
    here?" can only be answered through the people in it. A fixture that grows a
    colleague therefore adds that colleague to the existing firm instead of
    creating a second one beside it.
    """
    missing = 0
    for entry in entries:
        if not isinstance(entry, dict):
            raise RefusedError("each firm in the fixture must be an object")
        users = entry.get("users") or []
        if not users:
            raise RefusedError(
                f"firm '{entry.get('name')}' has no users — a firm nobody can "
                "sign in to is the derelict state ADR 0009 refuses"
            )

        # Accounts first, and all of them, before a single row is written. A
        # fixture naming somebody who cannot be provisioned should fail while
        # the table is still untouched, rather than half a firm in.
        drafts = [
            (parse_firm_user_creation(user), _subject_for(user, accounts, check=check))
            for user in users
        ]

        placed = {
            subject: found
            for _, subject in drafts
            if (found := store.find_user(subject)) is not None
        }
        firm_ids = {user.firm_id for user in placed.values()}
        if len(firm_ids) > 1:
            raise RefusedError(
                f"the people in '{entry.get('name')}' are already split across "
                f"{len(firm_ids)} firms; refusing to guess which one is meant"
            )

        absent = [(draft, s) for draft, s in drafts if s not in placed]
        if not absent:
            print(f"firm '{entry.get('name')}': already seeded")
            continue

        missing += len(absent)
        if check:
            print(f"firm '{entry.get('name')}': {len(absent)} user(s) missing")
            continue

        if firm_ids:
            firm_id = firm_ids.pop()
            print(f"firm '{entry.get('name')}': adding {len(absent)} user(s)")
        else:
            firm = create_firm(parse_firm_creation({"name": entry.get("name")}))
            store.create_firm(firm)
            firm_id = firm.id
            print(f"created firm {firm.name} ({firm.id})")

        for draft, subject in absent:
            store.add_user(create_firm_user(draft, firm_id=firm_id, subject=subject))
            print(f"  added {draft.email} ({draft.role}, admin={draft.is_admin})")

    return missing


# ── cases ───────────────────────────────────────────────────────────


@dataclass(frozen=True)
class CaseStores:
    """Everything a seeded case lands in. Built only once the guards have
    accepted the table and the bucket, which is why it is not `Dependencies`."""

    cases: CaseStore
    debtors: DebtorStore
    entities: CaseEntityStore
    documents: DocumentStore
    blobs: DocumentBlobStore
    objects: FixtureObjects


def derived_id(*parts: str) -> str:
    """A uuid that is a pure function of its parts — see the module docstring."""
    return str(uuid.uuid5(_SEED_NAMESPACE, "/".join(parts)))


def _fixture_version(
    env_fixture: Path, version: str
) -> tuple[dict[str, Any], dict[str, Any]]:
    """`cases.json` and `manifest.json` for one committed fixture version."""
    if not re.match(r"^v[0-9]+\Z", version):
        raise RefusedError(f"fixture version {version!r} must look like v1")
    folder = env_fixture.resolve().parent / _FIXTURES_DIR / version
    cases = load_fixture(folder / "cases.json", {})
    manifest = load_fixture(folder / "manifest.json", {})
    if cases.get("version") != version or manifest.get("version") != version:
        raise RefusedError(f"{folder} does not declare itself as version {version}")
    return cases, manifest


def _firm_and_subject(
    entry: Mapping[str, Any],
    firms: list[Any],
    store: FirmStore,
    accounts: Accounts,
    *,
    check: bool,
) -> tuple[str, str] | None:
    """The firm a fixture case lands in and the person who opened it — both
    resolved through the SEEDED rows, so a case can only ever belong to a
    firm this fixture describes and a person in it.

    None under --check when the person is not in the table yet: on a fresh
    environment the firms are missing too, and a report should say so rather
    than refuse."""
    firm_name = entry.get("firm")
    handle = entry.get("openedBy")
    for firm in firms:
        if not isinstance(firm, dict) or firm.get("name") != firm_name:
            continue
        for user in firm.get("users") or []:
            if user.get("handle") == handle:
                if check:
                    try:
                        subject = accounts.subject_of(str(user["email"]))
                    except RefusedError:
                        return None
                else:
                    subject = accounts.subject_of(str(user["email"]))
                placed = store.find_user(subject)
                if placed is None and check:
                    return None
                if placed is None:
                    raise RefusedError(
                        f"'{handle}' is not yet in the firm table — firms seed "
                        "before cases, so this is a missing account"
                    )
                return placed.firm_id, subject
        raise RefusedError(f"firm '{firm_name}' has nobody with handle '{handle}'")
    raise RefusedError(
        f"case fixture names firm '{firm_name}', which this fixture lacks"
    )


def _seed_one_case(
    spec: Mapping[str, Any],
    manifest: Mapping[str, Any],
    version: str,
    *,
    case_table: str,
    firm_id: str,
    created_by: str,
    stores: CaseStores,
    check: bool,
) -> int:
    """Converge one fixture case. Returns how many things were missing."""
    handle = str(spec.get("handle") or "")
    if not handle:
        raise RefusedError("every fixture case needs a handle")
    case_id = derived_id(case_table, firm_id, version, handle)
    missing = 0

    # The case itself: parsed by the route's own parser, stamped by the same
    # factory, and then given its derived id and a matching assignment.
    if stores.cases.read_for_worker(case_id) is None:
        missing += 1
        if not check:
            draft = parse_case_creation(
                {"chapter": spec.get("chapter"), "district": spec.get("district")}
            )
            minted, _ = create_case(draft, firm_id=firm_id, created_by=created_by)
            case = replace(minted, id=case_id)
            stores.cases.create(
                case, assign_case(case, subject=created_by, assigned_by=created_by)
            )
            print(f"  case '{handle}': created ({case_id})")
    else:
        print(f"  case '{handle}': present")

    # Debtors, keyed by filing role — the API's natural key for them.
    debtors = spec.get("debtors") or {}
    if not isinstance(debtors, dict):
        raise RefusedError(f"case '{handle}': debtors must be an object keyed by role")
    for role, body in debtors.items():
        debtor_draft = parse_debtor(body)
        if stores.debtors.get(case_id, filing_role=role) is not None:
            continue
        missing += 1
        if not check:
            stores.debtors.create(
                create_debtor(debtor_draft, case_id=case_id, filing_role=role)
            )
            print(f"    debtor {role}: created")

    # Collection items, in fixture order, each with an id derived from its
    # position so a re-run finds it rather than adding a twin.
    collections = spec.get("collections") or {}
    if not isinstance(collections, dict):
        raise RefusedError(f"case '{handle}': collections must be an object")
    for name, items in collections.items():
        kind = COLLECTIONS.get(str(name))
        if kind is None:
            raise RefusedError(f"case '{handle}': unknown collection '{name}'")
        for index, body in enumerate(items or []):
            entity_draft = parse_entity(kind, body)
            entity_id = derived_id(case_id, str(name), str(index))
            if stores.entities.get(case_id, kind, entity_id) is not None:
                continue
            missing += 1
            if not check:
                minted_entity = create_entity(kind, entity_draft, case_id=case_id)
                stores.entities.create(replace(minted_entity, id=entity_id))
                print(f"    {name}[{index}]: created")

    # Documents: the row AND the bytes. The bytes go first, server-side, and
    # the row is written `stored` only once HeadObject has seen them — the
    # same order and the same confirmation the API's complete route uses.
    for document in spec.get("documents") or []:
        source = str(document.get("object") or "")
        listed = (manifest.get("objects") or {}).get(source)
        if not listed:
            raise RefusedError(
                f"case '{handle}': document object {source!r} is not in manifest.json"
            )
        document_id = derived_id(case_id, "document", source)
        existing = stores.documents.get(case_id, document_id)
        if existing is not None and existing.status == STATUS_STORED:
            continue
        missing += 1
        if check:
            continue
        document_draft = parse_document_upload(
            {
                "kind": document.get("kind"),
                "fileName": document.get("fileName"),
                "contentType": document.get("contentType"),
                "byteSize": listed.get("size"),
            }
        )
        storage_ref = object_key(case_id, document_id)
        stores.objects.copy(
            f"{version}/{source}",
            target_key=storage_ref,
            content_type=document_draft.content_type,
        )
        blob = stores.blobs.stat(storage_ref)
        if blob is None:
            raise RefusedError(f"case '{handle}': the copy of {source} did not land")
        if blob.byte_size != listed.get("size"):
            raise RefusedError(
                f"case '{handle}': {source} landed at {blob.byte_size} bytes, "
                f"manifest says {listed.get('size')}"
            )
        minted_document = create_document(
            document_draft, case_id=case_id, uploaded_by=created_by
        )
        stored = confirm_document(
            replace(minted_document, id=document_id, storage_ref=storage_ref), blob
        )
        if existing is None:
            stores.documents.create(stored)
        else:
            stores.documents.update(stored)
        print(f"    document {document.get('fileName')}: stored")

    return missing


def _seed_cases(
    entries: list[Any],
    fixture: Mapping[str, Any],
    env_fixture: Path,
    *,
    case_table: str,
    firm_store: FirmStore,
    accounts: Accounts,
    stores: CaseStores,
    check: bool,
) -> int:
    missing = 0
    versions: dict[str, tuple[dict[str, Any], dict[str, Any]]] = {}
    for entry in entries:
        if not isinstance(entry, dict):
            raise RefusedError("each case in the fixture must be an object")
        version = str(entry.get("fixture") or "")
        if version not in versions:
            versions[version] = _fixture_version(env_fixture, version)
        cases, manifest = versions[version]
        wanted = str(entry.get("case") or "")
        spec = next(
            (c for c in cases.get("cases") or [] if c.get("handle") == wanted), None
        )
        if spec is None:
            raise RefusedError(f"fixture {version} has no case with handle '{wanted}'")
        resolved = _firm_and_subject(
            entry, list(fixture.get("firms") or []), firm_store, accounts, check=check
        )
        if resolved is None:
            print(f"  case '{wanted}': its firm is not seeded yet")
            missing += 1
            continue
        firm_id, subject = resolved
        missing += _seed_one_case(
            spec,
            manifest,
            version,
            case_table=case_table,
            firm_id=firm_id,
            created_by=subject,
            stores=stores,
            check=check,
        )
    return missing


# ── publish: git -> the fixture bucket ──────────────────────────────


def _require_fixture_bucket(bucket: str) -> None:
    if not re.match(_FIXTURE_BUCKET, bucket):
        raise RefusedError(
            f"'{bucket}' is not the shared fixture bucket "
            "(expected insolvia-shared-dev-fixtures-<region>)"
        )


def publish(folder: Path, objects: FixtureObjects, *, check: bool) -> int:
    """Put one committed fixture version into the bucket, byte for byte.

    Idempotent by digest: an object already there with the manifest's
    sha256 is left alone, and a local file that does NOT match the manifest
    is refused before anything uploads — the manifest is the contract the
    loader verifies against, so publishing bytes it disagrees with would be
    publishing a fixture that can never load.
    """
    version = folder.name
    cases = load_fixture(folder / "cases.json", {})
    manifest = load_fixture(folder / "manifest.json", {})
    if cases.get("version") != version or manifest.get("version") != version:
        raise RefusedError(f"{folder} does not declare itself as version {version}")

    missing = 0
    for key, listed in (manifest.get("objects") or {}).items():
        local = folder / key
        if not local.is_file():
            raise RefusedError(f"manifest lists {key} but {local} is absent")
        content = local.read_bytes()
        digest = hashlib.sha256(content).hexdigest()
        if digest != listed.get("sha256") or len(content) != listed.get("size"):
            raise RefusedError(
                f"{key} does not match manifest.json — regenerate the manifest "
                "rather than publishing bytes the loader will refuse"
            )
        if objects.digest(f"{version}/{key}") == digest:
            print(f"{version}/{key}: up to date")
            continue
        missing += 1
        if check:
            print(f"{version}/{key}: would upload")
            continue
        objects.put(
            f"{version}/{key}", content=content, content_type="application/octet-stream"
        )
        print(f"{version}/{key}: uploaded")
    for name in ("cases.json", "manifest.json"):
        if not check:
            objects.put(
                f"{version}/{name}",
                content=(folder / name).read_bytes(),
                content_type="application/json",
            )
    return missing


# ── capture: a DEV stack -> a git fixture version ───────────────────

_SERVER_OWNED = ("id", "case_id", "filing_role", "created_at", "updated_at")


def _strip(body: Mapping[str, object]) -> dict[str, object]:
    return {k: v for k, v in body.items() if k not in _SERVER_OWNED}


def capture(
    folder: Path,
    *,
    case_id: str,
    handle: str,
    case_table: str,
    stores: CaseStores,
    replace_existing: bool,
) -> None:
    """Write one dev-stack case into `folder` as (part of) a fixture version.

    Everything comes back out through the same core/ serialisers the API
    uses, minus the server-owned fields — the same shape `load` parses —
    so a captured case round-trips by construction.
    """
    if not re.match(_CAPTURABLE_TABLE, case_table):
        raise RefusedError(
            f"'{case_table}' is not a dev case table — only a dev stack "
            "may be captured from"
        )
    if not re.match(r"^[a-z0-9-]{1,60}\Z", handle):
        raise RefusedError("a handle is lowercase letters, digits and hyphens")
    case = stores.cases.read_for_worker(case_id)
    if case is None:
        raise RefusedError(f"no case {case_id} in {case_table}")

    version = folder.name
    cases_path = folder / "cases.json"
    manifest_path = folder / "manifest.json"
    cases: dict[str, Any] = (
        load_fixture(cases_path, {})
        if cases_path.exists()
        else {"version": version, "cases": []}
    )
    manifest: dict[str, Any] = (
        load_fixture(manifest_path, {})
        if manifest_path.exists()
        else {"version": version, "objects": {}}
    )
    existing = [c for c in cases.get("cases") or [] if c.get("handle") == handle]
    if existing and not replace_existing:
        raise RefusedError(f"{version} already has a case '{handle}' (pass --replace)")

    spec: dict[str, Any] = {
        "handle": handle,
        "chapter": case.chapter,
        "district": case.district,
        "debtors": {
            debtor.filing_role: _strip(debtor_json(debtor))
            for debtor in stores.debtors.list_for_case(case_id)
        },
        "collections": {},
        "documents": [],
    }
    for name, kind in COLLECTIONS.items():
        items = [
            _strip(entity_json(e)) for e in stores.entities.list_for_case(case_id, kind)
        ]
        if items:
            spec["collections"][name] = items

    (folder / "objects").mkdir(parents=True, exist_ok=True)
    for document in stores.documents.list_for_case(case_id):
        if document.status != STATUS_STORED:
            continue
        content = stores.blobs.get_bytes(document.storage_ref)
        if content is None:
            raise RefusedError(f"document {document.id} has no bytes in the bucket")
        key = f"objects/{handle}-{document.file_name}"
        (folder / key).write_bytes(content)
        manifest.setdefault("objects", {})[key] = {
            "size": len(content),
            "sha256": hashlib.sha256(content).hexdigest(),
        }
        spec["documents"].append(
            {
                "handle": document.id,
                "kind": document.kind,
                "fileName": document.file_name,
                "contentType": document.content_type,
                "object": key,
            }
        )

    kept = [c for c in cases.get("cases") or [] if c.get("handle") != handle]
    cases["cases"] = [*kept, spec]
    cases["version"] = version
    manifest["version"] = version
    cases_path.write_text(json.dumps(cases, indent=2) + "\n")
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n")
    print(f"captured '{handle}' into {folder}")


# ── plumbing ────────────────────────────────────────────────────────


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="seed",
        description=(
            "Load, publish or capture a seed fixture. Dev and staging only, never prod."
        ),
    )
    commands = parser.add_subparsers(dest="command", required=True)

    load = commands.add_parser("load", help="converge an environment on a fixture")
    load.add_argument("--fixture", required=True, type=Path)
    load.add_argument("--firm-table", required=True)
    load.add_argument(
        "--user-pool-id", required=True, help="the pool the fixture's people live in"
    )
    load.add_argument("--case-table", help="required when the fixture names cases")
    load.add_argument(
        "--document-bucket", help="required when a fixture case carries documents"
    )
    load.add_argument(
        "--fixture-bucket", help="required when a fixture case carries documents"
    )
    load.add_argument(
        "--check", action="store_true", help="report what is missing; write nothing"
    )

    pub = commands.add_parser(
        "publish", help="put a committed fixture version into the bucket"
    )
    pub.add_argument(
        "--version", required=True, type=Path, help="seeds/fixtures/<version>"
    )
    pub.add_argument("--fixture-bucket", required=True)
    pub.add_argument("--check", action="store_true")

    cap = commands.add_parser(
        "capture", help="write a dev-stack case into a fixture version"
    )
    cap.add_argument(
        "--version", required=True, type=Path, help="seeds/fixtures/<version>"
    )
    cap.add_argument(
        "--case", required=True, help="the case id in this machine's dev table"
    )
    cap.add_argument(
        "--handle", required=True, help="the fixture handle to write it as"
    )
    cap.add_argument("--case-table", required=True)
    cap.add_argument("--document-bucket", required=True)
    cap.add_argument("--replace", action="store_true")
    return parser


def _case_stores(
    args: argparse.Namespace, deps: Dependencies, *, fixture_bucket: str | None
) -> CaseStores:
    _require_seedable_table(args.case_table, kind="cases")
    bucket = args.document_bucket
    if bucket is not None and not re.match(_SEEDABLE_BUCKET, bucket):
        raise RefusedError(
            f"'{bucket}' is not a seedable document bucket (expected "
            "insolvia-dev-<machine short id>-case-documents-<region> or "
            "insolvia-staging-case-documents-<region>)"
        )
    if fixture_bucket is not None:
        _require_fixture_bucket(fixture_bucket)
    return CaseStores(
        cases=deps.case_store(args.case_table),
        debtors=deps.debtor_store(args.case_table),
        entities=deps.entity_store(args.case_table),
        documents=deps.document_store(args.case_table),
        blobs=deps.document_blobs(bucket or ""),
        objects=deps.fixture_objects(fixture_bucket or "", bucket),
    )


def _load(args: argparse.Namespace, deps: Dependencies) -> int:
    _require_seedable_table(args.firm_table, kind="firms")
    fixture = load_fixture(args.fixture, os.environ)
    firm_store = deps.firm_store(args.firm_table)
    accounts = deps.accounts(args.user_pool_id)
    missing = _seed_firms(
        fixture.get("firms") or [], firm_store, accounts, check=args.check
    )
    entries = list(fixture.get("cases") or [])
    if entries:
        if not args.case_table:
            raise RefusedError("the fixture names cases; --case-table is required")
        needs_bytes = any(
            spec.get("documents")
            for entry in entries
            if isinstance(entry, dict)
            for spec in _fixture_version(args.fixture, str(entry.get("fixture") or ""))[
                0
            ].get("cases")
            or []
            if spec.get("handle") == entry.get("case")
        )
        if needs_bytes and not (args.document_bucket and args.fixture_bucket):
            raise RefusedError(
                "a fixture case carries documents; --document-bucket and "
                "--fixture-bucket are both required"
            )
        stores = _case_stores(args, deps, fixture_bucket=args.fixture_bucket)
        missing += _seed_cases(
            entries,
            fixture,
            args.fixture,
            case_table=args.case_table,
            firm_store=firm_store,
            accounts=accounts,
            stores=stores,
            check=args.check,
        )
    if args.check and missing:
        return _NOT_SEEDED
    return _OK


def main(argv: list[str] | None = None, *, deps: Dependencies | None = None) -> int:
    args = _build_parser().parse_args(argv)
    dependencies = deps or Dependencies()
    try:
        if args.command == "load":
            return _load(args, dependencies)
        if args.command == "publish":
            _require_fixture_bucket(args.fixture_bucket)
            objects = dependencies.fixture_objects(args.fixture_bucket, None)
            missing = publish(args.version, objects, check=args.check)
            return _NOT_SEEDED if args.check and missing else _OK
        if args.command == "capture":
            stores = _case_stores(args, dependencies, fixture_bucket=None)
            capture(
                args.version,
                case_id=args.case,
                handle=args.handle,
                case_table=args.case_table,
                stores=stores,
                replace_existing=args.replace,
            )
            return _OK
    except RefusedError as refusal:
        print(f"refusing: {refusal}", file=sys.stderr)
        return _REFUSED
    return _OK


if __name__ == "__main__":
    raise SystemExit(main())
