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

## `${VAR}` in a fixture, and why it is not a convenience

THIS REPO IS PUBLIC. `e2e/CLAUDE.md` forbids the staging test user's address in
any committed file — not as a default, not in a fixture, not in a comment. So
`seeds/staging.json` names that user as `${E2E_TEST_USER_EMAIL}` and the value
arrives from the environment at load time. Expansion FAILS on an unset variable
rather than substituting empty: a fixture that silently seeded a firm for ""
would be a broken environment that looks provisioned.

`seeds/dev.json` needs none of this — `dev@insolvia.test` is a reserved TLD
(RFC 2606), unroutable by construction, and safe to commit.

## WHAT THIS IS NOT: a unit-test fixture factory

pytest builds its own data through `core/` constructors and `adapters/memory/`
— no files, no AWS, no subprocess. Reaching for this module from a unit test
would trade an in-process object for a network round trip.

Its consumers are the two places that need real rows in real tables: a
developer machine (`scripts/dev-aws-seed.sh`) and the staging e2e run.

## Why it is dev/staging only

A tool whose target can be changed by one argument is a tool that eventually is
— the reasoning `dev-aws-create-user.sh` records. `_require_seedable_table`
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
from typing import Any, Final

from insolvia_api.adapters.aws.firm_store import DynamoDbFirmStore
from insolvia_api.core.firms import (
    create_firm,
    create_firm_user,
    parse_firm_creation,
    parse_firm_user_creation,
)
from insolvia_api.core.ports import FirmStore

# What infra/modules/* name a table, per environment. Dev carries the machine
# short id from scripts/dev-aws-common.sh; staging is flat. Prod matches
# neither, which is the point.
_SEEDABLE_TABLE: Final = r"^insolvia-{kind}-(dev-[0-9a-f]{{12}}|staging)\Z"

_VAR: Final = re.compile(r"\$\{([A-Z_][A-Z0-9_]*)\}")

_OK: Final = 0
_NOT_SEEDED: Final = 1
_REFUSED: Final = 2

# email -> Cognito sub, raising RefusedError when there is no such account.
#
# Not `core.ports.UserDirectory`: that port is deliberately AdminCreateUser and
# nothing else, because it is the API's grant and widening it would make it an
# impersonation primitive. This is a different principal with a different,
# read-only need, so it gets its own seam — and the refusal is part of the
# contract rather than whatever the implementation happens to throw, so a fake
# that raises KeyError is a broken fake, not a passing test.
SubjectResolver = Callable[[str], str]


class RefusedError(Exception):
    """A guard or the fixture rejected the run. Nothing has been written."""


def _cognito_subjects(pool_id: str) -> SubjectResolver:
    import boto3

    client = boto3.client("cognito-idp")

    def resolve(email: str) -> str:
        try:
            user = client.admin_get_user(UserPoolId=pool_id, Username=email)
        except client.exceptions.UserNotFoundException:
            raise RefusedError(
                f"no pool user for {email} — create the account before seeding "
                "the firm it belongs to"
            ) from None
        for attribute in user["UserAttributes"]:
            if attribute["Name"] == "sub":
                return str(attribute["Value"])
        raise RefusedError(f"pool user {email} has no sub attribute")

    return resolve


@dataclass(frozen=True)
class Dependencies:
    """The seams tests replace. Factories, because a table name is only
    legitimate after `_require_seedable_table` has accepted it."""

    firm_store: Callable[[str], FirmStore] = DynamoDbFirmStore
    subjects: Callable[[str], SubjectResolver] = _cognito_subjects


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


def _seed_firms(
    entries: list[Any], store: FirmStore, resolve: SubjectResolver, *, check: bool
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

        # Resolve first: a fixture naming an account that does not exist should
        # fail before any row is written, not halfway through the firm.
        drafts = [
            (parse_firm_user_creation(user), resolve(str(user["email"])))
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
        help="the pool the fixture's people already exist in",
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
            dependencies.subjects(args.user_pool_id),
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
