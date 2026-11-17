"""The pure matrix generator (issue #94).

The format rules under test are the courts', not ours — each is pinned to the
published clerk instructions core/creditor_matrix.py cites (S.D./N.D. Fla.,
N.D./S.D. Tex., N.D. Ga.). What matters most is what the generator REFUSES:
a truncated or mis-cased line here is a bankruptcy notice that never arrives.
"""

from __future__ import annotations

import json
import os
from pathlib import Path

import pytest
from insolvia_api.core.creditor_matrix import (
    COMMON_FORMAT,
    CreditorMatrix,
    MatrixFormat,
    format_for_court,
    generate_creditor_matrix,
    matrix_json,
)
from insolvia_core import courts
from insolvia_core.case_entities import CaseEntity
from insolvia_core.creditors import CREDITOR, CreditorBody
from insolvia_core.fields import Address

MATRIX_GOLDENS = Path(__file__).resolve().parent / "goldens" / "creditor_matrix"

CASE_ID = "00000000-0000-4000-8000-00000000ca5e"


def creditor(
    name: str | None = "Example Bank",
    *,
    entity_id: str = "creditor-1",
    line1: str | None = "PO Box 15168",
    line2: str | None = None,
    city: str | None = "Wilmington",
    state: str | None = "DE",
    postal_code: str | None = "19850",
    raw: str | None = None,
) -> CaseEntity[CreditorBody]:
    return CaseEntity(
        kind=CREDITOR,
        id=entity_id,
        case_id=CASE_ID,
        created_at="2026-01-01T00:00:00.000000Z",
        updated_at="2026-01-01T00:00:00.000000Z",
        body=CreditorBody(
            name=name,
            address=Address(
                line1=line1,
                line2=line2,
                city=city,
                state=state,
                postal_code=postal_code,
                raw=raw,
            ),
        ),
        provenance={},
    )


def problem_fields(matrix: CreditorMatrix) -> set[tuple[str | None, str]]:
    return {(problem.creditor_id, problem.field) for problem in matrix.problems}


# ── Rendering the common format ─────────────────────────────────


def test_a_clean_creditor_prints_name_street_and_city_line_with_crlf():
    matrix = generate_creditor_matrix([creditor()])
    assert matrix.content == "Example Bank\r\nPO Box 15168\r\nWilmington DE 19850\r\n"
    assert matrix.creditor_count == 1
    assert matrix.problems == ()


def test_the_second_address_line_prints_between_street_and_city():
    matrix = generate_creditor_matrix(
        [creditor(line1="4141 Fourth Ave", line2="Suite 900")]
    )
    assert matrix.content is not None
    assert matrix.content.splitlines() == [
        "Example Bank",
        "4141 Fourth Ave",
        "Suite 900",
        "Wilmington DE 19850",
    ]


def test_creditors_are_separated_by_one_blank_line():
    matrix = generate_creditor_matrix(
        [
            creditor("Alpha Card", entity_id="creditor-1"),
            creditor("Beta Finance", entity_id="creditor-2"),
        ]
    )
    assert matrix.content is not None
    assert matrix.content.splitlines() == [
        "Alpha Card",
        "PO Box 15168",
        "Wilmington DE 19850",
        "",
        "Beta Finance",
        "PO Box 15168",
        "Wilmington DE 19850",
    ]


def test_entries_sort_alphabetically_by_name_ignoring_case():
    matrix = generate_creditor_matrix(
        [
            creditor("delta hospital", entity_id="creditor-1"),
            creditor("Alpha Card", entity_id="creditor-2"),
            creditor("Charlie & Sons", entity_id="creditor-3"),
        ]
    )
    assert matrix.content is not None
    names = [
        line
        for line in matrix.content.splitlines()
        if line and not line.startswith(("PO Box", "Wilmington"))
    ]
    assert names == ["Alpha Card", "Charlie & Sons", "delta hospital"]


