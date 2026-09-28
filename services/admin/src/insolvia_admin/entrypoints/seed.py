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
row it wrote last time instead of writing it twice (a debtor's id is derived
too, over the case id and its filing role — see "references" below for
why) — which is what makes "seed on every staging deploy" a convergence
rather than an accumulation.
Rows that exist are LEFT ALONE, even if the fixture has since changed: a
seeded environment is somewhere people work, and a re-seed that rewrote a
debtor under them would be the mystery regression this module exists to
prevent. A changed fixture is a new version.

`capture` refuses any source that is not a dev table, and that refusal is the
whole of hard rule "nothing real in a fixture": a dev stack holds nothing but
what a developer typed, so a fixture captured from one is synthetic by
construction. A reviewer of a `seeds/fixtures/` diff should still ask.

## Clients (ADR 0023)

A fixture case may name CLIENTS — debtors bound to it for the client portal,
in the invitation route's own body shape (`email`, `displayName`, `roles`)
plus a `handle` and, where the environment owns its accounts, a `password`.
The account is converged exactly as a firm person's is (`_subject_for`); the
binding is parsed by `parse_invitation`, planned by `plan_binding` and written
by the same ClientBindingStore the API composes, so a fixture client is
indistinguishable from one a firm invited — except that no invitation mail is
sent: the password is set here, which is what lets the integration tier sign
the client in over SRP against the portal's app client. A binding that
exists is left alone, revoked or not, for the "rows that exist" reason above.
A client's address may not also be a firm person's: a subject is staff or a
client, never both.

## Firm clients (ADR 0022), which are not those clients

A fixture VERSION (v4 on) carries a top-level `clients` list: the firm's
directory entries, `POST /v1/firm/clients` bodies plus a `handle`. A debtor
names one with `"client_id": {"$ref": "clients/<handle>"}` — the same
reference mechanism as a claim naming its creditor — and Debtor 1 and
Debtor 2 MUST, because a case is opened for a client; a fixture that does
not is refused before anything is written (so v1 to v3 no longer load: their
rows are what `purge_pre_client` deletes). A client's id is derived from
(firm table, firm, version, handle); the loader writes the clients a case
names before the case, then the case, its assignment and its client debtors
in `CaseStore.create`'s one transaction, exactly as `POST /v1/cases` does.
`capture` writes the clients back out, given `--firm-table`.

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
from insolvia_core.adapters.aws.client_binding_store import DynamoDbClientBindingStore
from insolvia_core.adapters.aws.debtor_store import DynamoDbDebtorStore
from insolvia_core.adapters.aws.document_blobs import S3DocumentBlobStore
from insolvia_core.adapters.aws.document_store import DynamoDbDocumentStore
from insolvia_core.adapters.aws.firm_store import DynamoDbFirmStore
from insolvia_core.adapters.aws.tax_id_cipher import KmsTaxIdCipher, case_key_alias
from insolvia_core.adapters.aws.tax_id_store import DynamoDbTaxIdStore
from insolvia_core.case_collections import COLLECTIONS
from insolvia_core.case_entities import create_entity, entity_json, parse_entity
from insolvia_core.cases import assign_case, create_case, parse_case_creation
from insolvia_core.clients import create_binding, parse_invitation, plan_binding
from insolvia_core.debtors import Debtor, create_debtor, debtor_json, parse_debtor
from insolvia_core.documents import (
    STATUS_STORED,
    confirm_document,
    create_document,
    object_key,
    parse_document_upload,
)
from insolvia_core.errors import ConflictError
from insolvia_core.firm_clients import (
    create_firm_client,
    firm_client_json,
    parse_firm_client,
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
    ClientBindingStore,
    DebtorStore,
    DocumentBlobStore,
    DocumentStore,
    FirmStore,
    TaxIdCipher,
    TaxIdStore,
)
from insolvia_core.tax_ids import store_tax_id

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
    # The debtor's tax id (issue 13.12 / #382): a fixture debtor's number is
    # sealed exactly as the API seals one — same store, same cipher, same
    # encryption context — under the target's case key, whose alias is
    # derived from the case table's name. Both factories take that name.
    tax_id_store: Callable[[str], TaxIdStore] = DynamoDbTaxIdStore
    tax_id_cipher: Callable[[str], TaxIdCipher] = lambda table: KmsTaxIdCipher(
        case_key_alias(table)
    )
    # (firm table, case table): a binding spans both (insolvia_core.clients).
    client_binding_store: Callable[[str, str], ClientBindingStore] = (
        DynamoDbClientBindingStore
    )


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
    tax_ids: TaxIdStore
    tax_id_cipher: TaxIdCipher


