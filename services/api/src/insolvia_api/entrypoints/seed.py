"""Write the rows a developer machine needs and the API refuses to create itself.

## Why anything outside the API writes rows at all

`core/firms.py` gave the service a tenancy model, and every route behind
`current_accessor()` answers 403 until the caller resolves to an active user of
an active firm. Nothing creates that first pair, and nothing can:
`POST /v1/firm/users` is itself behind `FIRM_ADMINISTRATION`, so it needs an
admin to add an admin; self-signup is off on every pool
(`allow_admin_create_user_only`); and ADR 0009 refuses any edit that would leave
a firm without an active administrator. All three are correct, and together they
mean the first firm has to be written from OUTSIDE the API.

`scripts/dev-aws-create-user.sh` is the Cognito half of the same gap and stops
where this starts: it leaves a person who can sign in and has no firm, which
`/v1/me` reports and every other route refuses.

## Why it goes through core/ rather than writing items

The item shapes live in `core/` — `firms.py` says so in its own docstring —
precisely so the DynamoDB and in-memory stores cannot drift apart. A seeding
tool that hand-rolled `{"PK": "FIRM#…", "SK": "META"}` would be a THIRD writer
of that shape and the one nobody re-reads when a field is added, quietly
producing valid-looking rows the service has stopped agreeing with.

This matters more with every subcommand, not less. A firm has five fields; a
case carries provenance and a confirm-before-entry invariant
(`docs/reference/case-data-model.md`) that are not conventions but rules, and
re-implementing them in a seeder is how they get violated first.

## WHAT THIS IS NOT: a test fixture factory

pytest tests build their own data through `core/` constructors and the
`adapters/memory/` stores — no AWS, no table names, no subprocess. Reaching for
this module from a unit or integration test would trade an in-process object
for a network round trip and buy nothing.

Two consumers genuinely need real rows in real tables, and they are the only
two: bootstrapping a developer machine, and the e2e suite when it runs against
that machine's dev stack (`e2e/scripts/dev-test.sh`). Both go through
`scripts/dev-aws-seed.sh`, so they seed identically instead of drifting.

## Why it is dev-only, and how that is enforced

Two independent guards, because one of them is an argument this process cannot
check. `scripts/dev-aws-seed.sh` asserts every table it passes carries THIS
machine's short id, resolved from this machine's Terraform state. This module
re-asserts the name shape itself, so running it by hand against a staging or
prod table fails before boto3 is constructed rather than seeding into a shared
environment. Neither guard alone is enough: the script's is stronger but
bypassable by calling this directly; this one is weaker but unconditional.

Staging and prod are provisioned another way, deliberately not here — see #178.
A tool whose target could be changed by one argument is a tool that eventually
is, which is the reasoning `dev-aws-create-user.sh` already records.

## Adding a subcommand

Three things, and the first is the one that carries the reasoning:

1. A `_seed_<entity>` handler that composes `core/` and returns an exit status.
   It owns its own idempotency check — "already there" is a 0, never a write.
2. Its parser in `_build_parser`, with `parents=[common]` so it inherits
   `--check`, and a `--<entity>-table` flag per store it touches.
3. A field on `StoreFactories`, so tests can pass the memory adapter.

Table names are flags rather than read from `services/api/.env`, because the
guard below has to see the name before anything opens a connection with it.
"""

from __future__ import annotations

import argparse
import re
import sys
from collections.abc import Callable
from dataclasses import dataclass
from typing import Final

from insolvia_api.adapters.aws.firm_store import DynamoDbFirmStore
from insolvia_api.core.firms import (
    ROLES,
    create_firm,
    create_firm_user,
    parse_firm_creation,
    parse_firm_user_creation,
)
from insolvia_api.core.ports import FirmStore

# What infra/modules/* name a table for infra/envs/dev, where the suffix is the
# machine short id from scripts/dev-aws-common.sh. Staging and prod are
# `insolvia-<kind>-staging` / `-prod` and match none of these.
_DEV_TABLE_TEMPLATE: Final = r"^insolvia-{kind}-dev-[0-9a-f]{{12}}\Z"

# Exit statuses, shared by every subcommand so a caller can branch on them
# without knowing which one it ran.
_OK: Final = 0
_NOT_PROVISIONED: Final = 1
_REFUSED: Final = 2


class _RefusedError(Exception):
    """A guard rejected the arguments. Never a partial write — guards run first."""


