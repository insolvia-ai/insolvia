"""The filing set and checklist (ADR 0024 build PR 3, core/filing_set.py),
and the packet-file measurements it judges (core/pdf_measure.py).

"Unit tests over every record": the parametrized tests below build the
filing set of the reference case for EVERY district (and every division) in
the current court registry release, against a real packet the assembly
worker measured — so a registry record the filing set cannot handle fails a
pull request, not a filing. Every identifier is fake; this repo is public.
"""

from __future__ import annotations

import io
import json
from dataclasses import replace

import pytest
from insolvia_api.api.routes.filing_set import filing_set_json
from insolvia_api.core.creditor_matrix import MATRIX_FILE_NAME
from insolvia_api.core.filing_set import (
    B121_SERIES,
    build_filing_set,
    default_max_bytes,
    latest_filing_set_packet,
)
from insolvia_api.core.form_overlay import OutputOptions
from insolvia_api.core.packet_assembly import (
    packet_form_series,
    run_packet_assembly,
)
from insolvia_api.core.packets import Packet, PacketPart, is_filing_set
from insolvia_api.core.pdf_measure import measure_part
from insolvia_core import courts
from pypdf import PdfWriter

from tests.unit.test_packet_assembly import (
    CASE_ID,
    TODAY,
    accept_job,
    build_deps,
    reference_case_data,
    reference_chapter_13_case_data,
)

RELEASE = courts.latest()
DISTRICTS = [d.code for d in RELEASE.districts]
DIVISIONS = [(d.code, v.code) for d in RELEASE.districts for v in d.divisions]


@pytest.fixture(scope="module")
def packet() -> Packet:
    """One real assembly of the reference case, measured by the worker."""
    data = reference_case_data()
    deps = build_deps(data)
    result = run_packet_assembly(accept_job(), deps, today=TODAY)
    assert result["outcome"] == "assembled"
    stored = deps.packet_store.get(CASE_ID, result["packet"]["id"])
    assert stored is not None
    return stored


def in_court(data, code: str, division: str | None = None):
    district = RELEASE.district(code)
    assert district is not None
    division = division or district.divisions[0].code
    case = replace(data.case, court=code, division=division, district=district.name)
    return replace(data, case=case)


def build(data, packets=(), release=RELEASE):
    return build_filing_set(data, packets=packets, release=release, as_of=TODAY)


def items(filing_set):
    return {item.id: item for item in filing_set.checklist}


# ── The measurements ────────────────────────────────────────────────────────


def test_the_worker_measures_every_part_of_the_packet(packet):
    names = [part.name for part in packet.parts]
    assert names[-1] == MATRIX_FILE_NAME
    for part in packet.parts:
        assert part.byte_size > 0
        if part.name == MATRIX_FILE_NAME:
            assert part.page_count is None
            continue
        # The official forms are letter-size, text-bearing PDFs.
        assert part.page_count, part.name
        assert part.non_letter_pages == 0, part.name
        assert part.pages_without_text == 0, part.name


def test_a_measured_part_never_carries_content(packet):
    # B121 is a part: what is stored about it is counts, never its text.
    data = reference_case_data()
    stored = json.dumps([vars(p) for p in packet.parts])
    for digits in data.tax_ids.values():
        assert digits not in stored
        assert f"{digits[:3]}-{digits[3:5]}-{digits[5:]}" not in stored


def _blank_pdf(width: float, height: float) -> bytes:
    writer = PdfWriter()
    writer.add_blank_page(width=width, height=height)
    buffer = io.BytesIO()
    writer.write(buffer)
    return buffer.getvalue()


def test_an_a4_page_without_text_is_measured_as_both():
    part = measure_part("scan.pdf", _blank_pdf(595, 842))
    assert part.page_count == 1
    assert part.non_letter_pages == 1
    assert part.pages_without_text == 1


def test_a_landscape_letter_page_is_letter():
    assert measure_part("wide.pdf", _blank_pdf(792, 612)).non_letter_pages == 0


