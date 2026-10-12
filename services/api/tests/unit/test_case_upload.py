"""The Case Upload package (ADR 0024 build PR 9): Debtor.txt for the
reference case validates against the encoded AO spec, every kind of fault is
caught, and the full SSN never leaves the file.

The reference case is the API's own (test_packet_assembly's
`reference_case_data`, placed in FLMB by test_filing_approval's `in_court`)
— the case the filing worker files against the fake court. Its tax ids are
the SSA's advertising block (987-65-4320..4329), which the Administration
will never issue.
"""

from __future__ import annotations

import ast
import json
import logging
from dataclasses import replace
from datetime import date
from pathlib import Path

import pytest
from insolvia_api.core import case_upload
from insolvia_api.core.case_upload import (
    CaseUploadError,
    build_debtor_txt,
    case_upload_file,
    resolve_spec,
    validate_debtor_txt,
)
from insolvia_core import courts
from insolvia_core.fields import Address

from .test_filing_approval import in_court
from .test_packet_assembly import build_deps, reference_case_data

# test_filing_approval.AS_OF — the approval world's fixed far-future day.
AS_OF = date(2099, 1, 15)
SPEC = resolve_spec(AS_OF)
REGISTRY = courts.latest()
SRC = Path(case_upload.__file__).resolve().parents[1]


def data():
    return in_court(reference_case_data(), "flmb")


def build(case_data=None):
    return build_debtor_txt(
        case_data if case_data is not None else data(),
        as_of=AS_OF,
        registry=REGISTRY,
        spec=SPEC,
    )


def lines(file=None):
    content = (file or build()).content.decode("ascii")
    return [line.split("|") for line in content.split("\n")[:-1]]


def joined(rows):
    return "".join("|".join(row) + "\n" for row in rows)


def codes(problems):
    return {(p.record, p.field, p.code) for p in problems}


# ── The spec, as data ───────────────────────────────────────────


def test_the_spec_encodes_every_field_of_the_three_records():
    assert [len(SPEC.records[k].fields) for k in ("stat", "debt", "alas")] == [
        80,
        21,
        8,
    ]
    for record in SPEC.records.values():
        assert [f.number for f in record.fields] == list(
            range(1, len(record.fields) + 1)
        )


def test_the_spec_cites_its_source_and_records_its_ambiguities():
    root = SRC / "regulatory" / "filing" / "case-upload" / "2022-03-01"
    manifest = json.loads((root / "manifest.json").read_text())
    raw = json.loads((root / "spec.json").read_text())
    assert manifest["source"]["url"].startswith("https://pacer.uscourts.gov/")
    assert "S22" in raw["source"]
    ids = {a["id"] for a in raw["ambiguities"]}
    assert {"trailing-delimiter", "signed-money-width", "alias-type-table"} <= ids


# ── The done-when: the reference case validates ─────────────────


def test_the_reference_case_debtor_txt_validates_against_the_spec():
    file = build()
    assert file.file_name == "Debtor.txt"
    assert validate_debtor_txt(file.content, SPEC) == ()


def test_the_reference_case_records():
    stat, debtor_1, debtor_2, *aliases = lines()
    assert len(stat) == 80  # no trailing delimiter (spec ambiguity)
    assert len(debtor_1) == 22  # a trailing delimiter, as sampled
    assert debtor_1[-1] == ""
    assert stat[:11] == ["stat", "", "", "", "i", "o", "", "7", "i", "c", "v"]
    # B101 lines 18-20: 1-49 creditors, the $100M-500M asset bracket, the
    # $50,001-100,000 liability bracket.
    assert stat[13:16] == ["A", "H", "B"]
    assert stat[23] == "y"  # a prior case within 8 years (B101 line 9)
    assert debtor_1[:8] == [
        "debt",
        "db",
        "Ada",
        "Quinn",
        "Lovelace",
        "",
        "",
        "987-65-4321",
    ]
    assert debtor_1[8] == "12-3456789"
    # FLMB's first division (Jacksonville, office 3); Hillsborough's FIPS.
    assert debtor_1[9] == "3"
    assert debtor_1[16] == "12057"
    # The mailing address, where the court's notices go.
    assert debtor_1[10:16] == [
        "4501 Postal Way",
        "PO Box 99",
        "",
        "Tampa",
        "FL",
        "33602",
    ]
    assert debtor_2[1:8] == ["jdb", "Ben", "", "Lovelace", "", "Jr.", "987-65-4322"]
    assert [a[:6] for a in aliases] == [
        ["alas", "db", "aka", "Ada", "", "Byron"],
        ["alas", "db", "aka", "", "", "Ada's Analytical Engines"],
    ]


