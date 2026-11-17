"""The generated amendment cover sheet (issue #370) — in isolation from
packet assembly's own integration coverage (test_packet_assembly.py), which
exercises it wired into a real amendedOnly run.
"""

from __future__ import annotations

import io
from dataclasses import replace
from datetime import UTC, datetime

from insolvia_api.core.amendment_cover_sheet import render_amendment_cover_sheet
from insolvia_api.core.form_templates import latest_form
from pypdf import PdfReader

from tests.unit.test_form_projections import reference_case_file

GENERATED_AT = datetime(2026, 9, 3, 12, 30, tzinfo=UTC)


def _page_text(content: bytes) -> str:
    return PdfReader(io.BytesIO(content)).pages[0].extract_text()


def test_the_cover_sheet_is_one_page() -> None:
    content = render_amendment_cover_sheet(
        reference_case_file(), amended_releases=(), generated_at=GENERATED_AT
    )
    assert len(PdfReader(io.BytesIO(content)).pages) == 1


def test_the_cover_sheet_says_it_is_not_an_official_form() -> None:
    content = render_amendment_cover_sheet(
        reference_case_file(), amended_releases=(), generated_at=GENERATED_AT
    )
    text = _page_text(content).lower()
    assert "not an official" in text
    assert "local" in text  # points a preparer at their district's own rules


def test_the_cover_sheet_names_both_debtors() -> None:
    content = render_amendment_cover_sheet(
        reference_case_file(), amended_releases=(), generated_at=GENERATED_AT
    )
    text = _page_text(content)
    assert "Ada Quinn Lovelace" in text
    assert "Ben Lovelace" in text


def test_the_cover_sheet_lists_the_amended_forms_in_order() -> None:
    content = render_amendment_cover_sheet(
        reference_case_file(),
        amended_releases=[latest_form("form/b106d"), latest_form("form/b106ef")],
        generated_at=GENERATED_AT,
    )
    text = _page_text(content)
    d_index = text.index("106D")
    ef_index = text.index("106E/F")
    assert d_index < ef_index


def test_the_cover_sheet_prints_the_case_filed_at_date() -> None:
    case_file = reference_case_file()
    case_file = replace(case_file, case=replace(case_file.case, filed_at="2026-08-15"))
    content = render_amendment_cover_sheet(
        case_file, amended_releases=(), generated_at=GENERATED_AT
    )
    assert "08/15/2026" in _page_text(content)


def test_the_cover_sheet_says_not_yet_recorded_when_the_case_has_no_filed_at() -> None:
    case_file = reference_case_file()
    assert case_file.case.filed_at is None
    content = render_amendment_cover_sheet(
        case_file, amended_releases=(), generated_at=GENERATED_AT
    )
    assert "Not yet recorded" in _page_text(content)


def test_the_cover_sheet_prints_when_it_was_generated() -> None:
    content = render_amendment_cover_sheet(
        reference_case_file(), amended_releases=(), generated_at=GENERATED_AT
    )
    assert "2026-09-03 12:30" in _page_text(content)


def test_a_long_form_title_wraps_inside_the_right_margin() -> None:
    content = render_amendment_cover_sheet(
        reference_case_file(),
        amended_releases=[latest_form("form/b106sum")],
        generated_at=GENERATED_AT,
    )
    page = PdfReader(io.BytesIO(content)).pages[0]
    right_edge = float(page.mediabox.width) - 72
    line_ends: list[float] = []

    def visit(text, _cm, tm, _font, size) -> None:
        if text.strip():
            # No Helvetica glyph is wider than 0.6 em on average text; this
            # bounds where a line can end.
            line_ends.append(tm[4] + len(text.rstrip()) * size * 0.6)

    page.extract_text(visitor_text=visit)
    assert line_ends
    assert max(line_ends) <= right_edge


def test_a_name_outside_the_pdf_font_encoding_does_not_fail_the_render() -> None:
    case_file = reference_case_file()
    renamed = tuple(
        replace(d, name=replace(d.name, surname="Nguyễn"))
        if d.filing_role == "debtor_1"
        else d
        for d in case_file.debtors
    )
    content = render_amendment_cover_sheet(
        replace(case_file, debtors=renamed),
        amended_releases=(),
        generated_at=GENERATED_AT,
    )
    assert "Debtor 1:" in _page_text(content)
