"""Creditor matrix generation (issue #94) — the court's mailing list.

The matrix is the one place a court constrains creditor data from outside: it
is the file the clerk's noticing system ingests, so its shape is set by the
clerks, not by us. The rules below are grounded in the published instructions
of the launch districts (ADR 0017: Florida, Texas, Georgia) rather than
asserted from memory:

- S.D. Fla. Clerk's Instruction CI-3, "Preparing a Creditor Matrix":
  https://www.flsb.uscourts.gov/sites/flsb/files/documents/clerks_instructions/Clerk%27s_Instructions_for_Preparing_Submitting_and_Obtaining_Service_Matrices_%28CI-3%29.pdf
- N.D. Fla., "Instructions for Creating the List of Creditors":
  https://www.flnb.uscourts.gov/list-of-creditors
- N.D. Tex., "Creditor Matrix Instructions":
  https://www.txnb.uscourts.gov/creditor-matrix-instructions
- S.D. Tex., "Lists of Creditors (Matrix)":
  https://www.txs.uscourts.gov/page/lists-creditors-matrix
- N.D. Ga., "List of Creditors Guidelines":
  https://www.ganb.uscourts.gov/list-creditors-guidelines

THE COMMON FORMAT, which every instruction above shares:

- a plain-text file, one column, left-justified, no headers/footers/page
  numbers/amounts;
- each creditor is a block of at most five lines, name first, each line at
  most 40 characters including spaces;
- the last line is city, state and ZIP: two-letter USPS state abbreviation in
  capitals without periods, nine-digit ZIPs hyphenated;
- one blank line between creditors;
- no duplicate name-and-address blocks (CI-3 rule (j));
- mixed case, ordinary printable characters only — the files are machine-read,
  and CI-3 rule (n) forbids substitutions like "%" for "c/o".

DISTRICT VARIANCE IS DATA, NOT CODE (the issue's own instruction). What varies
between districts is a handful of numbers — S.D. Tex. wants fixed six-line
blocks where everyone else wants up-to-five plus a separator — so the format
is a value (`MatrixFormat`) and the known departures live in
`DISTRICT_VARIANCES` beside their sources. Generation always uses
`COMMON_FORMAT` today: `case.district` is deliberately free text (see
core/cases.py — the authoritative court-code list belongs to the e-filing
work), so there is nothing reliable to key a lookup on yet. When district
codes arrive, wiring a variance in is a dictionary entry, not a new renderer.

VIOLATIONS ARE REPORTED, NEVER REPAIRED. A 41-character creditor name could be
truncated to fit, but a truncated line on the matrix is a mis-addressed
bankruptcy notice — the one failure this file exists to prevent — so every
problem is returned to a human with the creditor and field named, and no file
is produced until the list is clean. The single exception is deduplication,
which the courts require and which drops only blocks that would PRINT
identically: two records for the same creditor at two addresses are two
noticing entries and both survive.

PURE ON PURPOSE. ADR 0015: matrix generation is fast and deterministic, so it
is a synchronous endpoint today — and it becomes one step of 9.6's packet
worker, which imports this function, not the route that wraps it.
"""

from __future__ import annotations

import re
from collections.abc import Sequence
from dataclasses import dataclass
from typing import Final

from insolvia_core.case_entities import CaseEntity
from insolvia_core.creditors import CreditorBody

# The upload name CM/ECF's "Upload List of Creditors" step expects a .txt for.
# (S.D. Fla. CI-3 walks filers through saving "creditor.txt"; the name itself
# is not load-bearing on upload, the extension and encoding are.)
MATRIX_FILE_NAME: Final = "creditor-matrix.txt"

# Two-letter USPS state and possession abbreviations, per USPS Publication 28
# Appendix B (https://pe.usps.com/text/pub28/28apb.htm) — the same list S.D.
# Fla. CI-3 reprints as its section III. Includes the territories and the
# armed-forces "states" because creditors genuinely have those addresses.
US_STATE_ABBREVIATIONS: Final = frozenset(
    {
        "AL", "AK", "AS", "AZ", "AR", "CA", "CO", "CT", "DE", "DC",
        "FL", "FM", "GA", "GU", "HI", "ID", "IL", "IN", "IA", "KS",
        "KY", "LA", "ME", "MH", "MD", "MA", "MI", "MN", "MS", "MO",
        "MT", "NE", "NV", "NH", "NJ", "NM", "NY", "NC", "ND", "MP",
        "OH", "OK", "OR", "PA", "PW", "PR", "RI", "SC", "SD", "TN",
        "TX", "UT", "VT", "VI", "VA", "WA", "WV", "WI", "WY",
        "AA", "AE", "AP",
    }
)  # fmt: skip

# 12345 or 12345-6789 — "use a hyphen for nine digit zip codes" (CI-3 rule (h)).
_ZIP_RE: Final = re.compile(r"^\d{5}(-\d{4})?$")


