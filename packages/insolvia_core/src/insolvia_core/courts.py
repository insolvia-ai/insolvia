"""The court registry: the `courts/us-bankruptcy` series (issue #360 / 14.8).

WHAT A DISTRICT RECORD IS FOR. `case.district` was free text because the
court list belonged to the e-filing work — and that work (ADR 0024) settled
what a district record needs: the identity a filing prints, the divisions a
case is opened in (with the CM/ECF office code and the counties each serves,
which is how venue picks a division), the court's PDF and creditor-matrix
rules, how the debtor's signature and the SSN statement reach the docket,
whether the court's Case Upload facility has been proven against our data,
and a source with a date for every one of those facts. This module is the
loader for that record and the resolver a case reference is validated
against; the records themselves are committed releases under
`insolvia_core/regulatory/courts/us-bankruptcy/<effective_date>/`, one JSON
file per district, per ADR 0014's release layout (docs/reference/
effective-dating.md owns the shape of a release; this module owns the
payload's).

WHY THE REGISTRY LIVES IN THIS PACKAGE and not beside the API's other
series. Every other regulatory series is read by the API alone. This one is
read by the CASE DOMAIN — `cases.parse_case_creation` refuses a court and
division the registry does not know — and the case domain has two writers:
the API's routes and the admin service's seeder, which parses fixture cases
with the same function. A registry the seeder could not reach would leave the
fixtures unvalidated, so the series ships inside the package both services
install. ADR 0014's rule is "beside the thing that reads it", and the thing
that reads this one is here.

EVERY FACT IS WRAPPED. Court rules are read off web pages that move, and
ADR 0024 found the Texas statewide procedures (2017) disagreeing with a
court's own 2025 document on the same figure. So a fact is a `Fact`: a value,
a `verified`/`unverified` status, the source it was read from and the date it
was read. The loader refuses a `verified` fact with no source or no date, and
an `unverified` one may carry a null value — "we do not know" is a legal
answer here, and a better one than a guess. A consumer that needs a figure
(the packet's PDF check, the matrix format) reads the value and decides what
an unverified one means for it.

Stdlib only: reading the registry shipped inside this package is
configuration access, not an external dependency, and the architecture test
holds the domain modules to that.
"""

from __future__ import annotations

import json
import re
from collections.abc import Mapping
from dataclasses import dataclass
from datetime import date
from functools import cache
from importlib import resources
from importlib.resources.abc import Traversable
from typing import Final, Generic, TypeVar

SERIES_ID: Final = "courts/us-bankruptcy"

FACT_STATUSES: Final = ("verified", "unverified")

# How the court's Case Upload facility stands for our packages (ADR 0024
# "Case Upload is real, per-court, and still the attorney's act"): unproven
# until a training-database session says otherwise, then the legacy
# pipe-delimited text file or the XML case opening.
CASE_UPLOAD_STATUSES: Final = ("unverified", "legacy_txt", "xml")

# How Official Form B121 (the SSN statement) reaches the court: filed as its
# own docket event, not filed at all (the full SSN goes on the opening screen
# instead), or inside the petition package.
SSN_STATEMENT_HANDLINGS: Final = ("own_event", "not_filed", "with_petition")

_CODE_RE: Final = re.compile(r"^[a-z]{4}$")
_DIVISION_CODE_RE: Final = re.compile(r"^[a-z][a-z0-9-]{1,40}$")
_FIPS_RE: Final = re.compile(r"^\d{5}$")
_DIRNAME_RE: Final = re.compile(r"^(\d{4}-\d{2}-\d{2})(?:\+(\d+))?$")

# Census state FIPS prefixes for the states a district may sit in. A county
# code from another state inside a division is a data-entry slip the loader
# can catch, so it does.
_STATE_FIPS: Final = {"FL": "12", "GA": "13", "TX": "48"}

T = TypeVar("T")


@dataclass(frozen=True)
class Fact(Generic[T]):
    """One court fact with its provenance — see the module docstring."""

    value: T | None
    status: str
    source: str | None
    verified_at: date | None
    note: str = ""

    @property
    def verified(self) -> bool:
        return self.status == "verified"


