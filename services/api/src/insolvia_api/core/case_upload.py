"""The Case Upload package (ADR 0024 build PR 9): `Debtor.txt`, its builder
and the validator that holds it to the AO's specification.

## What the file is

CM/ECF's Case Upload takes a pipe-delimited text file that opens the case:
one `stat` record (80 statistics fields — chapter, fee status, the B101
estimates, the B106 totals, the B122A figures), one `debt` record per
debtor (name, the FULL tax identifier, address, the court's office code and
the county's FIPS code), and one `alas` record per name the debtor used.
The specification is data, not code: `regulatory/filing/case-upload/
<release>/spec.json` transcribes every field of the AO's document (its
number, name, Required column, width and permitted values) and records each
ambiguity with the reading taken. This module only reads it.

## Where the figures come from — the forms, not a second calculation

Every statistic is a figure a printed form already states, read from the
SAME projection that prints it: B106Sum's lines 1a-5 and 9g, B106I's line 2
and line 6 per column, B106J's line 23c, B122A-1's CMI columns, household
size and median (`means_test_trace.trace_means_test`, which the means-test
screen and the 122A projections share), and B122A-2's lines straight off
the engine's trace those projections land. The B101 answers (fee, debt
character, the estimates, prior cases, the sole proprietorship's nature)
are the petition record's, mapped to the spec's codes here. So the court's
statistics cannot disagree with the forms the attorney signed.

## The codes

The office code (debt field 10) is the case division's
`Division.office_code`; the county (field 17) is the debtor's residence
county looked up by name among the district's divisions (`County.fips`) —
both the court registry's (insolvia_core.courts). A county outside the
district, or a division with no office code, cannot land, and says so.

## The SSN — the most sensitive artifact in the system

`debt` field 8 is the full nine digits. They come from the sealed tax id
(issue 13.12) through `packet_assembly.disclose_tax_ids` with purpose
`case_upload` — one `taxid.read` row per debtor, naming who and why, BEFORE
the envelope is opened. Then:

- the file is GENERATED ON DEMAND and STREAMED — built in the filing
  worker's memory at upload time, handed to the court driver, and
  discarded. It is never stored, so it never appears in a document
  listing, a packet, a blob key or a response; nothing in the API composes
  it for a client (`tests/unit/test_case_upload.py` reads every call site);
- no problem message, exception, log line or repr carries a field's VALUE —
  only the record, the field number and name, and what is wrong with it;
- `CaseUploadFile` keeps its bytes out of its repr.

Why not store it encrypted under the case key? A stored copy would be a
second sealed copy of every debtor's SSN with a lifecycle of its own (who
deletes it, when, and what if the case changes after it was written), and it
would buy nothing: the file is a pure function of records the approval's
digest already binds (every debtor body, update time and tax-id reference,
every collection, the registry release), so the worker can rebuild exactly
what was approved, and the re-check before the final submit recomputes that
digest.
"""

from __future__ import annotations

import hashlib
import json
import re
import unicodedata
from collections.abc import Callable, Mapping, Sequence
from dataclasses import dataclass, field
from datetime import date
from decimal import Decimal, InvalidOperation
from functools import cache
from importlib import resources
from importlib.resources.abc import Traversable
from typing import TYPE_CHECKING, Final

from insolvia_core import courts
from insolvia_core.debtors import Debtor
from insolvia_core.fields import Address

from .form_fill import Text
from .form_projections import FormProjectionError, project
from .form_projections.b106c import _claimed_amount
from .form_projections.b106i import _column_widget
from .form_projections.shared import CaseFile, FieldValues
from .form_templates import FormRelease
from .forms_hub import resolve_case_form
from .means_test_trace import trace_means_test
from .packet_assembly import CaseData, disclose_tax_ids, read_case_data, to_case_file

if TYPE_CHECKING:
    from insolvia_core.cases import Case
    from insolvia_core.ports import (
        AccessLog,
        CaseEntityStore,
        DebtorStore,
        TaxIdCipher,
        TaxIdStore,
    )

SERIES_ID: Final = "filing/case-upload"

# The `taxid.read` row's purpose for this file — distinct from `b121` and
# `petition_review`, so the access log says the SSN was opened to be sent to
# the court in the Case Upload file.
TAX_ID_PURPOSE: Final = "case_upload"

# The upload's file name and its place in the filing set: first, before the
# petition (FLMB's guide, ADR 0024 source S19: Debtor.txt, Petition.pdf in
# the prescribed order, Creditor.txt).
DOCUMENT_KEY: Final = "case_upload"