@dataclass(frozen=True)
class StoreFactories:
    """How each subcommand reaches its store.

    Factories rather than ready-made stores: a table name is only legitimate
    after `_require_dev_table` has accepted it, and a caller passing a
    constructed store would have bound one this module would have refused.
    """

    firm: Callable[[str], FirmStore] = DynamoDbFirmStore


def _require_dev_table(table: str, *, kind: str) -> None:
    if not re.match(_DEV_TABLE_TEMPLATE.format(kind=kind), table):
        raise _RefusedError(
            f"'{table}' is not a dev {kind} table "
            f"(expected insolvia-{kind}-dev-<machine short id>)"
        )


# ── firm ────────────────────────────────────────────────────────────


def _seed_firm(args: argparse.Namespace, stores: StoreFactories) -> int:
    _require_dev_table(args.firm_table, kind="firms")
    store = stores.firm(args.firm_table)

    # IDEMPOTENT, and keyed on the subject rather than the firm name on purpose:
    # re-running must not give one person a second firm. The by-subject index is
    # eventually consistent, which is harmless here — the worst case is a second
    # run moments later reporting "not yet" and then failing on `add_user`'s
    # condition, which is still a refusal rather than a duplicate.
    existing = store.find_user(args.subject)
    if existing is not None:
        found = store.get_firm(existing.firm_id)
        name = found.name if found is not None else "<firm row missing>"
        print(
            f"already provisioned: {existing.email} is "
            f"{'an admin' if existing.is_admin else existing.role} of {name} "
            f"({existing.firm_id}), status={existing.status}"
        )
        return _OK

    if args.check:
        print("not provisioned: this subject is in no firm — every route will 403")
        return _NOT_PROVISIONED

    missing = [
        flag
        for flag, value in (
            ("--firm-name", args.firm_name),
            ("--email", args.email),
            ("--display-name", args.display_name),
        )
        if not value
    ]
    if missing:
        raise _RefusedError("required without --check: " + ", ".join(missing))

    firm = create_firm(parse_firm_creation({"name": args.firm_name}))
    store.create_firm(firm)

    # isAdmin and accessAllCases both true: this is the ONLY person in the firm,
    # so an admin who could not see its cases would be a seat that has to add a
    # second seat before it can do anything, and a non-admin would reproduce the
    # lockout this command exists to break.
    user = create_firm_user(
        parse_firm_user_creation(
            {
                "email": args.email,
                "displayName": args.display_name,
                "role": args.role,
                "isAdmin": True,
                "accessAllCases": True,
            }
        ),
        firm_id=firm.id,
        subject=args.subject,
    )
    store.add_user(user)

    print(f"created firm {firm.name} ({firm.id})")
    print(f"added {user.email} as an admin {user.role} with access to all cases")
    return _OK


_HANDLERS: Final[dict[str, Callable[[argparse.Namespace, StoreFactories], int]]] = {
    "firm": _seed_firm,
}


# ── plumbing ────────────────────────────────────────────────────────


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="seed",
        description="Seed this machine's dev data stores. Never staging or prod.",
    )
    common = argparse.ArgumentParser(add_help=False)
    common.add_argument(
        "--check",
        action="store_true",
        help="report whether the rows exist; write nothing",
    )

    entities = parser.add_subparsers(dest="entity", required=True, metavar="<what>")

    firm = entities.add_parser(
        "firm",
        parents=[common],
        help="a firm, with one person in it as its admin",
    )
    firm.add_argument("--firm-table", required=True)
    firm.add_argument("--subject", required=True, help="the person's Cognito sub")
    # Not `required`: --check needs none of them. `_seed_firm` enforces them at
    # the point it is about to write, so a bare --check does not demand a firm
    # name it will never use.
    firm.add_argument("--firm-name")
    firm.add_argument("--email")
    firm.add_argument("--display-name")
    firm.add_argument("--role", default="attorney", choices=list(ROLES))

    return parser


def main(
    argv: list[str] | None = None,
    *,
    stores: StoreFactories | None = None,
) -> int:
    args = _build_parser().parse_args(argv)
    try:
        return _HANDLERS[args.entity](args, stores or StoreFactories())
    except _RefusedError as refusal:
        print(f"refusing: {refusal}", file=sys.stderr)
        return _REFUSED


if __name__ == "__main__":
    raise SystemExit(main())