@dataclass(frozen=True)
class Source:
    """One page a record's facts were read from, and when."""

    id: str
    title: str
    url: str
    read: date


@dataclass(frozen=True)
class County:
    """A county a division serves, by its FIPS-5 code — the code Case Upload
    field 17 and the IEPD's `USCountyCode` both use."""

    name: str
    fips: str


@dataclass(frozen=True)
class Courthouse:
    name: str
    line1: str
    line2: str | None
    city: str
    state: str
    postal_code: str


@dataclass(frozen=True)
class Division:
    """One division of a district: where a case is opened, and which
    counties send their debtors there."""

    code: str
    name: str
    # The digit before the colon in `8:26-bk-01234` — Case Upload field 10.
    office_code: Fact[str]
    courthouse: Fact[Courthouse]
    counties: tuple[County, ...]
    counties_source: str


@dataclass(frozen=True)
class CmEcf:
    live_url: Fact[str]
    training_url: Fact[str]
    help_desk: Fact[str]
    release: Fact[str]


@dataclass(frozen=True)
class PdfRules:
    max_bytes: Fact[int]
    text_searchable_required: Fact[bool]
    flatten_required: Fact[bool]
    pdf_a_required: Fact[bool]


@dataclass(frozen=True)
class MatrixRules:
    """The creditor-matrix knobs — the same numbers the API's
    `creditor_matrix.MatrixFormat` renders with, now owned by the record
    rather than a dictionary in code."""

    max_line_chars: Fact[int]
    max_creditor_lines: Fact[int]
    blank_lines_between: Fact[int]
    pad_to_lines: Fact[int]
    name_line_chars: Fact[int]
    comma_after_city: Fact[bool]
    case_number_header_when_separate: Fact[bool]
    certification_required: Fact[bool]


@dataclass(frozen=True)
class SignatureInstrument:
    """How the debtor's wet-ink signature reaches an electronic filing."""

    kind: str
    title: str
    wet_ink_required: bool | None
    retention: str | None
    own_docket_event: bool | None
    form_url: str | None


@dataclass(frozen=True)
class LocalForm:
    id: str
    title: str
    url: str | None
    when_required: str
    source: str


@dataclass(frozen=True)
class FeeRule:
    deadline: str
    note: str


@dataclass(frozen=True)
class Opening:
    signature_instrument: Fact[SignatureInstrument]
    ssn_statement: Fact[str]
    local_forms: tuple[LocalForm, ...]
    fee_rule: Fact[FeeRule]


@dataclass(frozen=True)
class CaseUpload:
    status: str
    kind: str | None
    verified_at: date | None
    note: str


@dataclass(frozen=True)
class Registration:
    training_required: Fact[bool]
    notes: str


@dataclass(frozen=True)
class CourtDistrict:
    """One bankruptcy court, as the registry describes it."""

    code: str
    court_id: str | None
    name: str
    state: str
    circuit: int
    website: str
    divisions: tuple[Division, ...]
    cmecf: CmEcf
    pdf: PdfRules
    matrix: MatrixRules
    opening: Opening
    case_upload: CaseUpload
    registration: Registration
    sources: tuple[Source, ...]
    notes: str

    def division(self, code: str) -> Division | None:
        return next((d for d in self.divisions if d.code == code), None)

    def division_for_county(self, fips: str) -> Division | None:
        """The division a county's debtors file in — venue's answer."""
        return next(
            (d for d in self.divisions if any(c.fips == fips for c in d.counties)),
            None,
        )


@dataclass(frozen=True)
class CourtsRelease:
    """One immutable release of the series (effective-dating.md)."""

    series_id: str
    effective_date: date
    sequence: int
    source_url: str
    source_published: date | None
    source_sha256: str | None
    notes: str
    districts: tuple[CourtDistrict, ...]

    @property
    def release_id(self) -> str:
        base = f"{self.series_id}@{self.effective_date.isoformat()}"
        return base if self.sequence == 1 else f"{base}+{self.sequence}"

    def district(self, code: str) -> CourtDistrict | None:
        return next((d for d in self.districts if d.code == code), None)


# --- Loading and validation --------------------------------------------------