REQUIREMENTS: Final = ("always", "voluntary", "court", "no", "ignored")
TYPES: Final = (
    "literal",
    "ignored",
    "unused",
    "code",
    "letters",
    "money",
    "signed_money",
    "integer",
    "ssn",
    "ein",
    "case_number",
    "office_code",
    "county_code",
    "naics",
    "text",
)
RECORD_FIELD_COUNTS: Final = {"stat": 80, "debt": 21, "alas": 8}


# ── The specification, as data ──────────────────────────────────


@dataclass(frozen=True)
class FieldSpec:
    number: int
    name: str
    required: str
    width: int | None
    type: str
    values: tuple[str, ...] = ()
    max_count: int | None = None
    max: int | None = None


@dataclass(frozen=True)
class RecordSpec:
    kind: str
    name: str
    trailing_delimiter: bool
    fields: tuple[FieldSpec, ...]

    def field(self, number: int) -> FieldSpec:
        return self.fields[number - 1]


@dataclass(frozen=True)
class CaseUploadSpec:
    release_id: str
    effective_date: date
    source_url: str
    file_name: str
    delimiter: str
    record_separator: str
    accepted_stat_field_counts: tuple[int, ...]
    records: Mapping[str, RecordSpec]


def _fail(where: str, problem: str) -> ValueError:
    return ValueError(f"malformed case-upload spec {where}: {problem}")


def _field_spec(raw: object, where: str, expected: int) -> FieldSpec:
    if not isinstance(raw, dict):
        raise _fail(where, "a field is not an object")
    number = raw.get("n")
    if number != expected:
        raise _fail(where, f"field {expected} is numbered {number!r}")
    name, required, kind = raw.get("name"), raw.get("required"), raw.get("type")
    width = raw.get("width")
    if not isinstance(name, str) or not name:
        raise _fail(where, f"field {expected} has no name")
    if required not in REQUIREMENTS:
        raise _fail(where, f"field {expected} required={required!r}")
    if kind not in TYPES:
        raise _fail(where, f"field {expected} type={kind!r}")
    if width is not None and (not isinstance(width, int) or width < 0):
        raise _fail(where, f"field {expected} width={width!r}")
    values = raw.get("values", [])
    if not isinstance(values, list) or not all(isinstance(v, str) for v in values):
        raise _fail(where, f"field {expected} values")
    if kind in ("literal", "code", "letters") and not values:
        raise _fail(where, f"field {expected} is a {kind} with no values")
    max_count, maximum = raw.get("max_count"), raw.get("max")
    return FieldSpec(
        number=expected,
        name=name,
        required=str(required),
        width=width,
        type=str(kind),
        values=tuple(values),
        max_count=max_count if isinstance(max_count, int) else None,
        max=maximum if isinstance(maximum, int) else None,
    )


def load_spec(release_dir: Traversable) -> CaseUploadSpec:
    """One release directory: its manifest and its spec, checked whole — a
    field count that is not the spec's (80 / 21 / 8), a gap in the
    numbering, an unknown type or requirement fails the load."""
    where = release_dir.name
    manifest = json.loads(release_dir.joinpath("manifest.json").read_text("utf-8"))
    raw = json.loads(release_dir.joinpath("spec.json").read_text("utf-8"))
    if manifest.get("series_id") != SERIES_ID:
        raise _fail(where, "manifest names another series")
    records: dict[str, RecordSpec] = {}
    raw_records = raw.get("records")
    if not isinstance(raw_records, dict) or set(raw_records) != set(
        RECORD_FIELD_COUNTS
    ):
        raise _fail(where, "records must be exactly stat, debt and alas")
    for kind, count in RECORD_FIELD_COUNTS.items():
        record = raw_records[kind]
        raw_fields = record.get("fields")
        if not isinstance(raw_fields, list) or len(raw_fields) != count:
            raise _fail(where, f"{kind} must have {count} fields")
        if record["fields"][0].get("values") != [kind]:
            raise _fail(where, f"{kind} field 1 must be the literal {kind!r}")
        records[kind] = RecordSpec(
            kind=kind,
            name=str(record.get("name", kind)),
            trailing_delimiter=bool(record.get("trailing_delimiter")),
            fields=tuple(
                _field_spec(f, f"{where}/{kind}", n)
                for n, f in enumerate(raw_fields, start=1)
            ),
        )
    effective = date.fromisoformat(str(manifest["effective_date"]))
    sequence = int(manifest.get("sequence", 1))
    release_id = f"{SERIES_ID}@{effective.isoformat()}" + (
        "" if sequence == 1 else f"+{sequence}"
    )
    counts = raw.get("accepted_stat_field_counts")
    if not isinstance(counts, list) or RECORD_FIELD_COUNTS["stat"] not in counts:
        raise _fail(where, "accepted_stat_field_counts must include 80")
    return CaseUploadSpec(
        release_id=release_id,
        effective_date=effective,
        source_url=str(manifest["source"]["url"]),
        file_name=str(raw["file_name"]),
        delimiter=str(raw["delimiter"]),
        record_separator=str(raw["record_separator"]),
        accepted_stat_field_counts=tuple(int(c) for c in counts),
        records=records,
    )