def derived_id(*parts: str) -> str:
    """A uuid that is a pure function of its parts — see the module docstring."""
    return str(uuid.uuid5(_SEED_NAMESPACE, "/".join(parts)))


# ── references between a case's own records ────────────────────────
#
# A case is a graph, not a list: a claim names its creditor and its
# collateral (`creditor_id`, `asset_id`), a plan names the claims it treats,
# an income summary names its debtor column. Those are ids — and a fixture
# publishes NO ids (every one is derived per target), so a fixture cannot
# write one down. It writes a REFERENCE instead:
#
#     {"$handle": "car-loan", ...}         on the collection item referred TO
#     {"$ref": "claims/car-loan"}          wherever an id of it is wanted
#     {"$ref": "debtors/debtor_1"}         a debtor, by filing role
#
# and the loader swaps each `$ref` for the id that record has (or will have)
# in THIS target before the route's own parser sees the body — so the parser
# still validates exactly what the API would have been sent. `capture` writes
# the same shapes back out, which is what makes a captured case with a plan
# on it loadable anywhere.

_REF: Final = "$ref"
_HANDLE: Final = "$handle"
_DEBTORS: Final = "debtors"
# A firm client (ADR 0022) — `{"$ref": "clients/<handle>"}` names one of the
# version's top-level `clients`, by its `handle`.
_CLIENTS: Final = "clients"
# The filing roles that are always a firm client; a non-filing spouse may be.
_CLIENT_ROLES: Final = ("debtor_1", "debtor_2")


def _debtor_id(case_id: str, role: str) -> str:
    """The id a seeded debtor gets — derived like every other seeded row's,
    so a reference to it can be resolved before it is written."""
    return derived_id(case_id, "debtor", role)


def _client_id(firm_table: str, firm_id: str, version: str, handle: str) -> str:
    """A seeded firm client's id — derived over (target table, firm,
    version, handle) exactly as a case's is (ADR 0022), so a second load
    finds the client it wrote and a `$ref` resolves before it exists."""
    return derived_id(firm_table, firm_id, version, _CLIENTS, handle)


def _version_clients(handle: str, cases: Mapping[str, Any]) -> dict[str, Any]:
    """The version's top-level `clients`, keyed by handle — refused on a
    missing or duplicate handle."""
    found: dict[str, Any] = {}
    for entry in cases.get(_CLIENTS) or []:
        client_handle = entry.get("handle") if isinstance(entry, dict) else None
        if not isinstance(client_handle, str) or not client_handle:
            raise RefusedError(f"case '{handle}': every fixture client needs a handle")
        if client_handle in found:
            raise RefusedError(
                f"case '{handle}': two fixture clients have handle '{client_handle}'"
            )
        found[client_handle] = entry
    return found


def _collection_handles(
    handle: str, collections: Mapping[str, Any]
) -> dict[str, dict[str, int]]:
    """{collection: {item $handle: position}} — refused on a duplicate."""
    found: dict[str, dict[str, int]] = {}
    for name, items in collections.items():
        for index, body in enumerate(items or []):
            item_handle = body.get(_HANDLE) if isinstance(body, dict) else None
            if item_handle is None:
                continue
            seen = found.setdefault(str(name), {})
            if str(item_handle) in seen:
                raise RefusedError(
                    f"case '{handle}': {name} has two items with $handle "
                    f"'{item_handle}'"
                )
            seen[str(item_handle)] = index
    return found