@dataclass(frozen=True)
class MatrixFormat:
    """One district family's matrix shape, as numbers rather than branches.

    `pad_to_lines` is the S.D. Tex. departure: entries there are fixed
    six-line blocks with blank lines inserted to fill, instead of variable
    blocks with a separator. Every other knob is shared by all five published
    instruction sets above.
    """

    max_line_chars: int = 40
    max_creditor_lines: int = 5
    blank_lines_between: int = 1
    # When set, every block is padded with trailing blank lines to exactly
    # this many lines, and `blank_lines_between` should be 0 — the padding IS
    # the separation.
    pad_to_lines: int | None = None


COMMON_FORMAT: Final = MatrixFormat()

# The verified per-district departures from the common format, keyed by the
# court's CM/ECF abbreviation. DATA, deliberately: generation does not read
# this mapping yet, because case.district is free text with no authoritative
# code list (core/cases.py). It exists so the e-filing milestone wires a
# district in by adding a lookup, not a renderer — and so the next person
# checking a district records what they found beside a source, not in a
# comment three files away.
DISTRICT_VARIANCES: Final[dict[str, MatrixFormat]] = {
    # S.D. Tex.: "Addresses must be in a format of six lines for every entry
    # ... Blank lines must be inserted to conform to the six-line format."
    # https://www.txs.uscourts.gov/page/lists-creditors-matrix
    "txsb": MatrixFormat(pad_to_lines=6, blank_lines_between=0),
    # N.D. Tex. asks for at least two blank lines between listings (its
    # instruction page covers the paper and electronic matrix together).
    # https://www.txnb.uscourts.gov/creditor-matrix-instructions
    "txnb": MatrixFormat(blank_lines_between=2),
    # Verified identical to the common format: S.D. Fla. (CI-3), N.D. Fla.,
    # N.D. Ga. — no entry needed; absence means COMMON_FORMAT.
}


@dataclass(frozen=True)
class MatrixProblem:
    """One reason one creditor cannot go on the matrix as recorded.

    `creditor_id` is None for the one case-level problem (no creditors at
    all). `field` is the body path the fix belongs to, matching the paths the
    entity endpoints validate, so the client can put the message next to the
    input that needs the edit.
    """

    creditor_id: str | None
    field: str
    message: str


@dataclass(frozen=True)
class CreditorMatrix:
    """A generation outcome. `content` is the exact file text — present only
    when `problems` is empty, because a partial matrix silently omits
    creditors from noticing, which is worse than no file."""

    content: str | None
    creditor_count: int
    duplicates_omitted: int
    problems: tuple[MatrixProblem, ...]


def _printable_ascii(value: str) -> bool:
    # The clerks' scanners read plain ASCII; FLNB: "Do not use special
    # characters such as ½ or accent marks." Printable range only — the field
    # parsers already reject embedded newlines.
    return all(" " <= character <= "~" for character in value)


def _creditor_problems(entity: CaseEntity[CreditorBody]) -> list[MatrixProblem]:
    body = entity.body
    address = body.address
    problems: list[MatrixProblem] = []

    def problem(field: str, message: str) -> None:
        problems.append(
            MatrixProblem(creditor_id=entity.id, field=field, message=message)
        )

    if body.name is None:
        problem("name", "A creditor needs a name to appear on the matrix.")

    structured = (address.line1, address.city, address.state, address.postal_code)
    if all(part is None for part in structured):
        if address.raw is not None:
            # An extraction blob that was never structured. Refusing it here is
            # the point of confirm-before-entry: the matrix is a filing, and an
            # unparsed address cannot make the city/state/ZIP line the courts
            # require.
            problem(
                "address",
                "Only an unstructured address is on file — enter the street,"
                " city, state and ZIP before generating the matrix.",
            )
        else:
            problem("address", "A creditor needs a mailing address.")
    else:
        if address.line1 is None:
            problem("address.line1", "A street address or PO box is required.")
        if address.city is None:
            problem("address.city", "A city is required.")
        if address.state is None:
            problem("address.state", "A state is required.")
        elif address.state not in US_STATE_ABBREVIATIONS:
            problem(
                "address.state",
                'Must be a two-letter USPS state abbreviation in capitals, like "FL".',
            )
        if address.postal_code is None:
            problem("address.postal_code", "A ZIP code is required.")
        elif not _ZIP_RE.match(address.postal_code):
            problem(
                "address.postal_code",
                'Must be a five-digit ZIP ("33301") or hyphenated nine-digit'
                ' ZIP ("33301-1234").',
            )

    return problems


def _block_lines(body: CreditorBody) -> tuple[tuple[str, str], ...]:
    """The lines a clean creditor prints, each paired with the field it came
    from so a length or character problem can name what to edit. The last
    line follows CI-3 rule (h) and its own sample matrix: city, state and ZIP
    separated by single spaces, no comma."""
    address = body.address
    lines: list[tuple[str, str]] = [("name", body.name or "")]
    if address.line1 is not None:
        lines.append(("address.line1", address.line1))
    if address.line2 is not None:
        lines.append(("address.line2", address.line2))
    lines.append(("address", f"{address.city} {address.state} {address.postal_code}"))
    return tuple(lines)