@cache
def spec_releases() -> tuple[CaseUploadSpec, ...]:
    root = resources.files("insolvia_api").joinpath("regulatory/filing/case-upload")
    releases = [load_spec(d) for d in root.iterdir() if d.is_dir()]
    return tuple(sorted(releases, key=lambda r: r.effective_date))


def resolve_spec(as_of: date) -> CaseUploadSpec:
    applicable = [r for r in spec_releases() if r.effective_date <= as_of]
    if not applicable:
        raise LookupError(f"no release of {SERIES_ID} is effective on {as_of}")
    return applicable[-1]


# ── Problems — never a value ────────────────────────────────────


@dataclass(frozen=True)
class CaseUploadProblem:
    """One thing wrong with the file or with what it would be built from.
    `line` is the record's 1-based line, `field` its 1-based field number
    (0 for a whole-record or whole-file problem). The message names the
    record, the field and the fault — NEVER the field's content: the debtor
    record carries the full SSN, and a problem is shown, logged and stored."""

    line: int
    record: str
    field: int
    code: str
    message: str


class CaseUploadError(ValueError):
    """The file cannot be built or does not validate. Carries the problems;
    its message is their messages, which name no value."""

    def __init__(self, problems: Sequence[CaseUploadProblem]) -> None:
        self.problems = tuple(problems)
        super().__init__("; ".join(p.message for p in self.problems))


# ── The validator ───────────────────────────────────────────────

_PRINTABLE: Final = re.compile(r"^[\x20-\x7e]*$")
_MONEY: Final = re.compile(r"^\d{1,12}\.\d{2}$")
_SIGNED_MONEY: Final = re.compile(r"^-?\d{1,12}\.\d{2}$")
_PATTERNS: Final = {
    "ssn": re.compile(r"^\d{3}-\d{2}-\d{4}$"),
    "ein": re.compile(r"^\d{2}-\d{7}$"),
    "case_number": re.compile(r"^\d{2}-\d{5}$"),
    "office_code": re.compile(r"^[0-9A-Za-z]$"),
    "county_code": re.compile(r"^\d{5}$"),
    "naics": re.compile(r"^\d{4}$"),
}


def _check_field(
    spec: FieldSpec, value: str, *, line: int, record: str, add: Callable[..., None]
) -> None:
    def fault(code: str, what: str) -> None:
        add(line, record, spec.number, code, f"{what}")

    if spec.width is not None and len(value) > spec.width:
        fault("width", f"is longer than its {spec.width} characters")
        return
    if value == "":
        return
    kind = spec.type
    if kind in ("ignored",):
        return
    if kind == "unused":
        fault("not_blank", "must be blank")
    elif kind in ("literal", "code"):
        if value not in spec.values:
            fault("code", "is not one of the permitted values")
    elif kind == "letters":
        letters = list(value)
        if (
            any(letter not in spec.values for letter in letters)
            or len(set(letters)) != len(letters)
            or (spec.max_count is not None and len(letters) > spec.max_count)
        ):
            fault("code", "is not a permitted combination of values")
    elif kind == "money":
        if not _MONEY.match(value):
            fault("format", "is not an amount 0.00-999999999999.99")
    elif kind == "signed_money":
        if not _SIGNED_MONEY.match(value):
            fault("format", "is not a signed amount with two decimals")
    elif kind == "integer":
        if not value.isdigit() or (spec.max is not None and int(value) > spec.max):
            fault("format", "is not a whole number in range")
    elif kind == "text":
        pass  # the character set is checked for the whole line
    else:
        pattern = _PATTERNS[kind]
        if not pattern.match(value):
            fault("format", f"is not in the {kind} format")