def test_the_statistics_are_the_forms_figures():
    stat = lines()[0]
    money = [stat[n - 1] for n in (25, 26, 27, 28, 29, 30, 31, 33)]
    assert all(value and value.count(".") == 1 for value in money)
    assert stat[40 - 1]  # 106J line 23c
    assert stat[21 - 1] in ("y", "n")


def test_the_file_is_deterministic():
    assert build().content == build().content


# ── Negative: each field type, width, requirement and code ──────


def valid_rows():
    return lines()


def problems_with(mutate):
    rows = valid_rows()
    mutate(rows)
    return codes(validate_debtor_txt(joined(rows), SPEC))


@pytest.mark.parametrize(
    ("record", "index", "value", "code"),
    [
        (0, 25, "12.5", "format"),  # money: two decimals
        (0, 25, "-1.00", "format"),  # money: unsigned
        (0, 25, "1,000.00", "format"),  # money: no grouping
        (0, 40, "-1234567890123.00", "width"),  # signed money past 15
        (0, 40, "12", "format"),  # signed money
        (0, 47, "1x", "format"),  # integer
        (0, 15, "J", "code"),  # J is retired for assets
        (0, 9, "x", "code"),  # fee status
        (0, 6, "hh", "code"),  # letters repeated
        (0, 12, "y", "width"),  # not used: width 0
        (0, 19, "AB", "not_blank"),  # reserved for future use
        (0, 72, "2026-0042", "format"),  # case number
        (0, 76, "12A4", "format"),  # NAICS
        (1, 8, "987654321", "format"),  # SSN without dashes
        (1, 9, "123456789", "format"),  # EIN
        (1, 10, "33", "width"),  # office code is one character
        (1, 10, "%", "format"),
        (1, 17, "1205", "format"),  # county code is FIPS-5
        (1, 3, "A" * 21, "width"),  # first name 20
        (1, 15, "FLA", "width"),  # state 2
        (1, 2, "dbxx", "width"),
        (3, 3, "dba", "code"),  # alias type outside the attested table
    ],
)
def test_each_field_type_is_checked(record, index, value, code):
    def mutate(rows):
        rows[record][index - 1] = value

    found = problems_with(mutate)
    kind = ("stat", "debt", "debt", "alas", "alas")[record]
    assert (kind, index, code) in found


@pytest.mark.parametrize(
    ("record", "index"),
    [
        (0, 5),
        (0, 8),
        (0, 9),
        (0, 10),
        (0, 11),
        (0, 14),
        (0, 17),
        (0, 21),
        (0, 24),
        (1, 3),
        (1, 5),
        (1, 8),
        (1, 10),
        (1, 11),
        (1, 17),
        (3, 6),
    ],
)
def test_a_missing_required_field_is_caught(record, index):
    def mutate(rows):
        rows[record][index - 1] = ""

    kind = ("stat", "debt", "debt", "alas")[record]
    assert (kind, index, "required") in problems_with(mutate)


def test_a_wrong_field_count_is_caught_the_way_the_court_refuses_it():
    def drop_one(rows):
        rows[0].pop()

    def no_trailing(rows):
        rows[1].pop()

    assert ("stat", 0, "field_count") in problems_with(drop_one)
    assert ("debt", 0, "field_count") in problems_with(no_trailing)


def test_cross_field_rules():
    def waiver_on_13(rows):
        rows[0][7] = "13"
        rows[0][8] = "w"

    def two_natures_without_n(rows):
        rows[0][4] = "c"
        rows[0][5] = "hr"

    def alias_for_nobody(rows):
        rows[3][1] = "jdb"
        del rows[2]

    assert ("stat", 9, "rule") in problems_with(waiver_on_13)
    assert ("stat", 6, "rule") in problems_with(two_natures_without_n)
    assert ("alas", 2, "role") in problems_with(alias_for_nobody)


def test_the_file_shape_is_checked():
    rows = valid_rows()
    text = joined(rows)
    assert codes(validate_debtor_txt(text.rstrip("\n"), SPEC)) >= {("", 0, "separator")}
    assert ("", 0, "charset") in codes(
        validate_debtor_txt(text.replace("\n", "\r\n"), SPEC)
    )
    assert ("", 0, "charset") in codes(validate_debtor_txt("stat|é\n".encode(), SPEC))
    swapped = joined([rows[1], rows[0], *rows[2:]])
    assert ("stat", 0, "order") in codes(validate_debtor_txt(swapped, SPEC))
    assert ("", 0, "record_type") in codes(
        validate_debtor_txt(text + "alias|db|aka||||||\n", SPEC)
    )


