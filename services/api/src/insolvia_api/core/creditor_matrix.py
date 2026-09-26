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
is a value (`MatrixFormat`), and since issue #360 the numbers come from the
COURT REGISTRY (`insolvia_core.courts`, the `courts/us-bankruptcy` series):
each district record carries its matrix knobs as sourced, dated facts, and
`format_for_court` reads the VERIFIED ones into a `MatrixFormat`. A knob the
court's pages did not confirm falls back to the common format — a matrix in
the common format is accepted everywhere the instructions above were read,
while a figure guessed from a stale page is a mis-addressed notice.
`DISTRICT_VARIANCES` is now derived from the registry rather than written
here, keyed by the court's CM/ECF code as `case.court` names it; a case from
before the registry (no `court`) gets the common format.

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

from insolvia_core import courts
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
    blocks with a separator. `name_line_chars` and `comma_after_city` are
    the two knobs ADR 0024's research added (W.D. Tex. allows a 50-character
    name line and wants "Midland, TX"; N.D. Fla. wants the comma too).
    `case_number_header_when_separate` (E.D. Tex.) is carried as data only —
    it applies to a matrix filed separately AFTER the case has a number,
    which is the filing-set PR's rendering, not this generator's.
    """

    max_line_chars: int = 40
    max_creditor_lines: int = 5
    blank_lines_between: int = 1
    # When set, every block is padded with trailing blank lines to exactly
    # this many lines, and `blank_lines_between` should be 0 — the padding IS
    # the separation.
    pad_to_lines: int | None = None
    # A wider cap for the NAME line alone, where a court grants one.
    name_line_chars: int | None = None
    # "Tampa, FL 33602" rather than CI-3's "Tampa FL 33602".
    comma_after_city: bool = False
    case_number_header_when_separate: bool = False


COMMON_FORMAT: Final = MatrixFormat()


def format_for_court(court_code: str | None) -> MatrixFormat:
    """The format a case's court wants, from the registry's VERIFIED matrix
    facts — each knob independently, falling back to the common format for
    a knob the court's pages did not confirm, for a court the registry does
    not know, and for a case written before the registry (`court` None).

    Verified-only is the rule the module docstring gives: an unverified
    figure is a guess, and the common format is what every instruction set
    read for this module accepts.
    """
    if court_code is None:
        return COMMON_FORMAT
    record = courts.district(court_code)
    if record is None:
        return COMMON_FORMAT
    rules = record.matrix

    def number(fact: courts.Fact[int], default: int) -> int:
        return fact.value if fact.verified and fact.value is not None else default

    def flag(fact: courts.Fact[bool], default: bool) -> bool:
        return fact.value if fact.verified and fact.value is not None else default

    return MatrixFormat(
        max_line_chars=number(rules.max_line_chars, COMMON_FORMAT.max_line_chars),
        max_creditor_lines=number(
            rules.max_creditor_lines, COMMON_FORMAT.max_creditor_lines
        ),
        blank_lines_between=number(
            rules.blank_lines_between, COMMON_FORMAT.blank_lines_between
        ),
        # A verified null here is a verified "no fixed block size".
        pad_to_lines=rules.pad_to_lines.value if rules.pad_to_lines.verified else None,
        name_line_chars=(
            rules.name_line_chars.value if rules.name_line_chars.verified else None
        ),
        comma_after_city=flag(rules.comma_after_city, COMMON_FORMAT.comma_after_city),
        case_number_header_when_separate=flag(
            rules.case_number_header_when_separate,
            COMMON_FORMAT.case_number_header_when_separate,
        ),
    )


def district_variances() -> dict[str, MatrixFormat]:
    """The registry's departures from the common format, keyed by court
    code — the table this module once hand-wrote, now read. A court whose
    verified facts match the common format is recorded by ABSENCE, exactly
    as before."""
    return {
        code: fmt
        for code in courts.district_codes()
        if (fmt := format_for_court(code)) != COMMON_FORMAT
    }


# Kept under its old name for the callers and tests that read it as a table;
# the registry is its only author now.
DISTRICT_VARIANCES: Final[dict[str, MatrixFormat]] = district_variances()


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


def _block_lines(
    body: CreditorBody, fmt: MatrixFormat = COMMON_FORMAT
) -> tuple[tuple[str, str], ...]:
    """The lines a clean creditor prints, each paired with the field it came
    from so a length or character problem can name what to edit. The last
    line follows CI-3 rule (h) and its own sample matrix: city, state and ZIP
    separated by single spaces, no comma — unless the court's format says
    "Midland, TX" (`comma_after_city`)."""
    address = body.address
    lines: list[tuple[str, str]] = [("name", body.name or "")]
    if address.line1 is not None:
        lines.append(("address.line1", address.line1))
    if address.line2 is not None:
        lines.append(("address.line2", address.line2))
    separator = ", " if fmt.comma_after_city else " "
    lines.append(
        ("address", f"{address.city}{separator}{address.state} {address.postal_code}")
    )
    return tuple(lines)


def _line_problems(
    entity: CaseEntity[CreditorBody], fmt: MatrixFormat
) -> list[MatrixProblem]:
    problems: list[MatrixProblem] = []
    lines = _block_lines(entity.body, fmt)
    for field, line in lines:
        # The name line may be wider where the court grants it (W.D. Tex.).
        limit = (
            fmt.name_line_chars
            if field == "name" and fmt.name_line_chars is not None
            else fmt.max_line_chars
        )
        if len(line) > limit:
            problems.append(
                MatrixProblem(
                    creditor_id=entity.id,
                    field=field,
                    message=f"Exceeds {limit} characters — the"
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
        printable.append(tuple(line for _, line in _block_lines(entity.body, fmt)))

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