def test_a_text_file_is_a_size_only():
    part = measure_part(MATRIX_FILE_NAME, b"ACME BANK\nPO BOX 1\n")
    assert part == PacketPart(name=MATRIX_FILE_NAME, byte_size=19)


def test_bytes_that_are_not_a_pdf_are_unreadable():
    part = measure_part("broken.pdf", b"not a pdf at all")
    assert part.unreadable


# ── Every district, every division ───────────────────────────────────────────


@pytest.mark.parametrize(("code", "division"), DIVISIONS)
def test_every_division_of_every_district_builds_a_filing_set(code, division, packet):
    data = in_court(reference_case_data(), code, division)
    filing_set = build(data, (packet,))
    district = RELEASE.district(code)
    assert district is not None
    assert filing_set.court_code == code
    assert filing_set.division_name == district.division(division).name
    assert items(filing_set)["court"].status == "ready"


@pytest.mark.parametrize("code", DISTRICTS)
def test_the_documents_follow_the_packet_order_and_names(code, packet):
    data = in_court(reference_case_data(), code)
    filing_set = build(data, (packet,))
    district = RELEASE.district(code)
    assert district is not None
    # No launch court records a docket order or file names yet, so every
    # one gets the documented default: the packet's own order and names.
    assert district.opening.docket_order.value is None
    assert filing_set.order_basis == "default"
    assert filing_set.names_basis == "default"
    from_packet = [d for d in filing_set.documents if d.source == "packet"]
    assert [d.key for d in from_packet] == [
        *packet_form_series(data),
        "creditor_matrix",
    ]
    assert [d.file_name for d in from_packet] == [p.name for p in packet.parts]


@pytest.mark.parametrize("code", DISTRICTS)
def test_every_packet_file_passes_its_checks_or_says_why(code, packet):
    filing_set = build(in_court(reference_case_data(), code), (packet,))
    for document in filing_set.documents:
        if document.source == "outside":
            assert [c.outcome for c in document.checks] == ["unmeasured"]
            continue
        assert document.checks, document.key
        for check in document.checks:
            # The reference packet is small, letter-size and text-bearing.
            assert check.outcome == "pass", (code, document.key, check)


@pytest.mark.parametrize("code", DISTRICTS)
def test_b121_is_restricted_or_not_filed_never_public(code, packet):
    district = RELEASE.district(code)
    assert district is not None
    filing_set = build(in_court(reference_case_data(), code), (packet,))
    (b121,) = [d for d in filing_set.documents if d.key == B121_SERIES]
    expected = (
        "not_filed"
        if district.opening.ssn_statement.value == "not_filed"
        else "restricted"
    )
    assert b121.handling == expected
    assert "Social Security" in b121.note


@pytest.mark.parametrize("code", DISTRICTS)
def test_the_wire_shape_never_carries_a_tax_id(code, packet):
    data = reference_case_data()
    body = json.dumps(filing_set_json(build(in_court(data, code), (packet,))))
    for digits in data.tax_ids.values():
        assert digits not in body
        assert digits[-4:] not in body.replace("2026", "").replace("2099", "")


@pytest.mark.parametrize("code", DISTRICTS)
def test_the_checklist_covers_every_step_of_the_hand_off(code, packet):
    district = RELEASE.district(code)
    assert district is not None
    filing_set = build(in_court(reference_case_data(), code), (packet,))
    by_id = items(filing_set)
    for required in (
        "court",
        "case_data",
        "packet",
        "pdf_checks",
        "ssn_statement",
        "signature",
        "fee",
        "registration",
        "filing_method",
    ):
        assert required in by_id, (code, required)
    assert by_id["case_data"].status == "ready"
    assert by_id["packet"].status == "ready"
    assert by_id["pdf_checks"].status == "ready"
    # Nobody files automatically yet (ADR 0024 PR 10): every court hands off.
    assert filing_set.filing_method == "hand_off"
    assert by_id["filing_method"].status == "action"
    # Every local form the registry lists is an item, in the court's words.
    local = [i for i in filing_set.checklist if i.id.startswith("local_form:")]
    assert len(local) == len(district.opening.local_forms)
    # An unverified fact is never presented as settled.
    if not district.opening.ssn_statement.verified:
        assert by_id["ssn_statement"].status == "confirm"
    if not district.opening.signature_instrument.verified:
        assert by_id["signature"].status == "confirm"
    if not district.opening.fee_rule.verified:
        assert by_id["fee"].status == "confirm"
    assert by_id["docket_order"].status == "confirm"
    for item in filing_set.checklist:
        assert item.title, (code, item.id)
        assert item.detail, (code, item.id)


