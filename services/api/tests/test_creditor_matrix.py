"""The pure matrix generator (issue #94).

The format rules under test are the courts', not ours — each is pinned to the
published clerk instructions core/creditor_matrix.py cites (S.D./N.D. Fla.,
N.D./S.D. Tex., N.D. Ga.). What matters most is what the generator REFUSES:
a truncated or mis-cased line here is a bankruptcy notice that never arrives.
"""

from __future__ import annotations

import pytest
from insolvia_api.core.case_entities import CaseEntity
from insolvia_api.core.creditor_matrix import (
    COMMON_FORMAT,
    DISTRICT_VARIANCES,
    CreditorMatrix,
    MatrixFormat,
    generate_creditor_matrix,
    matrix_json,
)
from insolvia_api.core.creditors import CREDITOR, CreditorBody
from insolvia_api.core.fields import Address

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


# ── District variance is data ───────────────────────────────────


def test_the_southern_texas_variance_pads_to_six_line_blocks():
    matrix = generate_creditor_matrix(
        [
            creditor("Alpha Card", entity_id="creditor-1"),
            creditor("Beta Finance", entity_id="creditor-2"),
        ],
        fmt=DISTRICT_VARIANCES["txsb"],
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
        fmt=DISTRICT_VARIANCES["txnb"],
    )
    assert matrix.content is not None
    assert "Wilmington DE 19850\r\n\r\n\r\nBeta Finance" in matrix.content


def test_the_variance_table_only_departs_from_the_common_format():
    # Guards the data's meaning: an entry identical to COMMON_FORMAT is a
    # verified-identical district, and those are recorded by ABSENCE.
    for district, fmt in DISTRICT_VARIANCES.items():
        assert fmt != COMMON_FORMAT, district
        assert isinstance(fmt, MatrixFormat)


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
