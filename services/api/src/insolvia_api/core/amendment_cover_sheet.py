"""The amendment cover sheet (issue #370) — a GENERATED page, never an
official court form.

No national amendment cover sheet exists: Federal Rule of Bankruptcy
Procedure 1009 governs amending a schedule but prescribes no cover page of
its own, and Official Form B106Sum/B106Dec are the only forms the Administrative
Office publishes that touch an amendment at all (their own "amended filing"
caption, `form_overlay.apply_amendment_options`). Several individual
districts publish their OWN local cover sheet for an amendment — some
requiring it, most not — but those are local forms: no two districts'
layouts agree, this repository has no machine-readable spec for any of them
(`forms/README.md`'s ground truth is the AO's own AcroForm dumps, not a
district's), and guessing at one district's form and shipping it as though
it were universal would be worse than not printing one at all. So this
prints a PLAIN, clearly-labelled informational page instead — what forms
this packet amends, and the case's own filing date — built from the same
packet data the rest of assembly reads, and a preparer whose district
requires its own local cover sheet still needs to attach that form
themselves.

Drawn with `core/pdf_draw.py`'s raw content-stream primitives onto a blank
Letter page — the same reasoning `form_overlay.py`'s stamps give for not
adding reportlab: this service already depends on pypdf, and a caption, a
short disclaimer and a bulleted list of form names do not need a second PDF
library's weight.
"""

from __future__ import annotations

import io
import textwrap
from collections.abc import Sequence
from datetime import datetime

from pypdf import PdfWriter

from . import pdf_draw
from .form_projections.shared import CaseFile, format_date, full_name
from .form_templates import FormRelease

# US Letter, in points — every release in this set templates onto Letter, so
# the cover sheet matches the pages it precedes.
_PAGE_WIDTH = 612.0
_PAGE_HEIGHT = 792.0

_MARGIN = 72.0
_TITLE_SIZE = 16.0
_BODY_SIZE = 11.0
_NOTE_SIZE = 9.0
_LINE_GAP = 16.0
_INK = 0.0
_MUTED = 0.35
# Helvetica's average advance is a little over half its size; wrapping at
# that estimate keeps every line inside the right margin without measuring
# glyphs (a standard-14 font ships no metrics this module could read cheaply,
# and a slightly early wrap costs nothing on an informational page).
_AVERAGE_ADVANCE = 0.55


def _debtor_caption(case_file: CaseFile) -> list[str]:
    lines: list[str] = []
    debtor_1 = case_file.debtor("debtor_1")
    if debtor_1 is not None:
        name = full_name(debtor_1.name)
        if name:
            lines.append(f"Debtor 1: {name}")
    debtor_2 = case_file.debtor("debtor_2")
    if debtor_2 is not None:
        name = full_name(debtor_2.name)
        if name:
            lines.append(f"Debtor 2: {name}")
    return lines


def _wrapped(text: str, *, size: float, indent: float = 0.0) -> list[str]:
    usable = _PAGE_WIDTH - 2 * _MARGIN - indent
    return textwrap.wrap(text, width=max(20, int(usable / (size * _AVERAGE_ADVANCE))))


def _release_label(release: FormRelease) -> str:
    if release.official_number:
        return f"{release.official_number} - {release.title}"
    return release.title or release.form


def render_amendment_cover_sheet(
    case_file: CaseFile,
    *,
    amended_releases: Sequence[FormRelease],
    generated_at: datetime,
) -> bytes:
    """One generated page: the case caption, the filing date the PACKET's own
    data carries (`case.filed_at` — never re-typed), the forms this packet
    amends (in filing order, exactly as they appear in the zip after this
    page), and a plain-language note that this is not an official form.

    `amended_releases` is every release `packet_assembly.assemble` renders
    after this page — each schedule carrying an amended item, plus
    B106Sum/B106Dec — the SAME releases, so the list can never name a form
    the packet does not actually contain. Every line wraps inside the
    margins; the page stays one page because an amendment names at most the
    dozen schedules this set has.
    """
    writer = PdfWriter()
    page = writer.add_blank_page(width=_PAGE_WIDTH, height=_PAGE_HEIGHT)
    font_ref = pdf_draw.add_helvetica(writer)

    ops = b""
    y = _PAGE_HEIGHT - _MARGIN

    def line(text: str, *, size: float, gray: float, indent: float = 0.0) -> None:
        nonlocal ops, y
        for part in _wrapped(text, size=size, indent=indent):
            ops += pdf_draw.text_ops(
                part, x=_MARGIN + indent, y=y, size=size, rotate_degrees=0.0, gray=gray
            )
            y -= max(_LINE_GAP, size * 1.3)

    def gap(lines: float = 0.5) -> None:
        nonlocal y
        y -= _LINE_GAP * lines

    line("AMENDMENT COVER SHEET", size=_TITLE_SIZE, gray=_INK)
    gap()
    line(
        "This is a generated informational page, not an official bankruptcy "
        "form. Some districts require their own local amendment cover "
        "sheet; check this case's district's local rules separately.",
        size=_NOTE_SIZE,
        gray=_MUTED,
    )
    gap()

    for caption in _debtor_caption(case_file):
        line(caption, size=_BODY_SIZE, gray=_INK)
    if case_file.case.district:
        line(f"District: {case_file.case.district}", size=_BODY_SIZE, gray=_INK)
    filing_date = (
        format_date(case_file.case.filed_at)
        if case_file.case.filed_at
        else "Not yet recorded"
    )
    line(f"Filing date: {filing_date}", size=_BODY_SIZE, gray=_INK)
    gap()

    line("This packet amends the following:", size=_BODY_SIZE, gray=_INK)
    for release in amended_releases:
        line(f"- {_release_label(release)}", size=_BODY_SIZE, gray=_INK, indent=12)

    ops += pdf_draw.text_ops(
        f"Generated {generated_at.strftime('%Y-%m-%d %H:%M')} UTC",
        x=_MARGIN,
        y=_MARGIN,
        size=_NOTE_SIZE,
        rotate_degrees=0.0,
        gray=_MUTED,
    )

    pdf_draw.draw_on_page(writer, page, font_ref, ops)

    out = io.BytesIO()
    writer.write(out)
    return out.getvalue()