def test_generation_is_deterministic():
    creditors = [
        creditor("Beta Finance", entity_id="creditor-1"),
        creditor("Alpha Card", entity_id="creditor-2"),
    ]
    assert generate_creditor_matrix(creditors) == generate_creditor_matrix(creditors)


def test_a_nine_digit_zip_prints_hyphenated_as_entered():
    matrix = generate_creditor_matrix([creditor(postal_code="19850-1234")])
    assert matrix.content is not None
    assert "Wilmington DE 19850-1234" in matrix.content


# ── Deduplication: identical blocks print once ──────────────────


def test_blocks_that_print_identically_are_one_entry():
    matrix = generate_creditor_matrix(
        [
            creditor(entity_id="creditor-1"),
            creditor(entity_id="creditor-2"),
        ]
    )
    assert matrix.creditor_count == 1
    assert matrix.duplicates_omitted == 1
    assert matrix.content is not None
    assert matrix.content.count("Example Bank") == 1


def test_case_is_the_one_difference_dedupe_ignores():
    matrix = generate_creditor_matrix(
        [
            creditor("Example Bank", entity_id="creditor-1"),
            creditor("EXAMPLE BANK", entity_id="creditor-2"),
        ]
    )
    assert matrix.creditor_count == 1
    assert matrix.duplicates_omitted == 1


def test_the_same_creditor_at_two_addresses_is_two_noticing_entries():
    matrix = generate_creditor_matrix(
        [
            creditor(entity_id="creditor-1", line1="PO Box 15168"),
            creditor(entity_id="creditor-2", line1="PO Box 99999"),
        ]
    )
    assert matrix.creditor_count == 2
    assert matrix.duplicates_omitted == 0


# ── What the generator refuses ──────────────────────────────────


def test_a_nameless_creditor_is_a_problem_not_an_omission():
    matrix = generate_creditor_matrix([creditor(name=None)])
    assert matrix.content is None
    assert problem_fields(matrix) == {("creditor-1", "name")}


def test_a_raw_only_address_names_the_address_as_unstructured():
    matrix = generate_creditor_matrix(
        [
            creditor(
                line1=None,
                city=None,
                state=None,
                postal_code=None,
                raw="Example Bank PO Box 15168 Wilmington DE",
            )
        ]
    )
    assert matrix.content is None
    assert problem_fields(matrix) == {("creditor-1", "address")}
    assert "unstructured" in matrix.problems[0].message


def test_a_creditor_with_no_address_at_all_is_a_problem():
    matrix = generate_creditor_matrix(
        [creditor(line1=None, city=None, state=None, postal_code=None)]
    )
    assert problem_fields(matrix) == {("creditor-1", "address")}


@pytest.mark.parametrize(
    ("missing", "field"),
    [
        ({"line1": None}, "address.line1"),
        ({"city": None}, "address.city"),
        ({"state": None}, "address.state"),
        ({"postal_code": None}, "address.postal_code"),
    ],
)
def test_each_missing_address_part_is_named(missing, field):
    matrix = generate_creditor_matrix([creditor(**missing)])
    assert matrix.content is None
    assert problem_fields(matrix) == {("creditor-1", field)}


@pytest.mark.parametrize("state", ["Florida", "fl", "XX", "F"])
def test_a_state_must_be_a_two_letter_usps_abbreviation(state):
    matrix = generate_creditor_matrix([creditor(state=state)])
    assert problem_fields(matrix) == {("creditor-1", "address.state")}


@pytest.mark.parametrize("postal_code", ["3330", "333015", "33301 1234", "3330A"])
def test_a_zip_must_be_five_or_hyphenated_nine_digits(postal_code):
    matrix = generate_creditor_matrix([creditor(postal_code=postal_code)])
    assert problem_fields(matrix) == {("creditor-1", "address.postal_code")}