@pytest.mark.parametrize("code", DISTRICTS)
def test_the_size_limit_is_the_courts_or_the_strictest_on_record(code, packet):
    district = RELEASE.district(code)
    assert district is not None
    filing_set = build(in_court(reference_case_data(), code), (packet,))
    cap = district.pdf.max_bytes
    if cap.value is None:
        assert filing_set.max_bytes == default_max_bytes(RELEASE)
        assert filing_set.max_bytes_basis == "default"
    else:
        assert filing_set.max_bytes == cap.value
        assert filing_set.max_bytes_basis == (
            "court" if cap.verified else "court_unverified"
        )
    assert ("size_limit" in items(filing_set)) == (
        filing_set.max_bytes_basis != "court"
    )


@pytest.mark.parametrize("code", DISTRICTS)
def test_a_court_instrument_filed_as_its_own_event_is_a_document(code, packet):
    district = RELEASE.district(code)
    assert district is not None
    filing_set = build(in_court(reference_case_data(), code), (packet,))
    keys = [d.key for d in filing_set.documents]
    instrument = district.opening.signature_instrument.value
    own_event = instrument is not None and bool(instrument.own_docket_event)
    assert ("signature_instrument" in keys) == own_event
    if own_event:
        # After everything Insolvia produced: the packet's order, then it.
        assert keys.index("signature_instrument") > keys.index("creditor_matrix")


def test_the_default_size_cap_is_the_strictest_recorded():
    caps = [d.pdf.max_bytes.value for d in RELEASE.districts if d.pdf.max_bytes.value]
    assert default_max_bytes(RELEASE) == min(caps)


def test_a_chapter_13_case_lists_its_plan(packet):
    data = in_court(reference_chapter_13_case_data(), "flmb")
    keys = [d.key for d in build(data).documents]
    assert "form/b113" in keys
    assert "form/b108" not in keys


# ── What is missing, and why ─────────────────────────────────────────────────


def test_without_a_packet_every_file_is_unmeasured_and_the_packet_is_missing():
    filing_set = build(in_court(reference_case_data(), "txwb"))
    assert filing_set.packet is None
    for document in filing_set.documents:
        assert [c.outcome for c in document.checks] == ["unmeasured"]
    by_id = items(filing_set)
    assert by_id["packet"].status == "missing"
    assert by_id["packet"].link == "packet"
    assert by_id["pdf_checks"].status == "missing"


def test_a_draft_or_partial_packet_is_not_the_filing_set(packet):
    draft = replace(packet, id="draft", options=OutputOptions(draft_watermark=True))
    partial = replace(packet, id="partial", options=OutputOptions(forms=("b101",)))
    assert not is_filing_set(draft.options)
    assert latest_filing_set_packet((draft, partial)) is None
    assert latest_filing_set_packet((draft, packet, partial)) is packet
    assert build(in_court(reference_case_data(), "flsb"), (draft,)).packet is None


def test_a_packet_assembled_before_measurement_is_unmeasured(packet):
    old = replace(packet, parts=())
    filing_set = build(in_court(reference_case_data(), "flsb"), (old,))
    assert items(filing_set)["pdf_checks"].status == "missing"
    assert all(
        c.outcome == "unmeasured" for d in filing_set.documents for c in d.checks
    )


def test_a_file_over_the_cap_must_be_split(packet):
    first = packet.parts[0]
    big = replace(first, byte_size=36700160 * 2 + 1)  # flnb's cap is 35 MB
    oversized = replace(packet, parts=(big, *packet.parts[1:]))
    filing_set = build(in_court(reference_case_data(), "flnb"), (oversized,))
    (size,) = [c for c in filing_set.documents[0].checks if c.check == "size"]
    assert size.outcome == "fail"
    assert "split it into 3 files" in size.message
    assert items(filing_set)["pdf_checks"].status == "missing"