def _resolved(
    node: Any,
    *,
    handle: str,
    case_id: str,
    handles: Mapping[str, Mapping[str, int]],
    debtor_ids: Mapping[str, str],
    client_ids: Mapping[str, str],
) -> Any:
    """`node` with every `{"$ref": …}` replaced by the id it names in this
    target, and every `$handle` dropped — a body the API's parser accepts."""
    if isinstance(node, list):
        return [
            _resolved(
                item,
                handle=handle,
                case_id=case_id,
                handles=handles,
                debtor_ids=debtor_ids,
                client_ids=client_ids,
            )
            for item in node
        ]
    if not isinstance(node, dict):
        return node
    if _REF in node:
        target = node[_REF]
        if len(node) != 1 or not isinstance(target, str) or "/" not in target:
            raise RefusedError(
                f'case \'{handle}\': a reference is {{"$ref": "<collection>/'
                f'<handle>"}} and nothing else, not {node!r}'
            )
        collection, _, name = target.partition("/")
        if collection == _CLIENTS:
            if name not in client_ids:
                raise RefusedError(
                    f"case '{handle}': $ref {target!r} names no fixture client"
                )
            return client_ids[name]
        if collection == _DEBTORS:
            if name not in debtor_ids:
                raise RefusedError(
                    f"case '{handle}': $ref {target!r} names no fixture debtor"
                )
            return debtor_ids[name]
        position = (handles.get(collection) or {}).get(name)
        if position is None:
            raise RefusedError(
                f"case '{handle}': $ref {target!r} names no item with that $handle"
            )
        return derived_id(case_id, collection, str(position))
    return {
        key: _resolved(
            value,
            handle=handle,
            case_id=case_id,
            handles=handles,
            debtor_ids=debtor_ids,
            client_ids=client_ids,
        )
        for key, value in node.items()
        if key != _HANDLE
    }


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
    clients: Mapping[str, Any],
    case_table: str,
    firm_table: str,
    firm_id: str,
    created_by: str,
    firm_store: FirmStore,
    stores: CaseStores,
    check: bool,
) -> int:
    """Converge one fixture case. Returns how many things were missing.

    UNDER --check EVERY MISSING THING IS NAMED, in the same vocabulary the
    write path prints. A count alone is unreadable: a case row can be present
    while its debtors, items or documents are not, and the report was then
    `case 'x': present` followed by a bare "not fully loaded" — which leaves
    reading this source as the only way to learn what was absent.
    """
    handle = str(spec.get("handle") or "")
    if not handle:
        raise RefusedError("every fixture case needs a handle")
    case_id = derived_id(case_table, firm_id, version, handle)
    missing = 0

    debtors = spec.get("debtors") or {}
    if not isinstance(debtors, dict):
        raise RefusedError(f"case '{handle}': debtors must be an object keyed by role")
    collections = spec.get("collections") or {}
    if not isinstance(collections, dict):
        raise RefusedError(f"case '{handle}': collections must be an object")

    # What a `$ref` resolves to in THIS target (see "references" above): a
    # debtor already here keeps the id it has — a v1/v2 debtor was minted
    # before ids were derived — and one about to be written gets the
    # derived id it will be written under.
    debtor_ids = {
        str(role): (
            found.id
            if (found := stores.debtors.get(case_id, filing_role=str(role))) is not None
            else _debtor_id(case_id, str(role))
        )
        for role in debtors
    }
    handles = _collection_handles(handle, collections)
    client_ids = {
        client_handle: _client_id(firm_table, firm_id, version, client_handle)
        for client_handle in clients
    }

    def resolve(body: Any) -> Any:
        return _resolved(
            body,
            handle=handle,
            case_id=case_id,
            handles=handles,
            debtor_ids=debtor_ids,
            client_ids=client_ids,
        )

    # Every reference resolved ONCE before anything is written, so a
    # fixture naming a record it does not contain is refused while the
    # table is still untouched rather than half a case in.
    resolved_debtors = resolve(debtors)
    resolve(collections)

    # WHICH CLIENT EACH DEBTOR IS (ADR 0022). Debtor 1 and Debtor 2 must name
    # one — `client_id: {"$ref": "clients/<handle>"}` — because a case is
    # opened for a client; a non-filing spouse may. A fixture version from
    # before clients (v1 to v3) is therefore refused here, by design: its rows
    # are the pre-client data ADR 0022's release deletes, not something to
    # load again.
    linked: dict[str, str] = {}
    for role, body in resolved_debtors.items():
        client_id = body.get("client_id") if isinstance(body, dict) else None
        if client_id is None:
            if role in _CLIENT_ROLES:
                raise RefusedError(
                    f"case '{handle}': {role} names no client — every Debtor 1 "
                    'and Debtor 2 carries "client_id": {"$ref": "clients/<handle>"}'
                )
            continue
        if not isinstance(client_id, str):
            raise RefusedError(f"case '{handle}': {role}'s client_id is not a $ref")
        linked[str(role)] = client_id
    if len(set(linked.values())) != len(linked):
        raise RefusedError(
            f"case '{handle}': one client names two debtors — one client, one role"
        )

    # The clients themselves, before the case that names them: parsed by the
    # route's own parser, converged like every other seeded row.
    for name, client_id in sorted(client_ids.items()):
        if client_id not in linked.values():
            continue
        body = {k: v for k, v in clients[name].items() if k != "handle"}
        client_draft = parse_firm_client(body)
        if firm_store.get_client(firm_id, client_id) is not None:
            continue
        missing += 1
        if check:
            print(f"    client '{name}': missing")
            continue
        minted_client = create_firm_client(
            client_draft, firm_id=firm_id, created_by=created_by
        )
        firm_store.create_client(replace(minted_client, id=client_id))
        print(f"    client '{name}': created")

    # Each debtor's record, drafted — and its tax id sealed — only when it
    # is about to be written. Sealed the way the API seals one (issue 13.12
    # / #382): fresh ref, the firm bound in the context, the digits nowhere
    # but the envelope. The fixture value is synthetic by the fixture's own
    # rule, and the parser would refuse a real-looking one.
    def debtor_record(role: str, case_created_at: str) -> Debtor:
        debtor_draft = parse_debtor(resolved_debtors[role])
        tax_id = store_tax_id(
            debtor_draft.tax_id,
            existing=None,
            firm_id=firm_id,
            case_id=case_id,
            cipher=stores.tax_id_cipher,
            store=stores.tax_ids,
        )
        client_id = linked.get(role)
        minted_debtor = create_debtor(
            debtor_draft,
            case_id=case_id,
            filing_role=role,
            tax_id=tax_id,
            client_id=client_id,
            case_created_at=case_created_at if client_id is not None else None,
        )
        return replace(minted_debtor, id=debtor_ids[role])

    # Every debtor body parsed before the first case-table write, so a
    # malformed one fails with the API's field errors and nothing written.
    for role in resolved_debtors:
        parse_debtor(resolved_debtors[role])

    # The case itself: parsed by the route's own parser — clients and all —
    # stamped by the same factory, then given its derived id, a matching
    # assignment AND its client debtors, in the one transaction
    # `POST /v1/cases` uses.
    written_with_case: set[str] = set()
    stored_case = stores.cases.read_for_worker(case_id)
    if stored_case is None:
        missing += 1
        if check:
            print(f"  case '{handle}': missing")
        else:
            # `court` + `division` are a registry reference (issue #360), so
            # a fixture naming a court the registry does not know fails here
            # with the API's own field error rather than seeding a case the
            # service would refuse to write.
            draft = parse_case_creation(
                {
                    "chapter": spec.get("chapter"),
                    "court": spec.get("court"),
                    "division": spec.get("division"),
                    "client_ids": [linked[r] for r in _CLIENT_ROLES if r in linked],
                }
            )
            minted, _ = create_case(draft, firm_id=firm_id, created_by=created_by)
            case = replace(minted, id=case_id)
            opening = [
                debtor_record(role, case.created_at)
                for role in _CLIENT_ROLES
                if role in linked
                and stores.debtors.get(case_id, filing_role=role) is None
            ]
            stores.cases.create(
                case,
                assign_case(case, subject=created_by, assigned_by=created_by),
                opening,
            )
            written_with_case = {debtor.filing_role for debtor in opening}
            stored_case = case
            print(f"  case '{handle}': created ({case_id})")
            for role in sorted(written_with_case):
                print(f"    debtor {role}: created")
    else:
        print(f"  case '{handle}': present")

    # The rest of the debtors, keyed by filing role — the API's natural key.
    for role in resolved_debtors:
        if role in written_with_case:
            continue
        if stores.debtors.get(case_id, filing_role=role) is not None:
            continue
        missing += 1
        if check or stored_case is None:
            print(f"    debtor {role}: missing")
            continue
        stores.debtors.create(debtor_record(role, stored_case.created_at))
        print(f"    debtor {role}: created")

    # Collection items, in fixture order, each with an id derived from its
    # position so a re-run finds it rather than adding a twin — and so a
    # `$ref` to it resolves before it exists.
    for name, items in collections.items():
        kind = COLLECTIONS.get(str(name))
        if kind is None:
            raise RefusedError(f"case '{handle}': unknown collection '{name}'")
        for index, body in enumerate(items or []):
            entity_draft = parse_entity(kind, resolve(body))
            entity_id = derived_id(case_id, str(name), str(index))
            if stores.entities.get(case_id, kind, entity_id) is not None:
                continue
            missing += 1
            if check:
                print(f"    {name}[{index}]: missing")
            else:
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
            # A row that exists but is not `stored` is a DIFFERENT failure from
            # no row at all — an interrupted copy rather than an unseeded case —
            # and saying which is the whole point of naming them.
            state = "not stored" if existing is not None else "missing"
            print(f"    document {document.get('fileName')}: {state}")
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