def _split(line_text: str, record: RecordSpec, delimiter: str) -> list[str] | None:
    parts = line_text.split(delimiter)
    count = len(record.fields)
    if record.trailing_delimiter:
        if len(parts) != count + 1 or parts[-1] != "":
            return None
        return parts[:-1]
    if len(parts) != count:
        return None
    return parts


def validate_debtor_txt(
    content: bytes | str, spec: CaseUploadSpec
) -> tuple[CaseUploadProblem, ...]:
    """Every problem with a Debtor.txt against the encoded spec: the file's
    shape (separator, character set, record order and counts), each record's
    field count, every field's width, type, code set and requirement, and
    the cross-field rules the spec's notes state. Empty means valid."""
    problems: list[CaseUploadProblem] = []

    def add(line: int, record: str, number: int, code: str, what: str) -> None:
        if number:
            name = spec.records[record].field(number).name if record else ""
            message = f"{record} record (line {line}) field {number} ({name}) {what}"
        else:
            message = f"{record or 'file'} (line {line}) {what}"
        problems.append(CaseUploadProblem(line, record, number, code, message))

    if isinstance(content, bytes):
        try:
            text = content.decode("ascii")
        except UnicodeDecodeError:
            add(0, "", 0, "charset", "is not ASCII")
            return tuple(problems)
    else:
        text = content
    separator = spec.record_separator
    if not text.endswith(separator):
        add(0, "", 0, "separator", "does not end with the record separator")
    lines = text.split(separator)
    if lines and lines[-1] == "":
        lines = lines[:-1]
    if not lines:
        add(0, "", 0, "empty", "has no records")
        return tuple(problems)

    stat_values: list[str] | None = None
    roles: list[str] = []
    seen: list[str] = []
    aliases: list[tuple[int, str]] = []
    for number, line_text in enumerate(lines, start=1):
        if "\r" in line_text or not _PRINTABLE.match(line_text):
            add(number, "", 0, "charset", "holds a character outside printable ASCII")
            continue
        kind = line_text.split(spec.delimiter, 1)[0]
        record = spec.records.get(kind)
        if record is None:
            add(number, "", 0, "record_type", "is not a stat, debt or alas record")
            continue
        seen.append(kind)
        values = _split(line_text, record, spec.delimiter)
        if values is None:
            add(
                number,
                kind,
                0,
                "field_count",
                f"does not have the {len(record.fields)} fields of the spec",
            )
            continue
        for field_spec, value in zip(record.fields, values, strict=True):
            _check_field(field_spec, value, line=number, record=kind, add=add)
        if kind == "stat":
            stat_values = values
        elif kind == "debt":
            roles.append(values[1])
            _check_debtor(values, number, stat_values, add)
        else:
            aliases.append((number, values[1]))
            if values[5] == "":
                add(number, "alas", 6, "required", "is required")

    _check_order(seen, add)
    expected_roles = ["db", "jdb"][: len(roles)]
    if roles != expected_roles:
        add(
            0, "debt", 2, "role", "must be db on the first debtor and jdb on the second"
        )
    for number, role in aliases:
        if role not in roles:
            add(number, "alas", 2, "role", "names no debtor record's party role")
    if stat_values is not None:
        _check_stat(stat_values, len(roles), add)
    return tuple(problems)


def _check_order(seen: list[str], add: Callable[..., None]) -> None:
    if seen.count("stat") != 1 or (seen and seen[0] != "stat"):
        add(0, "stat", 0, "order", "must be exactly one record, and the first")
    debts = seen.count("debt")
    if debts not in (1, 2):
        add(0, "debt", 0, "order", "must be one record per debtor (one or two)")
    rank = {"stat": 0, "debt": 1, "alas": 2}
    if [rank[k] for k in seen] != sorted(rank[k] for k in seen):
        add(0, "", 0, "order", "records must run stat, then debt, then alas")


def _check_debtor(
    values: list[str],
    line: int,
    stat: list[str] | None,
    add: Callable[..., None],
) -> None:
    individual = stat is not None and stat[4] == "i"
    # '**' fields: required, but for the two blanks the spec itself allows
    # (spec.json ambiguity `court-configured-required`).
    required = [5, 10, 11, 14, 15, 16, 17]
    if individual:
        required += [3, 8]
    else:
        required += [9]
    for number in sorted(required):
        if values[number - 1] == "":
            add(line, "debt", number, "required", "is required")


