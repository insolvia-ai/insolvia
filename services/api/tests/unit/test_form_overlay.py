"""Output options (issue 13.11): the watermark/stamp overlay, signature-page
selection, `/s/` electronic signatures, and options parsing — in isolation
from packet assembly and the forms hub, which get their own end-to-end
coverage (test_packet_assembly.py, test_forms_hub.py) over the same case
data both features read.
"""

from __future__ import annotations

import io
from dataclasses import replace
from datetime import UTC, date, datetime

import pytest
from insolvia_api.core.form_fill import Text
from insolvia_api.core.form_overlay import (
    DEFAULT_OUTPUT_OPTIONS,
    OutputOptions,
    apply_output_stamps,
    apply_signature_options,
    output_options_json,
    parse_output_options,
    parse_output_options_query,
    select_pages,
    stamp_pages,
)
from insolvia_api.core.form_templates import latest_form
from insolvia_core.errors import FieldValidationError
from pypdf import PdfReader

from tests.unit.test_form_projections import reference_case_file

B101 = latest_form("form/b101")
B106DEC = latest_form("form/b106dec")
B106AB = latest_form("form/b106ab")
B2010 = latest_form("form/b2010")  # flat: no AcroForm, no fields at all
B2030 = latest_form("form/b2030")  # flat: no AcroForm, fields claim overlay boxes


def _page_count(pdf_bytes: bytes) -> int:
    return len(PdfReader(io.BytesIO(pdf_bytes)).pages)


# ── OutputOptions defaults ───────────────────────────────────────


def test_default_options_are_the_plain_filing_set() -> None:
    assert OutputOptions() == DEFAULT_OUTPUT_OPTIONS
    assert DEFAULT_OUTPUT_OPTIONS.is_default


def test_a_non_default_option_is_not_the_default() -> None:
    assert not OutputOptions(draft_watermark=True).is_default


# ── Parsing: the packet-assembly job body ────────────────────────


def test_parse_output_options_fills_in_every_default() -> None:
    assert parse_output_options({}, allow_forms=True) == OutputOptions()
    assert parse_output_options(None, allow_forms=True) == OutputOptions()


def test_parse_output_options_reads_every_field() -> None:
    parsed = parse_output_options(
        {
            "draftWatermark": True,
            "printDate": True,
            "signaturePages": "only",
            "signElectronically": True,
            "forms": ["b101", "b106ab"],
        },
        allow_forms=True,
    )
    assert parsed == OutputOptions(
        draft_watermark=True,
        print_date=True,
        signature_pages="only",
        sign_electronically=True,
        forms=("b101", "b106ab"),
    )


@pytest.mark.parametrize(
    "raw",
    [
        "not-an-object",
        {"draftWatermark": "yes"},
        {"printDate": 1},
        {"signElectronically": "true"},
        {"signaturePages": "some"},
        {"unknownOption": True},
        {"forms": []},
        {"forms": ["B101"]},  # not lower-case
        {"forms": ["b101", "b101"]},  # duplicate
        {"forms": "b101"},  # not a list
    ],
)
def test_a_malformed_options_body_is_refused(raw: object) -> None:
    with pytest.raises(FieldValidationError):
        parse_output_options(raw, allow_forms=True)


def test_forms_is_refused_when_the_caller_does_not_allow_it() -> None:
    # The preview route's rule: the form is already named in the URL.
    with pytest.raises(FieldValidationError):
        parse_output_options({"forms": ["b101"]}, allow_forms=False)


def test_output_options_json_round_trips_through_parse() -> None:
    options = OutputOptions(
        draft_watermark=True,
        signature_pages="omit",
        forms=("b101",),
    )
    assert parse_output_options(output_options_json(options), allow_forms=True) == (
        options
    )


def test_output_options_json_always_carries_every_key() -> None:
    body = output_options_json(OutputOptions())
    assert body == {
        "draftWatermark": False,
        "printDate": False,
        "signaturePages": "all",
        "signElectronically": False,
    }
    assert "forms" not in body  # None means "every form", not an empty list


# ── Parsing: the preview route's query string ────────────────────


def test_parse_output_options_query_reads_booleans_and_mode() -> None:
    parsed = parse_output_options_query(
        {"draftWatermark": "true", "printDate": "1", "signaturePages": "omit"}
    )
    assert parsed == OutputOptions(
        draft_watermark=True, print_date=True, signature_pages="omit"
    )