def _seed_clients(
    entries: list[Any],
    *,
    case_id: str,
    firm_id: str,
    invited_by: str,
    firm_store: FirmStore,
    accounts: Accounts,
    bindings: ClientBindingStore,
    check: bool,
) -> int:
    """Converge a fixture case's portal clients. Returns how many are missing.

    See the module docstring's "Clients" — the one path that writes a binding
    outside the invitation route, and it writes it through the same parser,
    planner and store.
    """
    missing = 0
    for entry in entries:
        if not isinstance(entry, dict):
            raise RefusedError("each client in a fixture case must be an object")
        handle = str(entry.get("handle") or "")
        draft = parse_invitation(
            {
                key: entry[key]
                for key in ("email", "displayName", "roles")
                if key in entry
            }
        )
        try:
            subject = _subject_for(entry, accounts, check=check)
        except RefusedError:
            if not check:
                raise
            print(f"    client '{handle}': no account")
            missing += 1
            continue
        if firm_store.find_user(subject) is not None:
            raise RefusedError(
                f"client '{handle}' is also a firm user — a subject is staff or a "
                "client, never both (ADR 0023)"
            )
        existing = bindings.get(firm_id, subject)
        if existing is not None:
            print(f"    client '{handle}': present ({existing.status})")
            continue
        missing += 1
        if check:
            print(f"    client '{handle}': missing")
            continue
        binding = create_binding(
            draft,
            firm_id=firm_id,
            case_id=case_id,
            subject=subject,
            invited_by=invited_by,
        )
        try:
            narrowed = plan_binding(
                binding, previous=None, on_case=bindings.list_for_case(case_id)
            )
            bindings.bind(binding, narrowed=narrowed)
        except ConflictError as conflict:
            # The API's 409 is this loader's refusal: a fixture that gives two
            # clients one debtor role is a fixture to fix, not to half-load.
            raise RefusedError(f"client '{handle}': {conflict}") from None
        print(f"    client '{handle}': bound ({', '.join(binding.roles)})")
    return missing