def _fail(where: str, problem: str) -> ValueError:
    return ValueError(f"malformed court record {where}: {problem}")


def _str_field(data: Mapping[str, object], key: str, where: str) -> str:
    value = data.get(key)
    if not isinstance(value, str) or not value.strip():
        raise _fail(where, f"{key} missing or empty")
    return value


def _optional_str(data: Mapping[str, object], key: str, where: str) -> str | None:
    value = data.get(key)
    if value is None:
        return None
    if not isinstance(value, str):
        raise _fail(where, f"{key} must be a string or null")
    return value


def _notes(data: Mapping[str, object], key: str, where: str) -> str:
    """A free-text note: absent and null both read as empty."""
    value = data.get(key)
    if value is None:
        return ""
    if not isinstance(value, str):
        raise _fail(where, f"{key} must be a string")
    return value


def _date(value: object, where: str, field_name: str) -> date | None:
    if value is None:
        return None
    if not isinstance(value, str):
        raise _fail(where, f"{field_name} {value!r} is not a date string")
    try:
        return date.fromisoformat(value)
    except ValueError as exc:
        raise _fail(where, f"{field_name} {value!r}: {exc}") from exc


def _sources(raw: object, where: str) -> tuple[Source, ...]:
    if not isinstance(raw, list) or not raw:
        raise _fail(where, "sources missing or empty")
    parsed: list[Source] = []
    for entry in raw:
        if not isinstance(entry, dict):
            raise _fail(where, "source is not an object")
        read = _date(entry.get("read"), where, "source.read")
        if read is None:
            raise _fail(where, "source.read missing")
        parsed.append(
            Source(
                id=_str_field(entry, "id", where),
                title=_str_field(entry, "title", where),
                url=_str_field(entry, "url", where),
                read=read,
            )
        )
    ids = [s.id for s in parsed]
    if len(ids) != len(set(ids)):
        raise _fail(where, "duplicate source ids")
    return tuple(parsed)


class _Facts:
    """Parses `Fact` wrappers against one record's source list, so a fact
    citing a source the record does not list fails the load."""

    def __init__(self, source_ids: frozenset[str], where: str) -> None:
        self.source_ids = source_ids
        self.where = where

    def raw(self, data: Mapping[str, object], key: str) -> tuple[object, Fact[object]]:
        where = f"{self.where} {key}"
        wrapper = data.get(key)
        if not isinstance(wrapper, dict):
            raise _fail(where, "must be a fact object {value, status, ...}")
        status = wrapper.get("status")
        if status not in FACT_STATUSES:
            raise _fail(where, f"status must be one of {FACT_STATUSES}")
        source = _optional_str(wrapper, "source", where)
        if source is not None and source not in self.source_ids:
            raise _fail(where, f"source {source!r} is not in the record's sources")
        verified_at = _date(wrapper.get("verified_at"), where, "verified_at")
        value = wrapper.get("value")
        # A verified fact may still carry null — "the page states no fixed
        # block size" is a verified absence — but it must say where and when
        # that was read.
        if status == "verified" and (source is None or verified_at is None):
            raise _fail(where, "a verified fact needs a source and verified_at")
        return value, Fact(
            value=None,
            status=str(status),
            source=source,
            verified_at=verified_at,
            note=_notes(wrapper, "note", where),
        )

    def string(self, data: Mapping[str, object], key: str) -> Fact[str]:
        value, fact = self.raw(data, key)
        if value is not None and not isinstance(value, str):
            raise _fail(f"{self.where} {key}", "value must be a string or null")
        return Fact(value, fact.status, fact.source, fact.verified_at, fact.note)

    def choice(
        self, data: Mapping[str, object], key: str, allowed: tuple[str, ...]
    ) -> Fact[str]:
        fact = self.string(data, key)
        if fact.value is not None and fact.value not in allowed:
            raise _fail(f"{self.where} {key}", f"value must be one of {allowed}")
        return fact

    def integer(self, data: Mapping[str, object], key: str) -> Fact[int]:
        value, fact = self.raw(data, key)
        if value is not None and (
            isinstance(value, bool) or not isinstance(value, int)
        ):
            raise _fail(f"{self.where} {key}", "value must be an integer or null")
        return Fact(value, fact.status, fact.source, fact.verified_at, fact.note)

    def boolean(self, data: Mapping[str, object], key: str) -> Fact[bool]:
        value, fact = self.raw(data, key)
        if value is not None and not isinstance(value, bool):
            raise _fail(f"{self.where} {key}", "value must be a boolean or null")
        return Fact(value, fact.status, fact.source, fact.verified_at, fact.note)

    def mapping(
        self, data: Mapping[str, object], key: str
    ) -> tuple[Mapping[str, object] | None, Fact[object]]:
        value, fact = self.raw(data, key)
        if value is not None and not isinstance(value, dict):
            raise _fail(f"{self.where} {key}", "value must be an object or null")
        return value, fact