@pytest.mark.parametrize("falsy", ["false", "0", ""])
def test_parse_output_options_query_reads_false_spellings(falsy: str) -> None:
    assert parse_output_options_query({"draftWatermark": falsy}) == OutputOptions()


def test_parse_output_options_query_absent_keys_keep_defaults() -> None:
    assert parse_output_options_query({}) == OutputOptions()


def test_parse_output_options_query_refuses_forms() -> None:
    # The preview route names one form in the URL; forwarding "forms" here
    # (rather than silently dropping it) is what lets this raise instead of
    # a client's copy-pasted packet-assembly query string being ignored.
    with pytest.raises(FieldValidationError):
        parse_output_options_query({"forms": "b101,b106ab"})


# ── Signature-page selection ──────────────────────────────────────


def test_select_pages_all_is_the_identity() -> None:
    assert select_pages(B101.template_pdf, B101, mode="all") is B101.template_pdf


def test_select_pages_only_keeps_exactly_the_signature_pages() -> None:
    only = select_pages(B101.template_pdf, B101, mode="only")
    assert only is not None
    assert _page_count(only) == len(B101.signature_pages) == 3


def test_select_pages_omit_drops_exactly_the_signature_pages() -> None:
    omit = select_pages(B101.template_pdf, B101, mode="omit")
    assert omit is not None
    total = _page_count(B101.template_pdf)
    assert _page_count(omit) == total - len(B101.signature_pages)


def test_select_pages_only_is_none_for_a_form_with_no_signature_line() -> None:
    assert B106AB.signature_pages == ()
    assert select_pages(B106AB.template_pdf, B106AB, mode="only") is None


def test_select_pages_omit_is_none_for_a_form_that_is_entirely_signature() -> None:
    # B106Dec's one page IS the signature block.
    assert B106DEC.signature_pages == (1,)
    assert select_pages(B106DEC.template_pdf, B106DEC, mode="omit") is None


# ── Stamps: watermark and top-margin date ────────────────────────


def test_stamp_pages_is_a_no_op_with_both_stamps_off() -> None:
    result = stamp_pages(
        B101.template_pdf,
        draft_watermark=False,
        print_date=False,
        printed_at=datetime.now(UTC),
    )
    assert result is B101.template_pdf


def test_stamp_pages_draws_the_watermark_and_the_date() -> None:
    stamped = stamp_pages(
        B101.template_pdf,
        draft_watermark=True,
        print_date=True,
        printed_at=datetime(2026, 9, 23, 14, 30, tzinfo=UTC),
    )
    text = PdfReader(io.BytesIO(stamped)).pages[0].extract_text()
    assert "DRAFT" in text
    assert "Printed 2026-09-23 14:30 UTC" in text
    # Every page carries both marks, not just the first.
    last_page_text = PdfReader(io.BytesIO(stamped)).pages[-1].extract_text()
    assert "DRAFT" in last_page_text


def test_stamp_pages_leaves_the_field_values_untouched() -> None:
    # The overlay draws OVER the page; it must never alter what get_fields()
    # reports back — that would mean it perturbed the deterministic fill.
    before = PdfReader(io.BytesIO(B101.template_pdf)).get_fields()
    stamped = stamp_pages(
        B101.template_pdf,
        draft_watermark=True,
        print_date=True,
        printed_at=datetime.now(UTC),
    )
    after = PdfReader(io.BytesIO(stamped)).get_fields()
    assert before is not None
    assert after is not None
    assert {k: v.value for k, v in before.items()} == {
        k: v.value for k, v in after.items()
    }


# ── apply_output_stamps: page selection, then stamps ─────────────


def test_apply_output_stamps_is_a_no_op_for_the_default_options() -> None:
    result = apply_output_stamps(
        B101.template_pdf,
        B101,
        options=DEFAULT_OUTPUT_OPTIONS,
        printed_at=datetime.now(UTC),
    )
    assert result == B101.template_pdf


def test_apply_output_stamps_returns_none_when_nothing_survives_selection() -> None:
    result = apply_output_stamps(
        B106AB.template_pdf,
        B106AB,
        options=OutputOptions(signature_pages="only"),
        printed_at=datetime.now(UTC),
    )
    assert result is None