def _seed_cases(
    entries: list[Any],
    fixture: Mapping[str, Any],
    env_fixture: Path,
    *,
    case_table: str,
    firm_table: str,
    firm_store: FirmStore,
    accounts: Accounts,
    stores: CaseStores,
    bindings: Callable[[], ClientBindingStore],
    check: bool,
) -> int:
    """`bindings` is a factory, called only for a case that names clients —
    so a fixture without any never constructs a client for a store it does
    not use."""
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
            clients=_version_clients(wanted, cases),
            case_table=case_table,
            firm_table=firm_table,
            firm_id=firm_id,
            created_by=subject,
            firm_store=firm_store,
            stores=stores,
            check=check,
        )
        clients = entry.get("clients") or []
        if clients:
            case_id = derived_id(case_table, firm_id, version, str(spec["handle"]))
            if stores.cases.read_for_worker(case_id) is None:
                # Only reachable under --check on an unseeded case: a binding
                # must never point at a case that is not there.
                print(f"  case '{wanted}': clients wait for the case")
                missing += len(clients)
                continue
            missing += _seed_clients(
                list(clients),
                case_id=case_id,
                firm_id=firm_id,
                invited_by=subject,
                firm_store=firm_store,
                accounts=accounts,
                bindings=bindings(),
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
# A captured firm client's server-owned members: identity, stamps, status
# (a seeded client starts active) and the tax-id view, which has no writer.
_CLIENT_SERVER_OWNED = (
    "id",
    "status",
    "created_at",
    "updated_at",
    "created_by",
    "tax_id_last_four",
)


def _strip(body: Mapping[str, object]) -> dict[str, object]:
    """The request body a captured record came from, laid out for review:
    the fields as the API prints them, then provenance, sorted — the order
    a reviewer reads a fixture diff in. `amended` is dropped when false,
    the default the parser supplies, so it only appears where it says
    something."""
    fields = {
        k: v
        for k, v in body.items()
        if k not in _SERVER_OWNED
        and k != "provenance"
        and not (k == "amended" and v is False)
    }
    provenance = body.get("provenance")
    if isinstance(provenance, Mapping):
        fields["provenance"] = dict(sorted(provenance.items()))
    return fields


def _without_tax_id(body: dict[str, object]) -> dict[str, object]:
    """A captured debtor minus its tax id and that field's provenance entry
    — see `capture` for why the view cannot be written back."""
    stripped = {k: v for k, v in body.items() if k != "tax_id"}
    provenance = stripped.get("provenance")
    if isinstance(provenance, Mapping):
        stripped["provenance"] = {k: v for k, v in provenance.items() if k != "tax_id"}
    return stripped


def _with_refs(node: Any, refs: Mapping[str, str], referenced: set[str]) -> Any:
    """`node` with every string that is one of this case's record ids
    replaced by `{"$ref": …}` — the inverse of `_resolved`. `referenced`
    collects what was pointed at, so only those items carry a `$handle`."""
    if isinstance(node, str) and node in refs:
        referenced.add(refs[node])
        return {_REF: refs[node]}
    if isinstance(node, list):
        return [_with_refs(item, refs, referenced) for item in node]
    if isinstance(node, dict):
        return {k: _with_refs(v, refs, referenced) for k, v in node.items()}
    return node


def capture(
    folder: Path,
    *,
    case_id: str,
    handle: str,
    case_table: str,
    stores: CaseStores,
    replace_existing: bool,
    firm_store: FirmStore | None = None,
) -> None:
    """Write one dev-stack case into `folder` as (part of) a fixture version.

    `firm_store` is where the debtors' firm clients are read from (ADR
    0022); a case whose debtors name clients cannot be captured without it.

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

    if case.court is None or case.division is None:
        # A pre-registry row (issue #360): the loader's parser — the API's —
        # needs the reference, and free text cannot be turned into one here.
        raise RefusedError(
            f"case {case_id} names no court and division; pick them on the "
            "case before capturing it"
        )
    debtors = stores.debtors.list_for_case(case_id)
    spec: dict[str, Any] = {
        "handle": handle,
        "chapter": case.chapter,
        # The registry reference, which is what `load` parses; `district` is
        # the printed name the parser derives from it.
        "court": case.court,
        "division": case.division,
        # `tax_id` is DROPPED from a captured debtor, and its provenance
        # entry with it: the API's own representation is the last-four view,
        # which the loader cannot re-seal (the full digits are behind the
        # audited read, which a fixture must never trigger). A captured
        # fixture that needs one gets it typed in by hand, from the SSA's
        # never-issued advertising block (insolvia_core.tax_ids).
        "debtors": {
            debtor.filing_role: _without_tax_id(_strip(debtor_json(debtor)))
            for debtor in debtors
        },
        "collections": {},
        "documents": [],
    }
    # Every id this case's records carry, and the reference that names the
    # same record in a fixture ("references" above). A captured body that
    # holds one of these ids gets the `$ref`; the item it points at gets a
    # `$handle`. An id the case does not own is left as typed.
    refs: dict[str, str] = {
        debtor.id: f"{_DEBTORS}/{debtor.filing_role}" for debtor in debtors
    }
    # The firm clients the debtors were copied from (ADR 0022) become the
    # version's top-level `clients`, each debtor's `client_id` — and every
    # `client` provenance entry's — a `$ref` to one. Handles are the case's
    # handle and the role, so two captured cases never collide.
    captured_clients: list[dict[str, Any]] = []
    for debtor in debtors:
        if debtor.client_id is None:
            continue
        if firm_store is None:
            raise RefusedError(
                f"case {case_id}'s {debtor.filing_role} names a client; "
                "--firm-table is needed to capture it"
            )
        client = firm_store.get_client(case.firm_id, debtor.client_id)
        if client is None:
            raise RefusedError(
                f"case {case_id}'s {debtor.filing_role} names client "
                f"{debtor.client_id}, which the firm table does not hold"
            )
        client_handle = f"{handle}-{debtor.filing_role.replace('_', '-')}"
        refs[client.id] = f"{_CLIENTS}/{client_handle}"
        body = {
            k: v
            for k, v in firm_client_json(client).items()
            if k not in _CLIENT_SERVER_OWNED
        }
        captured_clients.append({"handle": client_handle, **body})
    captured: dict[str, list[tuple[str, dict[str, object]]]] = {}
    for name, kind in COLLECTIONS.items():
        entities = stores.entities.list_for_case(case_id, kind)
        if not entities:
            continue
        captured[name] = []
        for index, entity in enumerate(entities, start=1):
            item_handle = f"{name}-{index}"
            refs[entity.id] = f"{name}/{item_handle}"
            captured[name].append((item_handle, _strip(entity_json(entity))))
    referenced: set[str] = set()
    spec["debtors"] = {
        role: _with_refs(body, refs, referenced)
        for role, body in spec["debtors"].items()
    }
    bodies = {
        name: [(h, _with_refs(body, refs, referenced)) for h, body in items]
        for name, items in captured.items()
    }
    for name, items in bodies.items():
        spec["collections"][name] = [
            {_HANDLE: h, **body} if f"{name}/{h}" in referenced else body
            for h, body in items
        ]

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
    if captured_clients:
        replaced = {entry["handle"] for entry in captured_clients}
        cases[_CLIENTS] = [
            *(c for c in cases.get(_CLIENTS) or [] if c.get("handle") not in replaced),
            *captured_clients,
        ]
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
    cap.add_argument(
        "--firm-table", help="where the debtors' firm clients are read from"
    )
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
        tax_ids=deps.tax_id_store(args.case_table),
        tax_id_cipher=deps.tax_id_cipher(args.case_table),
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
            firm_table=args.firm_table,
            firm_store=firm_store,
            accounts=accounts,
            stores=stores,
            bindings=lambda: deps.client_binding_store(
                args.firm_table, args.case_table
            ),
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
            capture_firms: FirmStore | None = None
            if args.firm_table is not None:
                # Only a dev stack may be captured from — the firm table too.
                if not re.match(r"^insolvia-dev-[0-9a-f]{12}-firms\Z", args.firm_table):
                    raise RefusedError(
                        f"'{args.firm_table}' is not a dev firm table — only a "
                        "dev stack may be captured from"
                    )
                capture_firms = dependencies.firm_store(args.firm_table)
            capture(
                args.version,
                case_id=args.case,
                handle=args.handle,
                case_table=args.case_table,
                stores=stores,
                replace_existing=args.replace,
                firm_store=capture_firms,
            )
            return _OK
    except RefusedError as refusal:
        print(f"refusing: {refusal}", file=sys.stderr)
        return _REFUSED
    return _OK


if __name__ == "__main__":
    raise SystemExit(main())