def _courthouse(raw: Mapping[str, object], where: str) -> Courthouse:
    return Courthouse(
        name=_str_field(raw, "name", where),
        line1=_str_field(raw, "line1", where),
        line2=_optional_str(raw, "line2", where),
        city=_str_field(raw, "city", where),
        state=_str_field(raw, "state", where),
        postal_code=_str_field(raw, "postal_code", where),
    )


def _division(raw: object, where: str, facts: _Facts, state: str) -> Division:
    if not isinstance(raw, dict):
        raise _fail(where, "division is not an object")
    code = _str_field(raw, "code", where)
    where = f"{where} division {code}"
    if not _DIVISION_CODE_RE.match(code):
        raise _fail(where, "code must be a lowercase slug")
    division_facts = _Facts(facts.source_ids, where)
    courthouse_raw, courthouse_fact = division_facts.mapping(raw, "courthouse")
    courthouse = Fact[Courthouse](
        _courthouse(courthouse_raw, f"{where} courthouse")
        if courthouse_raw is not None
        else None,
        courthouse_fact.status,
        courthouse_fact.source,
        courthouse_fact.verified_at,
        courthouse_fact.note,
    )
    counties_raw = raw.get("counties")
    if not isinstance(counties_raw, list) or not counties_raw:
        raise _fail(where, "counties missing or empty")
    counties: list[County] = []
    for entry in counties_raw:
        if not isinstance(entry, dict):
            raise _fail(where, "county is not an object")
        fips = _str_field(entry, "fips", where)
        if not _FIPS_RE.match(fips) or not fips.startswith(_STATE_FIPS[state]):
            raise _fail(where, f"county fips {fips!r} is not a {state} FIPS-5 code")
        counties.append(County(name=_str_field(entry, "name", where), fips=fips))
    counties_source = _str_field(raw, "counties_source", where)
    if counties_source not in facts.source_ids:
        raise _fail(where, f"counties_source {counties_source!r} is not a source")
    return Division(
        code=code,
        name=_str_field(raw, "name", where),
        office_code=division_facts.string(raw, "office_code"),
        courthouse=courthouse,
        counties=tuple(counties),
        counties_source=counties_source,
    )


def _local_forms(
    raw: object, where: str, source_ids: frozenset[str]
) -> tuple[LocalForm, ...]:
    if raw is None:
        return ()
    if not isinstance(raw, list):
        raise _fail(where, "local_forms must be a list")
    forms: list[LocalForm] = []
    for entry in raw:
        if not isinstance(entry, dict):
            raise _fail(where, "local form is not an object")
        source = _str_field(entry, "source", where)
        if source not in source_ids:
            raise _fail(where, f"local form source {source!r} is not a source")
        forms.append(
            LocalForm(
                id=_str_field(entry, "id", where),
                title=_str_field(entry, "title", where),
                url=_optional_str(entry, "url", where),
                when_required=_str_field(entry, "when_required", where),
                source=source,
            )
        )
    return tuple(forms)