def test_a_forty_character_line_is_the_boundary():
    exactly_forty = "A" * 40
    over = "A" * 41
    assert generate_creditor_matrix([creditor(exactly_forty)]).problems == ()
    matrix = generate_creditor_matrix([creditor(over)])
    assert matrix.content is None
    assert problem_fields(matrix) == {("creditor-1", "name")}
    assert "40 characters" in matrix.problems[0].message


def test_a_long_city_line_names_the_address():
    matrix = generate_creditor_matrix(
        [creditor(city="A" * 35)]  # "A"*35 + " DE 19850" = 44 characters
    )
    assert matrix.content is None
    assert problem_fields(matrix) == {("creditor-1", "address")}


def test_non_ascii_text_is_refused_not_transliterated():
    matrix = generate_creditor_matrix([creditor("Crédit Municipal")])
    assert matrix.content is None
    assert problem_fields(matrix) == {("creditor-1", "name")}
    assert "ASCII" in matrix.problems[0].message


def test_an_empty_creditor_list_is_a_case_level_problem():
    matrix = generate_creditor_matrix([])
    assert matrix.content is None
    assert problem_fields(matrix) == {(None, "creditors")}


def test_one_bad_creditor_withholds_the_whole_file():
    # A partial matrix silently drops a creditor from noticing — worse than
    # no file — so a single problem means no content at all.
    matrix = generate_creditor_matrix(
        [
            creditor("Alpha Card", entity_id="creditor-1"),
            creditor(name=None, entity_id="creditor-2"),
        ]
    )
    assert matrix.content is None
    assert matrix.creditor_count == 0
    assert problem_fields(matrix) == {("creditor-2", "name")}


# ── District variance is data, read from the court registry ─────


def test_the_southern_texas_variance_pads_to_six_line_blocks():
    matrix = generate_creditor_matrix(
        [
            creditor("Alpha Card", entity_id="creditor-1"),
            creditor("Beta Finance", entity_id="creditor-2"),
        ],
        fmt=format_for_court("txsb"),
    )
    assert matrix.content is not None
    lines = matrix.content.splitlines()
    # Two entries, each exactly six lines (three printed plus three blank).
    assert len(lines) == 12
    assert lines[3:6] == ["", "", ""]
    assert lines[6] == "Beta Finance"


def test_the_northern_texas_variance_widens_the_separator():
    matrix = generate_creditor_matrix(
        [
            creditor("Alpha Card", entity_id="creditor-1"),
            creditor("Beta Finance", entity_id="creditor-2"),
        ],
        fmt=format_for_court("txnb"),
    )
    assert matrix.content is not None
    assert "Wilmington DE 19850\r\n\r\n\r\nBeta Finance" in matrix.content


def test_each_courts_format_is_its_registry_records_verified_knobs():
    # What each launch court's pages say, as the registry records it — the
    # departures the goldens below render.
    assert format_for_court("txsb").pad_to_lines == 6
    assert format_for_court("txsb").blank_lines_between == 0
    assert format_for_court("txnb").blank_lines_between == 2
    assert format_for_court("txeb").blank_lines_between == 2
    assert format_for_court("txeb").case_number_header_when_separate is True
    assert format_for_court("txwb").name_line_chars == 50
    assert format_for_court("txwb").comma_after_city is True
    assert format_for_court("flnb").comma_after_city is True
    # M.D. Ga., re-read for the courts/us-bankruptcy@2026-09-26 release.
    assert format_for_court("gamb") == MatrixFormat(
        blank_lines_between=2, comma_after_city=True
    )
    # S.D. Fla. (CI-3) is the common format.
    assert format_for_court("flsb") == COMMON_FORMAT


def test_the_format_follows_the_release_in_force(monkeypatch):
    # No snapshot table: the format is read from the registry at call time,
    # so a new release changes it the day it takes effect. Under the first
    # release M.D. Ga.'s knobs were unverified and the common format applied.
    first = courts.get("courts/us-bankruptcy@2026-09-24")
    monkeypatch.setattr(courts, "current", lambda: first)
    assert format_for_court("gamb") == COMMON_FORMAT


