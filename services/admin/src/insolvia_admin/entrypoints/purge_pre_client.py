"""Delete the pre-client case partitions — ADR 0022's release step, and the
one command in this repo that deletes case data on purpose.

    purge_pre_client --case-table T --firm-table F            report only
    purge_pre_client --case-table T --firm-table F --check    exit 1 if any remain
    purge_pre_client --case-table T --firm-table F --apply    delete them

WHY IT EXISTS. ADR 0022 chose no migration (maintainer, 2026-09-26: the
product is not live and every environment's data is disposable). From the
PR that made `POST /v1/cases` require clients, a debtor without a
`client_id` is a state the store no longer produces; the rows written before
it are deleted here, once per environment, and the seed fixture that
replaces them names a client for every debtor. The rule, and what it can
never delete, is `insolvia_admin.core.pre_client_purge`'s docstring — read
it before running this anywhere.

WHY IT IS SAFE TO RUN TWICE. A deleted partition has no case root, so the
next plan does not name it; a case opened for a client has a client-linked
Debtor 1 from its first write, so no plan ever names it. `--check` after an
`--apply` is the proof: exit 0 means nothing pre-client is left.

WHERE IT MAY RUN. Dev, staging AND prod — the one fence here that admits
prod, which is why it is its own entrypoint rather than a `seed`
subcommand: the seeder's dev/staging-only fence is load-bearing and stays
as it is. The two tables must name the same environment, so a staging firm
table can never be paired with a prod case table. Dry run is the default;
nothing is written without `--apply`.

NOT DELETED, and why: the access log's rows for these cases (append-only by
design — an audit trail a maintenance command could erase is not one), and
the documents' bytes in the case-documents bucket (the objects are keyed by
case id and become unreachable with their rows; deleting bytes needs an S3
grant no principal running this holds, and orphaned synthetic fixtures are
not worth one).
"""

from __future__ import annotations

import argparse
import re
import sys
from collections.abc import Callable
from typing import Final

from insolvia_admin.adapters.aws.case_purge import DynamoDbCaseTableRows
from insolvia_admin.core.pre_client_purge import CaseTableRows, purge

# insolvia-<env>-<kind>, environment SECOND (the insolvia-aws-naming skill);
# dev carries the machine's short id inside the env segment.
_TABLE: Final = r"^insolvia-(?P<env>dev-[0-9a-f]{{12}}|staging|prod)-{kind}\Z"

_OK: Final = 0
_REMAINING: Final = 1
_REFUSED: Final = 2


class RefusedError(Exception):
    """A guard rejected the run. Nothing has been read or written."""


def _environment(table: str, *, kind: str) -> str:
    match = re.match(_TABLE.format(kind=kind), table)
    if match is None:
        raise RefusedError(
            f"'{table}' is not an Insolvia {kind} table (expected "
            f"insolvia-<dev-<machine id>|staging|prod>-{kind})"
        )
    return match.group("env")


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="purge_pre_client",
        description="Delete case partitions whose debtors name no firm client "
        "(ADR 0022). Reports only unless --apply.",
    )
    parser.add_argument("--case-table", required=True)
    parser.add_argument("--firm-table", required=True)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--apply", action="store_true", help="delete what the plan names")
    mode.add_argument(
        "--check", action="store_true", help="exit 1 if any pre-client case remains"
    )
    return parser


def main(
    argv: list[str] | None = None,
    *,
    rows: Callable[[str, str], CaseTableRows] = DynamoDbCaseTableRows,
) -> int:
    args = _build_parser().parse_args(argv)
    try:
        case_env = _environment(args.case_table, kind="cases")
        firm_env = _environment(args.firm_table, kind="firms")
        if case_env != firm_env:
            raise RefusedError(
                f"the case table is {case_env}'s and the firm table is "
                f"{firm_env}'s — both must name one environment"
            )
    except RefusedError as refusal:
        print(f"refusing: {refusal}", file=sys.stderr)
        return _REFUSED

    report = purge(rows(args.case_table, args.firm_table), apply=args.apply)
    # Case ids and counts only: never a name — a case id is what the access
    # log and every request log already carry.
    verb = "deleted" if args.apply else "would delete"
    print(
        f"{case_env}: {len(report.planned)} pre-client case(s); "
        f"{report.kept_with_client} opened for clients, kept"
    )
    for case_id in report.deleted if args.apply else report.planned:
        print(f"  {verb} case {case_id}")
    if args.apply:
        print(
            f"  {report.items_deleted} item(s) and {report.bindings_deleted} "
            "portal binding row(s) deleted"
        )
        for case_id in report.stopped:
            print(f"  stopped at case {case_id}: a debtor names a client now")
    if args.check and report.planned:
        return _REMAINING
    return _OK


if __name__ == "__main__":
    raise SystemExit(main())