def _line_problems(
    entity: CaseEntity[CreditorBody], fmt: MatrixFormat
) -> list[MatrixProblem]:
    problems: list[MatrixProblem] = []
    lines = _block_lines(entity.body)
    for field, line in lines:
        if len(line) > fmt.max_line_chars:
            problems.append(
                MatrixProblem(
                    creditor_id=entity.id,
                    field=field,
                    message=f"Exceeds {fmt.max_line_chars} characters — the"
                    " courts reject longer matrix lines, so shorten it"
                    " (abbreviate, or move detail to the second address"
                    " line).",
                )
            )
        if not _printable_ascii(line):
            problems.append(
                MatrixProblem(
                    creditor_id=entity.id,
                    field=field,
                    message="Contains characters outside plain ASCII — the"
                    " courts' scanners cannot read accents or symbols, so"
                    " respell it with ordinary letters.",
                )
            )
    if len(lines) > fmt.max_creditor_lines:
        problems.append(
            MatrixProblem(
                creditor_id=entity.id,
                field="address",
                message=f"Prints as more than {fmt.max_creditor_lines} lines —"
                " the courts cap a creditor at"
                f" {fmt.max_creditor_lines}.",
            )
        )
    return problems


def generate_creditor_matrix(
    creditors: Sequence[CaseEntity[CreditorBody]],
    fmt: MatrixFormat = COMMON_FORMAT,
) -> CreditorMatrix:
    """The matrix for one case's creditor list, or every reason there isn't
    one — never both, and never a partial file.

    Deterministic by construction: entries sort alphabetically by name (the
    order every sample matrix prints, and the order a clerk checks against
    the schedules), case-insensitively, with the full block as the tiebreak.
    Blocks that would print identically ignoring letter case are one entry —
    CI-3 rule (j) forbids duplicates, and case is the one difference the
    mailroom cannot see. Nothing else merges: the entry-time rule that
    dedupe is a suggestion to a human (core/creditors.py) still governs the
    records themselves; this only refuses to print one mailing label twice.

    The file uses CRLF line endings and ends with a newline — the clerks'
    instructions all describe an "MS-DOS text" file, and CRLF is the one
    convention every district's intake tooling predates.
    """
    problems: list[MatrixProblem] = []
    if not creditors:
        problems.append(
            MatrixProblem(
                creditor_id=None,
                field="creditors",
                message="The case has no creditors — a matrix must list every"
                " creditor before it can be filed.",
            )
        )

    printable: list[tuple[str, ...]] = []
    for entity in creditors:
        creditor_problems = _creditor_problems(entity)
        if creditor_problems:
            problems.extend(creditor_problems)
            continue
        line_problems = _line_problems(entity, fmt)
        if line_problems:
            problems.extend(line_problems)
            continue
        printable.append(tuple(line for _, line in _block_lines(entity.body)))

    if problems:
        return CreditorMatrix(
            content=None,
            creditor_count=0,
            duplicates_omitted=0,
            problems=tuple(problems),
        )

    printable.sort(key=lambda block: (block[0].casefold(), block))
    blocks: list[tuple[str, ...]] = []
    seen: set[tuple[str, ...]] = set()
    duplicates = 0
    for block in printable:
        key = tuple(line.casefold() for line in block)
        if key in seen:
            duplicates += 1
            continue
        seen.add(key)
        blocks.append(block)

    rendered: list[str] = []
    for block in blocks:
        lines = list(block)
        if fmt.pad_to_lines is not None:
            lines.extend([""] * (fmt.pad_to_lines - len(lines)))
        rendered.append("\r\n".join(lines))
    separator = "\r\n" * (fmt.blank_lines_between + 1)
    content = separator.join(rendered) + "\r\n"
    return CreditorMatrix(
        content=content,
        creditor_count=len(blocks),
        duplicates_omitted=duplicates,
        problems=(),
    )


def matrix_json(matrix: CreditorMatrix) -> dict[str, object]:
    """The API representation. `content` is omitted, not null, when there are
    problems — the same absent-means-absent rule every other response here
    follows."""
    body: dict[str, object] = {
        "fileName": MATRIX_FILE_NAME,
        "creditorCount": matrix.creditor_count,
        "duplicatesOmitted": matrix.duplicates_omitted,
        "problems": [
            {
                **(
                    {"creditorId": problem.creditor_id}
                    if problem.creditor_id is not None
                    else {}
                ),
                "field": problem.field,
                "message": problem.message,
            }
            for problem in matrix.problems
        ],
    }
    if matrix.content is not None:
        body["content"] = matrix.content
    return body