def test_an_unverified_knob_falls_back_to_the_common_format():
    # M.D. Fla.'s only published widths are the pro se paper instructions
    # (28 characters / 4 lines), stored unverified for the represented
    # debtor's upload — so the generator keeps the common format there.
    assert format_for_court("flmb").max_line_chars == 40
    assert format_for_court("flmb").max_creditor_lines == 5


def test_a_case_without_a_court_gets_the_common_format():
    assert format_for_court(None) == COMMON_FORMAT
    assert format_for_court("nyeb") == COMMON_FORMAT


def test_the_western_texas_format_widens_the_name_line_and_adds_the_comma():
    fmt = format_for_court("txwb")
    name = "Consolidated Example Receivables Servicing LLC"  # 46 chars
    assert len(name) > 40
    matrix = generate_creditor_matrix([creditor(name)], fmt=fmt)
    assert matrix.problems == ()
    assert matrix.content == f"{name}\r\nPO Box 15168\r\nWilmington, DE 19850\r\n"
    # An address line is still held to 40.
    over = generate_creditor_matrix([creditor(line1="x" * 41)], fmt=fmt)
    assert problem_fields(over) == {("creditor-1", "address.line1")}


# ── E.D. Tex.: the case number heads a separately-filed matrix ───


def test_a_separately_filed_eastern_texas_matrix_starts_with_the_case_number():
    matrix = generate_creditor_matrix(
        [creditor()], format_for_court("txeb"), case_number="26-40001"
    )
    # LBR App. 1007-b-5 III.A.2: the number, two blank lines, and the first
    # creditor "on the fourth line".
    assert matrix.content is not None
    assert matrix.content.split("\r\n")[:4] == ["26-40001", "", "", "Example Bank"]


def test_without_a_case_number_eastern_texas_prints_no_header():
    # Before the case is opened, and through Case Upload (III.B), there is no
    # separate filing and no number to print.
    matrix = generate_creditor_matrix([creditor()], format_for_court("txeb"))
    assert matrix.content == "Example Bank\r\nPO Box 15168\r\nWilmington DE 19850\r\n"


@pytest.mark.parametrize("court", ["flsb", "gamb", "txnb", "txsb", "txwb"])
def test_a_court_that_wants_no_header_never_prints_the_case_number(court):
    matrix = generate_creditor_matrix(
        [creditor()], format_for_court(court), case_number="26-40001"
    )
    assert matrix.content is not None
    assert "26-40001" not in matrix.content


@pytest.mark.parametrize("case_number", ["", "   ", "26-40001é", "9" * 41])
def test_a_case_number_that_cannot_print_withholds_the_file(case_number):
    matrix = generate_creditor_matrix(
        [creditor()], format_for_court("txeb"), case_number=case_number
    )
    assert matrix.content is None
    assert problem_fields(matrix) == {(None, "case_number")}


# ── Per-court goldens (ADR 0024 PR 2) ───────────────────────────
#
# One creditor list (goldens/creditor_matrix/matrix_case.json) rendered for
# every launch court through the registry, each pinned byte-for-byte — CRLFs,
# blank lines and the trailing newline included, because those bytes are what
# the clerk's system ingests (the directory's .gitattributes keeps git from
# rewriting them). Courts whose verified knobs coincide share a file; the map
# says which, and a court added to the registry fails the guard below until
# someone decides where its matrix belongs.
#
# Regenerate after a deliberate change:
#   UPDATE_MATRIX_GOLDENS=1 pytest tests/unit/test_creditor_matrix.py -k golden