def _check_stat(values: list[str], debtors: int, add: Callable[..., None]) -> None:
    def blank(number: int) -> bool:
        return values[number - 1] == ""

    def fault(number: int, code: str, what: str) -> None:
        add(1, "stat", number, code, what)

    for number in (1, 5, 8, 9, 10, 11, 17, 21):
        if blank(number):
            fault(number, "required", "is required")
    voluntary = values[10] == "v"
    chapter = values[7]
    if voluntary:
        for number in (13, 14, 15, 16, 24):
            if blank(number) and chapter != "15":
                fault(number, "required", "is required on a voluntary case")
    nature = values[5]
    if values[4] == "i":
        if len(nature) > 1 or (nature and nature not in "hrsbo"):
            fault(6, "rule", "allows one of h, r, s, b, o for an individual")
    elif not nature:
        fault(6, "required", "is required for a debtor that is not an individual")
    elif len(nature) == 2 and "n" not in nature:
        fault(6, "rule", "allows two values only when one is n")
    if values[8] == "w" and not (chapter == "7" and voluntary):
        fault(9, "rule", "allows w only on a voluntary Chapter 7 case")
    if chapter in ("9", "12") and values[9] not in ("", "b"):
        fault(10, "rule", "must be b on Chapter 9 and 12")
    if not voluntary and chapter not in ("7", "11"):
        fault(8, "rule", "allows only Chapter 7 or 11 on an involuntary case")
    if chapter != "15" and not blank(23):
        fault(23, "rule", "is valid only on Chapter 15")
    if debtors < 1:
        fault(0, "order", "has no debtor record")


# ── The builder ─────────────────────────────────────────────────


@dataclass(frozen=True)
class CaseUploadFile:
    """The built file. Its bytes stay out of the repr — they hold the SSN."""

    file_name: str
    spec_release: str
    content: bytes = field(repr=False)

    @property
    def sha256(self) -> str:
        return hashlib.sha256(self.content).hexdigest()


class _Problems:
    def __init__(self) -> None:
        self.items: list[CaseUploadProblem] = []

    def add(self, record: str, number: int, code: str, message: str) -> None:
        self.items.append(CaseUploadProblem(0, record, number, code, message))


def _ascii(value: str | None, record: str, number: int, problems: _Problems) -> str:
    """The value as printable ASCII: accents folded, whitespace collapsed,
    the delimiter refused. Never echoes the value in a problem."""
    if value is None:
        return ""
    folded = unicodedata.normalize("NFKD", value)
    folded = "".join(c for c in folded if not unicodedata.combining(c))
    folded = " ".join(folded.split())
    if not _PRINTABLE.match(folded) or "|" in folded:
        problems.add(
            record,
            number,
            "charset",
            f"{record} field {number} holds a character the file cannot carry",
        )
        return ""
    return folded


def _money(value: Decimal | None) -> str:
    return "" if value is None else f"{value:.2f}"


def _parse_money(fill: object) -> Decimal | None:
    if not isinstance(fill, Text):
        return None
    try:
        return Decimal(fill.value.replace(",", ""))
    except InvalidOperation:
        return None


_CREDITOR_CODES: Final = {
    "1_49": "A",
    "50_99": "B",
    "100_199": "C",
    "200_999": "D",
    "1000_5000": "E",
    "5001_10000": "F",
    "10001_25000": "G",
    "25001_50000": "H",
    "50001_100000": "I",
    "more_than_100000": "J",
}
# B101 lines 19-20's twelve brackets onto the spec's scale; J is retired.
_DOLLAR_CODES: Final = {
    "0_50000": "A",
    "50001_100000": "B",
    "100001_500000": "C",
    "500001_1000000": "D",
    "1000001_10000000": "E",
    "10000001_50000000": "F",
    "50000001_100000000": "G",
    "100000001_500000000": "H",
    "500000001_1000000000": "I",
    "1000000001_10000000000": "K",
    "10000000001_50000000000": "L",
    "more_than_50000000000": "M",
}
_FEE_CODES: Final = {"full": "p", "installments": "i", "waiver": "w"}
_DEBT_CODES: Final = {"consumer": "c", "business": "b", "other": "o"}
_BUSINESS_CODES: Final = {
    "health_care_business": "h",
    "single_asset_real_estate": "r",
    "stockbroker": "s",
    "commodity_broker": "b",
    "none_of_the_above": "o",
}
_MARITAL_CODES: Final = {
    "not_married": "a",
    "married_not_filing_separated": "b",
    "married_not_filing_same_household": "c",
    "married_filing_jointly": "d",
}
# stat field -> the B122A-2 engine line it copies (spec.json's `form`).
_B122A2_FIELDS: Final = {
    48: "4",
    49: "6",
    50: "7c",
    51: "7f",
    52: "8",
    53: "9c",
    56: "15",
    58: "13c",
    59: "13f",
    60: "24",
    61: "29",
    62: "30",
    63: "32",
    64: "37",
    65: "38",
    66: "39c",
    67: "39d",
    69: "41a",
    70: "41b",
}