@pytest.mark.parametrize(("code", "outcome"), [("flsb", "fail"), ("txwb", "warn")])
def test_a_page_without_text_fails_where_the_court_requires_text(code, outcome, packet):
    # FLSB requires text-searchable PDFs (verified); TXWB's rule is unknown.
    first = replace(packet.parts[0], pages_without_text=1)
    scanned = replace(packet, parts=(first, *packet.parts[1:]))
    filing_set = build(in_court(reference_case_data(), code), (scanned,))
    (text,) = [c for c in filing_set.documents[0].checks if c.check == "text_layer"]
    assert text.outcome == outcome
    expected = "missing" if outcome == "fail" else "confirm"
    assert items(filing_set)["pdf_checks"].status == expected


def test_a_page_that_is_not_letter_fails(packet):
    first = replace(packet.parts[0], non_letter_pages=2)
    odd = replace(packet, parts=(first, *packet.parts[1:]))
    filing_set = build(in_court(reference_case_data(), "gasb"), (odd,))
    (page,) = [c for c in filing_set.documents[0].checks if c.check == "page_size"]
    assert page.outcome == "fail"


def test_an_incomplete_case_says_what_is_missing_and_where_to_fix_it():
    data = in_court(reference_case_data(), "txsb")
    incomplete = replace(data, petitions=(), debtors=())
    by_id = items(build(incomplete))
    assert "case_data" not in by_id
    assert by_id["case_data:petitions"].status == "missing"
    assert by_id["case_data:petitions"].link == "petition"
    assert by_id["case_data:debtors"].link == "intake"


def test_a_case_with_no_court_falls_back_to_the_common_defaults():
    data = reference_case_data()
    data = replace(data, case=replace(data.case, court=None, division=None))
    filing_set = build(data)
    assert filing_set.court_code is None
    assert filing_set.max_bytes == default_max_bytes(RELEASE)
    by_id = items(filing_set)
    assert by_id["court"].status == "missing"
    assert by_id["court"].link == ""
    # Without a court there are no court rules to list.
    assert "ssn_statement" not in by_id
    (b121,) = [d for d in filing_set.documents if d.key == B121_SERIES]
    assert b121.handling == "restricted"


def test_a_filed_case_still_gets_its_checklist(packet):
    data = in_court(reference_case_data(), "gamb")
    filed = replace(data, case=replace(data.case, status="filed"))
    by_id = items(build(filed, (packet,)))
    assert by_id["filed"].status == "ready"
    assert "filing_method" in by_id


# ── A court that records its own order and names ─────────────────────────────


def test_a_recorded_docket_order_and_file_names_are_followed(packet):
    district = RELEASE.district("txwb")
    assert district is not None
    order = ("creditor_matrix", "form/b101", "signature_instrument")
    names = {"form/b101": "Petition.pdf", "creditor_matrix": "Creditor.txt"}
    opening = replace(
        district.opening,
        docket_order=courts.Fact(order, "verified", "S8", TODAY, ""),
        file_names=courts.Fact(names, "verified", "S8", TODAY, ""),
    )
    custom = replace(district, opening=opening)
    release = replace(
        RELEASE,
        districts=tuple(custom if d.code == "txwb" else d for d in RELEASE.districts),
    )
    filing_set = build(in_court(reference_case_data(), "txwb"), (packet,), release)
    keys = [d.key for d in filing_set.documents]
    assert keys[:3] == list(order)
    by_key = {d.key: d for d in filing_set.documents}
    assert by_key["form/b101"].file_name == "Petition.pdf"
    assert by_key["creditor_matrix"].file_name == "Creditor.txt"
    # Renamed, but still judged on the packet file it came from.
    assert [c.outcome for c in by_key["form/b101"].checks] == ["pass"] * 3
    assert filing_set.order_basis == "court"
    assert "docket_order" not in items(filing_set)