GOLDEN_FOR_COURT = {
    "flsb": "common",  # CI-3 is the common format
    "ganb": "common",
    "gasb": "common",  # every matrix knob unverified
    # The comma after the city. W.D. Tex.'s 50-character name line changes
    # nothing for names within 40 (its own test above pins the wider line),
    # so the two Florida courts that want the comma print the same file.
    "txwb": "txwb",
    "flnb": "txwb",
    "flmb": "txwb",  # its narrower widths are pro se only, unverified
    "txsb": "txsb",
    "txnb": "txnb",
    "txeb": "txnb",  # identical until the case number heads it (below)
    "gamb": "gamb",
}


def _golden_case() -> tuple[list[CaseEntity[CreditorBody]], str]:
    raw = json.loads((MATRIX_GOLDENS / "matrix_case.json").read_text("utf-8"))
    creditors = [
        creditor(
            body["name"],
            entity_id=f"creditor-{index}",
            line1=body["address"].get("line1"),
            line2=body["address"].get("line2"),
            city=body["address"].get("city"),
            state=body["address"].get("state"),
            postal_code=body["address"].get("postal_code"),
        )
        for index, body in enumerate(raw["creditors"], start=1)
    ]
    return creditors, raw["case_number"]


def _check_golden(name: str, matrix: CreditorMatrix) -> None:
    assert matrix.problems == ()
    assert matrix.content is not None
    data = matrix.content.encode("ascii")
    path = MATRIX_GOLDENS / f"{name}.txt"
    if os.environ.get("UPDATE_MATRIX_GOLDENS") == "1":  # pragma: no cover
        path.write_bytes(data)
    assert data == path.read_bytes(), name


def test_every_registry_court_has_a_matrix_golden():
    assert set(GOLDEN_FOR_COURT) == set(courts.district_codes())


def test_the_common_format_renders_to_its_golden():
    creditors, _ = _golden_case()
    _check_golden("common", generate_creditor_matrix(creditors))


@pytest.mark.parametrize("court", sorted(GOLDEN_FOR_COURT))
def test_each_courts_matrix_renders_to_its_golden(court):
    creditors, _ = _golden_case()
    matrix = generate_creditor_matrix(creditors, format_for_court(court))
    assert matrix.duplicates_omitted == 1
    _check_golden(GOLDEN_FOR_COURT[court], matrix)


def test_the_eastern_texas_separate_filing_renders_to_its_golden():
    creditors, case_number = _golden_case()
    matrix = generate_creditor_matrix(
        creditors, format_for_court("txeb"), case_number=case_number
    )
    _check_golden("txeb_filed_separately", matrix)


def test_the_departing_courts_matrices_all_differ_byte_for_byte():
    # ADR 0024 PR 2's done-when: one case, six different files.
    names = ["common", "txsb", "txnb", "txwb", "txeb_filed_separately", "gamb"]
    files = {name: (MATRIX_GOLDENS / f"{name}.txt").read_bytes() for name in names}
    assert len(set(files.values())) == len(names)


# ── The API representation ──────────────────────────────────────


def test_matrix_json_carries_the_file_and_omits_absent_content():
    generated = matrix_json(generate_creditor_matrix([creditor()]))
    assert generated == {
        "fileName": "creditor-matrix.txt",
        "creditorCount": 1,
        "duplicatesOmitted": 0,
        "problems": [],
        "content": "Example Bank\r\nPO Box 15168\r\nWilmington DE 19850\r\n",
    }

    refused = matrix_json(generate_creditor_matrix([creditor(name=None)]))
    assert "content" not in refused
    assert refused["problems"] == [
        {
            "creditorId": "creditor-1",
            "field": "name",
            "message": "A creditor needs a name to appear on the matrix.",
        }
    ]


def test_matrix_json_omits_creditor_id_on_the_case_level_problem():
    refused = matrix_json(generate_creditor_matrix([]))
    problems = refused["problems"]
    assert isinstance(problems, list)
    assert "creditorId" not in problems[0]
    assert problems[0]["field"] == "creditors"