# ── /s/ electronic signatures ─────────────────────────────────────


def test_apply_signature_options_is_a_no_op_when_off() -> None:
    values = {"sign.debtor1_signature": Text("placeholder")}
    result = apply_signature_options(
        B101,
        values,
        case_file=reference_case_file(),
        options=OutputOptions(),
        today=date(2026, 9, 3),
    )
    assert result is values


def test_apply_signature_options_signs_both_debtors() -> None:
    result = apply_signature_options(
        B101,
        {},
        case_file=reference_case_file(),
        options=OutputOptions(sign_electronically=True),
        today=date(2026, 9, 3),
    )
    assert result["sign.debtor1_signature"] == Text("/s/ Ada Quinn Lovelace")
    assert result["sign.debtor2_signature"] == Text("/s/ Ben Lovelace Jr.")
    # Without print_date, the paired date fields stay untouched.
    assert "sign.debtor1_executed_on" not in result
    # The attorney's own line is never filled by this feature.
    assert "attorney.signature" not in result


def test_apply_signature_options_prints_the_date_when_asked() -> None:
    result = apply_signature_options(
        B101,
        {},
        case_file=reference_case_file(),
        options=OutputOptions(sign_electronically=True, print_date=True),
        today=date(2026, 9, 3),
    )
    assert result["sign.debtor1_executed_on"] == Text("09/03/2026")
    assert result["sign.debtor2_executed_on"] == Text("09/03/2026")


def test_apply_signature_options_leaves_an_unset_debtor_wet() -> None:
    # B106Dec's reference case still has both debtors, so exercise the
    # single-debtor case through a case file with only Debtor 1.
    solo = replace(reference_case_file(), debtors=reference_case_file().debtors[:1])
    result = apply_signature_options(
        B106DEC,
        {},
        case_file=solo,
        options=OutputOptions(sign_electronically=True),
        today=date(2026, 9, 3),
    )
    assert "debtor1_signature" in result
    assert "debtor2_signature" not in result


# ── Flat releases (B2010, B2030): no AcroForm, so no widget for the ─────────
# output-options machinery to reach for — signature_pages is empty and
# neither /s/ injection nor page selection may crash reaching for one.


def test_flat_releases_have_no_signature_pages() -> None:
    # B2010 has no fields at all; B2030's one "signature" field claims an
    # overlay box, not a widget, and the loader only counts real AcroForm
    # widget pages (form_templates.py), so both are empty.
    assert B2010.is_flat
    assert B2010.signature_pages == ()
    assert B2030.is_flat
    assert B2030.signature_pages == ()


def test_select_pages_only_is_none_for_a_flat_release_with_no_fields() -> None:
    assert select_pages(B2010.template_pdf, B2010, mode="only") is None


def test_select_pages_omit_keeps_every_page_of_a_flat_release_with_no_fields() -> None:
    omit = select_pages(B2010.template_pdf, B2010, mode="omit")
    assert omit is not None
    assert _page_count(omit) == _page_count(B2010.template_pdf)


def test_select_pages_only_is_none_for_a_flat_release_with_overlay_boxes() -> None:
    assert select_pages(B2030.template_pdf, B2030, mode="only") is None


def test_select_pages_omit_keeps_every_page_of_a_flat_release_with_overlay_boxes() -> (
    None
):
    omit = select_pages(B2030.template_pdf, B2030, mode="omit")
    assert omit is not None
    assert _page_count(omit) == _page_count(B2030.template_pdf)


def test_apply_signature_options_is_a_no_op_for_a_flat_release_with_no_fields() -> None:
    result = apply_signature_options(
        B2010,
        {},
        case_file=reference_case_file(),
        options=OutputOptions(sign_electronically=True),
        today=date(2026, 9, 3),
    )
    assert result == {}


def test_apply_signature_options_does_not_sign_an_overlay_signature_line() -> None:
    # B2030's one "signature" field is the ATTORNEY's line (an overlay box,
    # not a widget) — apply_signature_options only ever signs a DEBTOR line,
    # so it must be left wet, and reaching for it must not crash trying to
    # resolve an overlay box name as a PDF widget.
    result = apply_signature_options(
        B2030,
        {},
        case_file=reference_case_file(),
        options=OutputOptions(sign_electronically=True),
        today=date(2026, 9, 3),
    )
    assert result == {}