def _opening(raw: object, where: str, facts: _Facts) -> Opening:
    if not isinstance(raw, dict):
        raise _fail(where, "opening is not an object")
    where = f"{where} opening"
    opening_facts = _Facts(facts.source_ids, where)
    instrument_raw, instrument_fact = opening_facts.mapping(raw, "signature_instrument")
    instrument: SignatureInstrument | None = None
    if instrument_raw is not None:
        iw = f"{where} signature_instrument"
        wet_ink = instrument_raw.get("wet_ink_required")
        own_event = instrument_raw.get("own_docket_event")
        if wet_ink is not None and not isinstance(wet_ink, bool):
            raise _fail(iw, "wet_ink_required must be a boolean or null")
        if own_event is not None and not isinstance(own_event, bool):
            raise _fail(iw, "own_docket_event must be a boolean or null")
        instrument = SignatureInstrument(
            kind=_str_field(instrument_raw, "kind", iw),
            title=_str_field(instrument_raw, "title", iw),
            wet_ink_required=wet_ink,
            retention=_optional_str(instrument_raw, "retention", iw),
            own_docket_event=own_event,
            form_url=_optional_str(instrument_raw, "form_url", iw),
        )
    fee_raw, fee_fact = opening_facts.mapping(raw, "fee_rule")
    fee = (
        FeeRule(
            deadline=_str_field(fee_raw, "deadline", f"{where} fee_rule"),
            note=_notes(fee_raw, "note", f"{where} fee_rule"),
        )
        if fee_raw is not None
        else None
    )
    return Opening(
        signature_instrument=Fact(
            instrument,
            instrument_fact.status,
            instrument_fact.source,
            instrument_fact.verified_at,
            instrument_fact.note,
        ),
        ssn_statement=opening_facts.choice(
            raw, "ssn_statement", SSN_STATEMENT_HANDLINGS
        ),
        local_forms=_local_forms(raw.get("local_forms"), where, facts.source_ids),
        fee_rule=Fact(
            fee, fee_fact.status, fee_fact.source, fee_fact.verified_at, fee_fact.note
        ),
    )


def _case_upload(raw: object, where: str) -> CaseUpload:
    if not isinstance(raw, dict):
        raise _fail(where, "case_upload is not an object")
    where = f"{where} case_upload"
    status = raw.get("status")
    if status not in CASE_UPLOAD_STATUSES:
        raise _fail(where, f"status must be one of {CASE_UPLOAD_STATUSES}")
    verified_at = _date(raw.get("verified_at"), where, "verified_at")
    if status != "unverified" and verified_at is None:
        raise _fail(where, "a verified Case Upload status needs verified_at")
    return CaseUpload(
        status=str(status),
        kind=_optional_str(raw, "kind", where),
        verified_at=verified_at,
        note=_notes(raw, "note", where),
    )