def _projected(
    case_file: CaseFile, series: str, as_of: date, problems: _Problems
) -> tuple[FieldValues, FormRelease] | None:
    try:
        release = resolve_case_form(case_file.case, series, as_of=as_of)
        return project(release, case_file), release
    except (FormProjectionError, LookupError, KeyError):
        problems.add(
            "stat",
            0,
            "projection",
            f"{series.removeprefix('form/')} cannot be projected for this case",
        )
        return None


def _column(
    values: FieldValues, release: FormRelease, field_id: str, digit: str
) -> object:
    entry = values.get(field_id)
    if isinstance(entry, dict):
        return entry.get(_column_widget(release, field_id, digit))
    return entry if digit == "1" else None


def _statistics(
    data: CaseData, case_file: CaseFile, as_of: date, problems: _Problems
) -> list[str]:
    stat = [""] * 80

    def put(number: int, value: str) -> None:
        stat[number - 1] = value

    case = data.case
    petition = case_file.petition
    put(1, "stat")
    put(5, "i")  # Insolvia files individual cases only (B101).
    if case_file.sole_proprietorships:
        kind = case_file.sole_proprietorships[0].business_type
        put(6, _BUSINESS_CODES.get(kind or "", ""))
    put(8, str(case.chapter))
    put(11, "v")
    put(17, "n")  # Chapter 11 small business only; never a case filed here.
    if petition is None:
        problems.add("stat", 0, "petition", "the case has no petition record")
        return stat
    put(9, _FEE_CODES.get(petition.fee_handling or "", ""))
    put(10, _DEBT_CODES.get(petition.debt_character or "", ""))
    if case.chapter == 7:
        funds = petition.ch7_funds_available_for_creditors
        put(13, "" if funds is None else ("y" if funds else "n"))
    else:
        put(13, "y")
    put(14, _CREDITOR_CODES.get(petition.estimated_creditors or "", ""))
    put(15, _DOLLAR_CODES.get(petition.estimated_assets or "", ""))
    put(16, _DOLLAR_CODES.get(petition.estimated_liabilities or "", ""))
    # B101 line 9: the prior cases the debtor declared (the last 8 years).
    put(24, "y" if case_file.prior_cases else "n")

    summary = _projected(case_file, "form/b106sum", as_of, problems)
    if summary is not None:
        values, _ = summary
        for number, field_id in (
            (25, "line_1a_total_real_estate"),
            (26, "line_1b_total_personal_property"),
            (27, "line_2_secured_claims_total"),
            (28, "line_3a_priority_unsecured_total"),
            (29, "line_3b_nonpriority_unsecured_total"),
            (30, "line_4_combined_monthly_income"),
            (31, "line_5_monthly_expenses"),
            (33, "line_9g_total"),
        ):
            put(number, _money(_parse_money(values.get(field_id))))
    if case_file.exemptions:
        put(
            35,
            _money(
                sum(
                    (_claimed_amount(e, case_file) for e in case_file.exemptions),
                    Decimal("0"),
                )
            ),
        )
    income = _projected(case_file, "form/b106i", as_of, problems)
    if income is not None:
        values, release = income
        for number, field_id, digit in (
            (36, "line_2_gross_wages", "1"),
            (37, "line_2_gross_wages", "2"),
            (38, "line_6_total_deductions", "1"),
            (39, "line_6_total_deductions", "2"),
        ):
            put(number, _money(_parse_money(_column(values, release, field_id, digit))))
    expenses = _projected(case_file, "form/b106j", as_of, problems)
    if expenses is not None:
        put(40, _money(_parse_money(expenses[0].get("line_23c_net_income"))))

    _means_test(stat, case_file, problems)
    return stat


