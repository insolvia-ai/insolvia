"""A court's case number, read into its parts — the key court notices are
matched on (ADR 0024 PR 8, feeding #369).

CM/ECF numbers a bankruptcy case `O:YY-tt-NNNNN`, optionally followed by
the assigned judge's initials:

    6:26-bk-10000          office 6, 2026, a bankruptcy case, number 10000
    8:26-bk-01234-RCT      the same shape, with the judge's initials
    26-10000-mg            the office and the type left off (several courts
                           print their notices this way), judge `mg`

What identifies the case is (court, office, year, type, sequence). The
judge's initials are NOT part of it: a reassignment changes them, and a
notice printed before and one printed after must find the same case. Nor are
the zero-padding, the letter case or a four-digit year — `6:2026-BK-1234`
and `6:26-bk-01234` are one case.

`case_number_key` is the stored form (`caseNumberKey` on the case item):

    flmb:6:26-bk-10000

— the court's CM/ECF code first, because a number is unique only within its
court. A notice whose number omits the office (`26-10000`) cannot produce
this key alone; its matcher compares `CaseNumber.matches`, which treats a
missing office (and a missing type, which then means `bk`) as a wildcard
over the stored parts. That leniency is the MATCHER's, for text the court
printed; what Insolvia STORES always has every part (`parse_case_number(...,
require_office=True)` is what the hand-back route and the filing worker use).

Free text stays free: `cases.case_number` is still whatever the docket
said, and a number this module cannot read simply has no key (#355's
reasoning — a format check on the manual PATCH would refuse the one court
that writes its numbers differently).
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Final

# Office: one or two digits. Year: two or four. Type: two or three letters
# (bk, ap, mp...). Sequence: one to six digits. Judge: letters, after a dash
# or in parentheses.
_PATTERN: Final = re.compile(
    r"""
    ^\s*
    (?:(?P<office>\d{1,2})\s*:\s*)?
    (?P<year>\d{4}|\d{2})
    -
    (?:(?P<type>[A-Za-z]{2,3})-)?
    (?P<sequence>\d{1,6})
    (?:\s*-\s*(?P<judge>[A-Za-z]{1,6})|\s*\((?P<judge_paren>[A-Za-z]{1,6})\))?
    \s*$
    """,
    re.VERBOSE,
)

DEFAULT_TYPE: Final = "bk"


@dataclass(frozen=True)
class CaseNumber:
    """The identifying parts of a court case number. `office` is None only
    when the text left it off."""

    office: str | None
    year: str  # two digits
    type: str  # lower case, `bk` when the text left it off
    sequence: str  # at least five digits, zero-padded
    judge: str | None = None

    @property
    def canonical(self) -> str:
        """The number in CM/ECF's own form, without the judge — what the app
        shows and what the case record stores."""
        office = f"{self.office}:" if self.office is not None else ""
        return f"{office}{self.year}-{self.type}-{self.sequence}"

    def key(self, court: str) -> str:
        """The stored match key. Refuses a number without its office: a key
        must name the whole case."""
        if self.office is None:
            raise ValueError("a case number key needs the office")
        return f"{court.strip().lower()}:{self.canonical}"

    def matches(self, other: CaseNumber) -> bool:
        """Whether two printed numbers name the same case within one court —
        a missing office on either side matches any office."""
        return (
            self.year == other.year
            and self.type == other.type
            and self.sequence == other.sequence
            and (
                self.office is None
                or other.office is None
                or self.office == other.office
            )
        )


def parse_case_number(text: str, *, require_office: bool = False) -> CaseNumber | None:
    """The parts of `text`, or None when it is not a CM/ECF case number."""
    match = _PATTERN.match(text)
    if match is None:
        return None
    office = match.group("office")
    if office is None and require_office:
        return None
    year = match.group("year")
    sequence = match.group("sequence")
    judge = match.group("judge") or match.group("judge_paren")
    return CaseNumber(
        office=str(int(office)) if office is not None else None,
        year=year[-2:],
        type=(match.group("type") or DEFAULT_TYPE).lower(),
        sequence=str(int(sequence)).zfill(5),
        judge=judge.upper() if judge else None,
    )


def case_number_key(court: str | None, case_number: str | None) -> str | None:
    """`caseNumberKey` for a case: None when the case has no court, no number,
    or a number this module cannot read whole."""
    if not court or not case_number:
        return None
    parsed = parse_case_number(case_number, require_office=True)
    return parsed.key(court) if parsed is not None else None


_FORM_DATE: Final = re.compile(r"^(\d{4}-\d{2}-\d{2})(?:$|[T ])")


def petition_date(filed_at: str) -> str | None:
    """The petition date — a form date, `YYYY-MM-DD` — from the court's own
    timestamp of the filing.

    The CALENDAR DATE THE COURT PRINTED, never converted: the docket's date
    is the court's local date, and a filing at 11 p.m. Eastern is that day's
    petition even though it is the next day in UTC. So a driver reports the
    court's timestamp with the court's own offset (`2026-10-08T23:10:00-04:00`)
    or the bare date, and this takes the date part as written. None when the
    text does not start with a date."""
    match = _FORM_DATE.match(filed_at.strip())
    return match.group(1) if match is not None else None