def _district(raw: Mapping[str, object], where: str) -> CourtDistrict:
    code = _str_field(raw, "code", where)
    where = f"{where} {code}"
    if not _CODE_RE.match(code):
        raise _fail(where, "code must be the four-letter CM/ECF id")
    state = _str_field(raw, "state", where)
    if state not in _STATE_FIPS:
        raise _fail(where, f"state {state!r} is not a launch state (ADR 0017)")
    circuit = raw.get("circuit")
    if isinstance(circuit, bool) or not isinstance(circuit, int):
        raise _fail(where, "circuit must be an integer")
    sources = _sources(raw.get("sources"), where)
    facts = _Facts(frozenset(s.id for s in sources), where)

    divisions_raw = raw.get("divisions")
    if not isinstance(divisions_raw, list) or not divisions_raw:
        raise _fail(where, "divisions missing or empty")
    divisions = tuple(_division(d, where, facts, state) for d in divisions_raw)
    codes = [d.code for d in divisions]
    if len(codes) != len(set(codes)):
        raise _fail(where, "duplicate division codes")
    fips_seen: dict[str, str] = {}
    for division in divisions:
        for county in division.counties:
            if county.fips in fips_seen:
                raise _fail(
                    where,
                    f"county {county.fips} is in both {fips_seen[county.fips]}"
                    f" and {division.code}",
                )
            fips_seen[county.fips] = division.code

    for section in ("cmecf", "pdf", "matrix", "registration"):
        if not isinstance(raw.get(section), dict):
            raise _fail(where, f"{section} is not an object")
    cmecf_raw = raw["cmecf"]
    pdf_raw = raw["pdf"]
    matrix_raw = raw["matrix"]
    registration_raw = raw["registration"]
    assert isinstance(cmecf_raw, dict)
    assert isinstance(pdf_raw, dict)
    assert isinstance(matrix_raw, dict)
    assert isinstance(registration_raw, dict)
    cmecf_facts = _Facts(facts.source_ids, f"{where} cmecf")
    pdf_facts = _Facts(facts.source_ids, f"{where} pdf")
    matrix_facts = _Facts(facts.source_ids, f"{where} matrix")
    registration_facts = _Facts(facts.source_ids, f"{where} registration")

    return CourtDistrict(
        code=code,
        court_id=_optional_str(raw, "court_id", where),
        name=_str_field(raw, "name", where),
        state=state,
        circuit=circuit,
        website=_str_field(raw, "website", where),
        divisions=divisions,
        cmecf=CmEcf(
            live_url=cmecf_facts.string(cmecf_raw, "live_url"),
            training_url=cmecf_facts.string(cmecf_raw, "training_url"),
            help_desk=cmecf_facts.string(cmecf_raw, "help_desk"),
            release=cmecf_facts.string(cmecf_raw, "release"),
        ),
        pdf=PdfRules(
            max_bytes=pdf_facts.integer(pdf_raw, "max_bytes"),
            text_searchable_required=pdf_facts.boolean(
                pdf_raw, "text_searchable_required"
            ),
            flatten_required=pdf_facts.boolean(pdf_raw, "flatten_required"),
            pdf_a_required=pdf_facts.boolean(pdf_raw, "pdf_a_required"),
        ),
        matrix=MatrixRules(
            max_line_chars=matrix_facts.integer(matrix_raw, "max_line_chars"),
            max_creditor_lines=matrix_facts.integer(matrix_raw, "max_creditor_lines"),
            blank_lines_between=matrix_facts.integer(matrix_raw, "blank_lines_between"),
            pad_to_lines=matrix_facts.integer(matrix_raw, "pad_to_lines"),
            name_line_chars=matrix_facts.integer(matrix_raw, "name_line_chars"),
            comma_after_city=matrix_facts.boolean(matrix_raw, "comma_after_city"),
            case_number_header_when_separate=matrix_facts.boolean(
                matrix_raw, "case_number_header_when_separate"
            ),
            certification_required=matrix_facts.boolean(
                matrix_raw, "certification_required"
            ),
        ),
        opening=_opening(raw.get("opening"), where, facts),
        case_upload=_case_upload(raw.get("case_upload"), where),
        registration=Registration(
            training_required=registration_facts.boolean(
                registration_raw, "training_required"
            ),
            notes=_notes(registration_raw, "notes", f"{where} registration"),
        ),
        sources=sources,
        notes=_notes(raw, "notes", where),
    )


def _load_json(node: Traversable, where: str) -> dict[str, object]:
    try:
        parsed = json.loads(node.read_text(encoding="utf-8"))
    except (FileNotFoundError, ValueError) as exc:
        raise _fail(where, f"{node.name}: {exc}") from exc
    if not isinstance(parsed, dict):
        raise _fail(where, f"{node.name} is not a JSON object")
    return parsed