def _means_test(stat: list[str], case_file: CaseFile, problems: _Problems) -> None:
    def put(number: int, value: str) -> None:
        stat[number - 1] = value

    chapter = case_file.case.chapter
    trace = trace_means_test(case_file)
    if not trace.case.cmi.problems:
        put(32, _money(Decimal(trace.case.cmi.combined_monthly_total)))
    result = trace.result
    consumer = (
        case_file.petition is not None
        and case_file.petition.debt_character == "consumer"
    )
    if chapter != 7:
        put(21, "n")  # Chapter 7 voluntary individual consumer cases only.
        return
    if result is None:
        if consumer:
            problems.add(
                "stat",
                21,
                "means_test",
                "the means test has not reached a result for this case",
            )
        else:
            put(21, "n")
        return
    put(21, "y" if result.outcome == "presumption_of_abuse" else "n")
    inputs = trace.case.inputs
    if inputs.disabled_veteran is True:
        put(41, "y")
    if inputs.non_consumer_debts is True:
        put(42, "y")
    put(43, _MARITAL_CODES.get(trace.marital_filing_status or "", ""))
    columns = {c.column: c for c in trace.case.cmi.columns}
    if "A" in columns:
        put(44, _money(Decimal(columns["A"].monthly_total)))
    if "B" in columns:
        put(45, _money(Decimal(columns["B"].monthly_total)))
    comparison = result.comparison
    if comparison is not None:
        put(46, _money(Decimal(comparison.annual_median)))
        put(47, str(comparison.household_size))
    if result.outcome not in ("no_presumption", "presumption_of_abuse"):
        return  # below the median or exempt: 122A-2 is not filed.
    lines = {line.line: line.amount for line in result.lines}
    for number, line in _B122A2_FIELDS.items():
        if line in lines:
            put(number, _money(Decimal(lines[line])))
    vehicle = lines.get("12")
    put(55, _money(Decimal(vehicle if vehicle is not None else lines.get("14", "0"))))
    if "11" in lines:
        put(57, str(min(int(Decimal(lines["11"])), 2)))
    if result.determined_by == "unsecured_ratio":
        put(68, "u")
        put(71, "y" if result.outcome == "presumption_of_abuse" else "n")
    else:
        put(68, "y" if result.outcome == "presumption_of_abuse" else "n")


def _county_code(
    district: courts.CourtDistrict, address: Address, role: str, problems: _Problems
) -> str:
    def norm(name: str) -> str:
        cleaned = name.lower().replace("saint ", "st. ").removesuffix(" county")
        return " ".join(cleaned.split())

    county = address.county
    if not county:
        problems.add("debt", 17, "county", f"{role} has no residence county")
        return ""
    if address.state and address.state.upper() != district.state:
        problems.add("debt", 17, "county", f"{role}'s county is outside the district")
        return ""
    for division in district.divisions:
        for candidate in division.counties:
            if norm(candidate.name) == norm(county):
                return candidate.fips
    problems.add(
        "debt", 17, "county", f"{role}'s county is not one the district serves"
    )
    return ""


def _debtor_record(
    debtor: Debtor,
    party_role: str,
    *,
    office_code: str,
    district: courts.CourtDistrict,
    tax_ids: Mapping[str, str],
    problems: _Problems,
    trailing: bool,
) -> list[str]:
    role = debtor.filing_role
    record = [""] * 21
    name = debtor.name
    record[0] = "debt"
    record[1] = party_role
    record[2] = _ascii(name.given, "debt", 3, problems)
    record[3] = _ascii(name.middle, "debt", 4, problems)
    record[4] = _ascii(name.surname, "debt", 5, problems)
    record[6] = _ascii(name.suffix, "debt", 7, problems)
    digits = tax_ids.get(role)
    if digits is None:
        problems.add("debt", 8, "tax_id", f"{role}'s tax id is not available")
    elif len(digits) != 9 or not digits.isdigit():
        problems.add("debt", 8, "tax_id", f"{role}'s tax id is not nine digits")
    else:
        record[7] = f"{digits[:3]}-{digits[3:5]}-{digits[5:]}"
    record[8] = ",".join(debtor.employer_ids)
    record[9] = office_code
    mailing = debtor.mailing_address
    address = mailing if mailing.line1 else debtor.residence_address
    record[10] = _ascii(address.line1, "debt", 11, problems)
    record[11] = _ascii(address.line2, "debt", 12, problems)
    record[13] = _ascii(address.city, "debt", 14, problems)
    record[14] = _ascii((address.state or "").upper() or None, "debt", 15, problems)
    record[15] = _ascii(address.postal_code, "debt", 16, problems)
    record[16] = _county_code(district, debtor.residence_address, role, problems)
    return [*record, ""] if trailing else record