# ── What cannot be built ────────────────────────────────────────


def test_a_county_outside_the_district_cannot_land():
    base = data()
    moved = replace(
        base.debtors[0],
        residence_address=replace(base.debtors[0].residence_address, county="Fulton"),
    )
    with pytest.raises(CaseUploadError) as raised:
        build(replace(base, debtors=(moved, *base.debtors[1:])))
    assert ("debt", 17, "county") in codes(raised.value.problems)


def test_a_missing_tax_id_cannot_land():
    with pytest.raises(CaseUploadError) as raised:
        build(replace(data(), tax_ids={"debtor_1": "987654321"}))
    assert ("debt", 8, "tax_id") in codes(raised.value.problems)


def test_a_value_too_wide_is_refused_by_the_validator():
    base = data()
    wide = replace(
        base.debtors[0],
        mailing_address=Address(
            line1="1" * 41, city="Tampa", state="FL", postal_code="33602"
        ),
    )
    with pytest.raises(CaseUploadError) as raised:
        build(replace(base, debtors=(wide, *base.debtors[1:])))
    assert ("debt", 11, "width") in codes(raised.value.problems)


def test_accents_fold_to_ascii():
    base = data()
    accented = replace(base.debtors[0], name=replace(base.debtors[0].name, given="Adá"))
    file = build(replace(base, debtors=(accented, *base.debtors[1:])))
    assert lines(file)[1][2] == "Ada"


# ── The SSN never leaves the file ───────────────────────────────


SSN_FORMS = ("987654321", "987-65-4321", "987654322", "987-65-4322")


def _assert_no_ssn(text: str) -> None:
    for form in SSN_FORMS:
        assert form not in text


def test_the_composition_logs_one_taxid_read_per_debtor_with_its_purpose(caplog):
    # The stores as the API's packet tests build them: the tax ids SEALED,
    # so only the logged read can put the digits in the file.
    case_data = data()
    deps = build_deps(case_data)
    log = deps.access_log
    caplog.set_level(logging.DEBUG)
    file = case_upload_file(
        case_data.case,
        principal="attorney-0001",
        as_of=AS_OF,
        debtor_store=deps.debtor_store,
        entity_store=deps.entity_store,
        tax_id_store=deps.tax_id_store,
        tax_id_cipher=deps.tax_id_cipher,
        access_log=log,
    )
    assert b"987-65-4321" in file.content
    reads = [e for e in log.events if e.action == "taxid.read"]
    assert [(e.principal, e.purpose, e.filing_role) for e in reads] == [
        ("attorney-0001", "case_upload", "debtor_1"),
        ("attorney-0001", "case_upload", "debtor_2"),
    ]
    _assert_no_ssn(caplog.text)
    _assert_no_ssn(repr(file))
    _assert_no_ssn(json.dumps([vars(e) for e in log.events], default=str))


def test_no_problem_or_exception_carries_a_value():
    rows = valid_rows()
    rows[1][7] = "987-65-43210"  # an SSN one digit too long
    rows[1][2] = "A" * 21
    problems = validate_debtor_txt(joined(rows), SPEC)
    assert problems
    error = CaseUploadError(problems)
    for text in (str(error), repr(error), *(repr(p) for p in problems)):
        _assert_no_ssn(text)
        assert "AAAAAAAAAAAAAAAAAAAAA" not in text


def test_nothing_that_answers_a_client_composes_the_file():
    """The file is streamed to the court by the filing worker and never
    stored: no module of the API's web layer, adapters or entrypoints names
    the builder, and nothing in core but this module does."""
    offenders = []
    for path in sorted(SRC.rglob("*.py")):
        if path.name == "case_upload.py":
            continue
        tree = ast.parse(path.read_text())
        for node in ast.walk(tree):
            names = []
            if isinstance(node, ast.ImportFrom) and node.module:
                names = [node.module] + [a.name for a in node.names]
            elif isinstance(node, ast.Import):
                names = [a.name for a in node.names]
            if any("case_upload" in n for n in names):
                offenders.append(str(path.relative_to(SRC)))
    assert offenders == []