def _load_release(release_dir: Traversable) -> CourtsRelease:
    where = f"{SERIES_ID}/{release_dir.name}"
    match = _DIRNAME_RE.match(release_dir.name)
    if match is None:
        raise _fail(where, "directory name is not <effective_date>[+<sequence>]")
    effective = date.fromisoformat(match.group(1))
    sequence = int(match.group(2)) if match.group(2) else 1
    if sequence < 1:
        raise _fail(where, "sequence must be >= 1")

    manifest = _load_json(release_dir.joinpath("manifest.json"), where)
    if manifest.get("series_id") != SERIES_ID:
        raise _fail(where, f"manifest series_id {manifest.get('series_id')!r}")
    if manifest.get("effective_date") != effective.isoformat():
        raise _fail(where, "manifest effective_date disagrees with the path")
    if manifest.get("sequence") != sequence:
        raise _fail(where, "manifest sequence disagrees with the path")
    source = manifest.get("source")
    if not isinstance(source, dict):
        raise _fail(where, "manifest source missing")
    notes = manifest.get("notes")
    if not isinstance(notes, str) or not notes.strip():
        raise _fail(where, "manifest notes missing — say what this release is")

    districts_dir = release_dir.joinpath("districts")
    files = sorted(
        (node for node in districts_dir.iterdir() if node.name.endswith(".json")),
        key=lambda node: node.name,
    )
    if not files:
        raise _fail(where, "no district records under districts/")
    districts: list[CourtDistrict] = []
    for node in files:
        record = _district(_load_json(node, where), where)
        if node.name != f"{record.code}.json":
            raise _fail(where, f"{node.name} holds the record for {record.code!r}")
        districts.append(record)
    return CourtsRelease(
        series_id=SERIES_ID,
        effective_date=effective,
        sequence=sequence,
        source_url=_str_field(source, "url", where),
        source_published=_date(source.get("published"), where, "source.published"),
        source_sha256=_optional_str(source, "sha256", where),
        notes=notes,
        districts=tuple(districts),
    )


def load_registry(root: Traversable) -> tuple[CourtsRelease, ...]:
    """Load and validate every release of the series under a registry root.

    Raises ValueError on any malformed release — the loader ADR 0014 asks of
    a series' first consumer, run over the committed registry by the unit
    tests so a bad record fails a pull request rather than a filing.
    """
    series_dir = root.joinpath("courts").joinpath("us-bankruptcy")
    loaded = [
        _load_release(release_dir)
        for release_dir in sorted(series_dir.iterdir(), key=lambda n: n.name)
        if release_dir.is_dir()
    ]
    if not loaded:
        raise ValueError(f"the {SERIES_ID} series has no releases")
    loaded.sort(key=lambda r: (r.effective_date, r.sequence))
    ids = [r.release_id for r in loaded]
    if len(ids) != len(set(ids)):
        raise ValueError(f"the {SERIES_ID} series has duplicate release ids")
    return tuple(loaded)


@cache
def registry() -> tuple[CourtsRelease, ...]:
    """The committed registry, shipped inside this package (ADR 0014)."""
    return load_registry(resources.files("insolvia_core").joinpath("regulatory"))


# --- Resolution (effective-dating.md) ----------------------------------------


def pick_release(candidates: tuple[CourtsRelease, ...], as_of: date) -> CourtsRelease:
    """The release with the greatest effective_date <= as_of, ties broken by
    highest sequence. No fallback past the beginning."""
    applicable = [r for r in candidates if r.effective_date <= as_of]
    if not applicable:
        earliest = min(r.effective_date for r in candidates)
        raise LookupError(
            f"no release of {SERIES_ID} is effective on or before"
            f" {as_of.isoformat()} (series begins {earliest.isoformat()})"
        )
    return max(applicable, key=lambda r: (r.effective_date, r.sequence))


def resolve(as_of: date) -> CourtsRelease:
    return pick_release(registry(), as_of)


def get(release_id: str) -> CourtsRelease:
    """That exact release — must succeed forever for any id ever pinned."""
    for release in registry():
        if release.release_id == release_id:
            return release
    raise KeyError(f"{SERIES_ID} has no release {release_id!r}")


def latest() -> CourtsRelease:
    """Newest by (effective_date, sequence), even if still in the future."""
    return registry()[-1]


def current() -> CourtsRelease:
    """The release in force today — what a floating case resolves against.

    A court's identity does not move the way a dollar figure does, so every
    caller that merely needs to know whether a court exists reads this; the
    pinned reads belong to packet assembly, like every other series.
    """
    return resolve(date.today())


def district(code: str) -> CourtDistrict | None:
    return current().district(code)


def division(
    court_code: str, division_code: str
) -> tuple[CourtDistrict, Division] | None:
    """The court and division a case reference names, or None when the
    registry does not know the pair — the check `cases.parse_case_creation`
    refuses on."""
    court = district(court_code)
    if court is None:
        return None
    found = court.division(division_code)
    return None if found is None else (court, found)


def district_codes() -> tuple[str, ...]:
    return tuple(d.code for d in current().districts)
