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

## Adding an entity

A `_seed_<entity>` function that takes its slice of the fixture and the stores
it needs, a key in the fixture schema, and a field on `Dependencies` so tests
can pass a memory adapter. `cases` is the expected next one.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
from collections.abc import Callable, Mapping
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Final, Protocol

from insolvia_core.adapters.aws.firm_store import DynamoDbFirmStore
from insolvia_core.firms import (
    create_firm,
    create_firm_user,
    parse_firm_creation,
    parse_firm_user_creation,
)
from insolvia_core.ports import FirmStore

# What infra/modules/* name a table, per environment. Dev carries the machine
# short id from scripts/dev-aws-common.sh; staging is flat. Prod matches
# neither, which is the point.
_SEEDABLE_TABLE: Final = r"^insolvia-{kind}-(dev-[0-9a-f]{{12}}|staging)\Z"

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


@dataclass(frozen=True)
class Dependencies:
    """The seams tests replace. Factories, because neither a table name nor a
    pool id is legitimate until the guards below have accepted it."""

    firm_store: Callable[[str], FirmStore] = DynamoDbFirmStore
    accounts: Callable[[str], Accounts] = CognitoAccounts


def _require_seedable_table(table: str, *, kind: str) -> None:
    if not re.match(_SEEDABLE_TABLE.format(kind=kind), table):
        raise RefusedError(
            f"'{table}' is not a seedable {kind} table (expected "
            f"insolvia-{kind}-dev-<machine short id> or insolvia-{kind}-staging)"
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


# ── plumbing ────────────────────────────────────────────────────────


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="seed",
        description="Load a seed fixture. Dev and staging only, never prod.",
    )
    parser.add_argument("--fixture", required=True, type=Path)
    parser.add_argument("--firm-table", required=True)
    parser.add_argument(
        "--user-pool-id",
        required=True,
        help="the pool the fixture's people live in",
    )
    parser.add_argument(
        "--check",
        action="store_true",
        help="report what is missing; write nothing",
    )
    return parser


def main(argv: list[str] | None = None, *, deps: Dependencies | None = None) -> int:
    args = _build_parser().parse_args(argv)
    dependencies = deps or Dependencies()
    try:
        _require_seedable_table(args.firm_table, kind="firms")
        fixture = load_fixture(args.fixture, os.environ)
        missing = _seed_firms(
            fixture.get("firms") or [],
            dependencies.firm_store(args.firm_table),
            dependencies.accounts(args.user_pool_id),
            check=args.check,
        )
    except RefusedError as refusal:
        print(f"refusing: {refusal}", file=sys.stderr)
        return _REFUSED
    if args.check and missing:
        return _NOT_SEEDED
    return _OK


if __name__ == "__main__":
    raise SystemExit(main())
