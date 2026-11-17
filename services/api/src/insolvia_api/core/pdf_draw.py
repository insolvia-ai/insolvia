"""Raw content-stream text drawing, shared by every pass that marks up an
ALREADY-RENDERED PDF page rather than filling an AcroForm field.

Extracted from `core/form_overlay.py` (issue #370) when a second caller
arrived: the "Draft" watermark and print-date stamp (`form_overlay.stamp_pages`)
and the generated amendment cover sheet (`core/amendment_cover_sheet.py`) both
need "put this text at this point, in this font, optionally rotated" and
nothing more elaborate — pypdf's own low-level primitives (`pypdf.generic`),
a standard-14 font (Helvetica, no embedding, every PDF reader carries it), no
reportlab. One copy of the escaping and content-stream assembly means both
callers are byte-for-byte the same drawing code, which is what keeps the
packet's golden tests meaningful for one and not for the other.

Nothing here reads a form spec or a case record — it is pure PDF mechanics,
one level below `core/form_fill.py`'s AcroForm field values.
"""

from __future__ import annotations

import math

from pypdf import PageObject, PdfWriter
from pypdf.generic import DictionaryObject, NameObject, StreamObject

# The resource name every drawn font is registered under. One name is safe
# across pages because each page gets its own /Resources /Font dict — a
# clash would only matter if two different fonts needed the same page, which
# nothing here does.
OVERLAY_FONT_RESOURCE = "/InsolviaOverlay"


def pdf_escape(text: str) -> str:
    """Escape a literal string for a PDF content stream: backslash first, so
    the backslashes escaping the parentheses are not themselves re-escaped."""
    return text.replace("\\", r"\\").replace("(", r"\(").replace(")", r"\)")


def text_ops(
    text: str, *, x: float, y: float, size: float, rotate_degrees: float, gray: float
) -> bytes:
    """The content-stream operators for one line of text: a rotation matrix
    around `(x, y)`, a gray fill, and a `Tj` show. Callers compose several of
    these into one page's drawing ops."""
    radians = math.radians(rotate_degrees)
    a, b = math.cos(radians), math.sin(radians)
    c, d = -math.sin(radians), math.cos(radians)
    ops = (
        "q\n"
        f"{a:.6f} {b:.6f} {c:.6f} {d:.6f} {x:.2f} {y:.2f} cm\n"
        f"{gray:.2f} g\n"
        "BT\n"
        f"{OVERLAY_FONT_RESOURCE} {size:.1f} Tf\n"
        "0 0 Td\n"
        f"({pdf_escape(text)}) Tj\n"
        "ET\n"
        "Q\n"
    )
    # The font is registered with /WinAnsiEncoding, which is cp1252. A
    # character outside it (a debtor's name in Vietnamese, say) prints as
    # "?" rather than raising — a stamp or a cover-sheet caption is never
    # worth failing a whole packet over.
    return ops.encode("cp1252", errors="replace")


def add_helvetica(writer: PdfWriter) -> object:
    """Register the standard-14 Helvetica font on `writer` and return the
    indirect reference every drawn page's /Resources /Font entry points at."""
    font = DictionaryObject(
        {
            NameObject("/Type"): NameObject("/Font"),
            NameObject("/Subtype"): NameObject("/Type1"),
            NameObject("/BaseFont"): NameObject("/Helvetica"),
            NameObject("/Encoding"): NameObject("/WinAnsiEncoding"),
        }
    )
    return writer._add_object(font)


def draw_on_page(
    writer: PdfWriter, page: PageObject, font_ref: object, ops: bytes
) -> None:
    """Append `ops` to `page`'s content stream, registering `font_ref` as
    `OVERLAY_FONT_RESOURCE` in its /Resources — the page's EXISTING content
    stream is kept and extended, never replaced, so whatever was already
    drawn (an official form's own widgets, an earlier stamp) stays exactly
    as it was."""
    resources = page.get("/Resources")
    resources_obj = (
        resources.get_object() if resources is not None else DictionaryObject()
    )
    fonts = resources_obj.get("/Font")
    fonts_obj = fonts.get_object() if fonts is not None else DictionaryObject()
    fonts_obj[NameObject(OVERLAY_FONT_RESOURCE)] = font_ref
    resources_obj[NameObject("/Font")] = fonts_obj
    page[NameObject("/Resources")] = resources_obj

    existing = page.get_contents()
    combined = (existing.get_data() + b"\n" + ops) if existing is not None else ops

    stream = StreamObject()
    stream.set_data(combined)
    page[NameObject("/Contents")] = writer._add_object(stream)