def _alias_records(
    debtor: Debtor, party_role: str, problems: _Problems, trailing: bool
) -> list[list[str]]:
    records: list[list[str]] = []
    for alias in debtor.other_names_used:
        record = [""] * 8
        record[0], record[1], record[2] = "alas", party_role, "aka"
        if alias.business_name:
            record[5] = _ascii(alias.business_name, "alas", 6, problems)
        else:
            record[3] = _ascii(alias.given, "alas", 4, problems)
            record[4] = _ascii(alias.middle, "alas", 5, problems)
            record[5] = _ascii(alias.surname, "alas", 6, problems)
        records.append([*record, ""] if trailing else record)
    return records


def build_debtor_txt(
    data: CaseData,
    *,
    as_of: date,
    registry: courts.CourtsRelease,
    spec: CaseUploadSpec,
) -> CaseUploadFile:
    """The case's Debtor.txt, from the case data with its tax ids DISCLOSED
    (`data.tax_ids` filled by `disclose_tax_ids`), validated against `spec`
    before it is returned. Raises CaseUploadError naming every problem —
    what cannot be built and what does not validate — and never a value."""
    problems = _Problems()
    case = data.case
    district = registry.district(case.court) if case.court else None
    division = (
        district.division(case.division)
        if district is not None and case.division
        else None
    )
    if district is None or division is None:
        problems.add("debt", 10, "court", "the case has no court and division")
        raise CaseUploadError(problems.items)
    office_code = division.office_code.value
    if not office_code:
        problems.add("debt", 10, "office", "the division has no CM/ECF office code")
        raise CaseUploadError(problems.items)

    case_file = to_case_file(data)
    debtors = [d for d in data.debtors if d.filing_role in ("debtor_1", "debtor_2")]
    debtors.sort(key=lambda d: d.filing_role)
    if not debtors or debtors[0].filing_role != "debtor_1":
        problems.add("debt", 0, "debtor", "the case has no debtor 1")
        raise CaseUploadError(problems.items)

    debt_spec, alas_spec = spec.records["debt"], spec.records["alas"]
    rows = [_statistics(data, case_file, as_of, problems)]
    party_roles = ("db", "jdb")
    for debtor, party_role in zip(debtors, party_roles, strict=False):
        rows.append(
            _debtor_record(
                debtor,
                party_role,
                office_code=office_code,
                district=district,
                tax_ids=data.tax_ids,
                problems=problems,
                trailing=debt_spec.trailing_delimiter,
            )
        )
    for debtor, party_role in zip(debtors, party_roles, strict=False):
        rows.extend(
            _alias_records(debtor, party_role, problems, alas_spec.trailing_delimiter)
        )
    if problems.items:
        raise CaseUploadError(problems.items)

    text = "".join(spec.delimiter.join(row) + spec.record_separator for row in rows)
    found = validate_debtor_txt(text, spec)
    if found:
        raise CaseUploadError(found)
    return CaseUploadFile(
        file_name=spec.file_name,
        spec_release=spec.release_id,
        content=text.encode("ascii"),
    )


def case_upload_file(
    case: Case,
    *,
    principal: str,
    as_of: date,
    debtor_store: DebtorStore,
    entity_store: CaseEntityStore,
    tax_id_store: TaxIdStore,
    tax_id_cipher: TaxIdCipher,
    access_log: AccessLog,
) -> CaseUploadFile:
    """THE ONE COMPOSITION, for the filing worker at upload time: read the
    case, open each debtor's sealed tax id under purpose `case_upload` (a
    logged `taxid.read` row per debtor, naming `principal`), build and
    validate. The result lives in the caller's memory for the length of the
    upload and is never stored. No route calls this."""
    data = read_case_data(case, debtor_store=debtor_store, entity_store=entity_store)
    disclosed = disclose_tax_ids(
        data,
        principal=principal,
        purpose=TAX_ID_PURPOSE,
        tax_id_store=tax_id_store,
        tax_id_cipher=tax_id_cipher,
        access_log=access_log,
    )
    return build_debtor_txt(
        disclosed,
        as_of=as_of,
        registry=courts.resolve(as_of),
        spec=resolve_spec(as_of),
    )
