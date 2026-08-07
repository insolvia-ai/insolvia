"""Create the FIRST firm on a developer machine, and put one person in it.

## Why this exists

`core/firms.py` gave the service a tenancy model, and every route behind
`current_accessor()` answers 403 until the caller resolves to an active user of
an active firm. Nothing creates that first pair. `POST /v1/firm/users` cannot:
it is itself behind `FIRM_ADMINISTRATION`, so it needs an admin to add an
admin. Self-signup is off on every pool (`allow_admin_create_user_only`), and
ADR 0009 states the matching invariant for edits — a firm with no active
administrator cannot appoint one. All three are correct, and together they mean
the first firm has to be written from OUTSIDE the API.

`scripts/dev-aws-create-user.sh` is the Cognito half of the same gap and stops
where this starts: it leaves a person who can sign in and has no firm, which
`/v1/me` reports and every other route refuses. This is the DynamoDB half.

## Why it goes through core/firms.py rather than writing items

The item shapes live in `core/firms.py` precisely so the DynamoDB and in-memory
stores cannot drift apart (its module docstring says so). A seeding tool that
hand-rolled `{"PK": "FIRM#…", "SK": "META"}` would be a THIRD writer of that
shape and the one nobody re-reads when a field is added — it would keep
producing valid-looking rows that the service has stopped agreeing with. So
this composes the same functions a route does: parse a draft, construct through
`create_firm` / `create_firm_user`, hand the result to the real adapter.

## Why it is dev-only, and how that is enforced

Two independent guards, because one of them is an argument this process cannot
check. `scripts/dev-aws-create-firm.sh` asserts the table it passes carries THIS
machine's short id, resolved from this machine's Terraform state. This module
re-asserts the name shape itself, so running it by hand against a staging or
prod table fails before boto3 is constructed rather than seeding a tenant into
a shared environment. Neither guard alone is enough: the script's is stronger
but bypassable by calling this directly; this one is weaker but unconditional.

Staging and prod get their first firm another way. That is deliberately not
this file's problem — see the PR and `docs/runbooks/` — because a script whose
target could be changed by one argument is a script that eventually is, which
is the same reasoning `dev-aws-create-user.sh` records for being dev-only.
"""

from __future__ import annotations

import argparse
import re
import sys
from collections.abc import Callable
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

# The dev firm table, and only the dev firm table: `insolvia-firms-dev-<12 hex>`
# is what infra/modules/firm_store names it for infra/envs/dev, where the suffix
# is the machine short id from scripts/dev-aws-common.sh. Staging and prod are
# `insolvia-firms-staging` / `insolvia-firms-prod` and do not match.
_DEV_TABLE_RE: Final = re.compile(r"^insolvia-firms-dev-[0-9a-f]{12}\Z")


def _parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        prog="seed_dev_firm",
        description="Create the first firm on a dev machine and put one admin in it.",
    )
    parser.add_argument("--table", required=True, help="dev firm table name")
    parser.add_argument("--subject", required=True, help="the person's Cognito sub")
    # Not `required`, because --check needs neither. Enforced below instead, so
    # a bare --check does not demand a firm name it will never write.
    parser.add_argument("--firm-name")
    parser.add_argument("--email")
    parser.add_argument("--display-name")
    parser.add_argument("--role", default="attorney", choices=list(ROLES))
    parser.add_argument(
        "--check",
        action="store_true",
        help="report whether the subject is already in a firm; write nothing",
    )
    args = parser.parse_args(argv)
    if not args.check:
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
            parser.error(
                "the following are required without --check: " + ", ".join(missing)
            )
    return args


def main(
    argv: list[str] | None = None,
    *,
    store_factory: Callable[[str], FirmStore] = DynamoDbFirmStore,
) -> int:
    """Seed, or report. Returns the process exit status.

    `store_factory` is the port seam, and it is a factory rather than a store
    because the table name is only known after the guard below has accepted it
    — a caller passing a ready-made store could bind one this function would
    have refused. Tests pass `lambda _: MemoryFirmStore()`.
    """
    args = _parse_args(argv)

    if not _DEV_TABLE_RE.match(args.table):
        print(
            f"refusing: '{args.table}' is not a dev firm table "
            "(expected insolvia-firms-dev-<machine short id>)",
            file=sys.stderr,
        )
        return 2

    store = store_factory(args.table)

    # IDEMPOTENT, and the check is by subject rather than by firm name on
    # purpose: re-running this must not give one person a second firm. The
    # by-subject index is eventually consistent, which is harmless here —
    # the worst case is a second run moments later reporting "not yet" and
    # failing on add_user's condition instead, which is still a refusal.
    existing = store.find_user(args.subject)
    if existing is not None:
        found = store.get_firm(existing.firm_id)
        name = found.name if found is not None else "<firm row missing>"
        print(
            f"already provisioned: {existing.email} is "
            f"{'an admin' if existing.is_admin else existing.role} of {name} "
            f"({existing.firm_id}), status={existing.status}"
        )
        return 0

    if args.check:
        print("not provisioned: this subject is in no firm — every route will 403")
        return 1

    firm = create_firm(parse_firm_creation({"name": args.firm_name}))
    store.create_firm(firm)

    # isAdmin and accessAllCases both true: this is the ONLY person in the firm,
    # so an admin who could not see the firm's cases would be a seat that has to
    # add a second seat before it can do anything, and a non-admin would
    # reproduce the lockout this script exists to break.
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
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
