"""What a packet's files measure — the facts the filing set's PDF checks are
judged against (ADR 0024 build PR 3).

A court refuses an upload for three reasons this module can see in the bytes:
it is over the court's size cap, it has pages with no text layer (a scan
where the court wants a text-searchable PDF), or a page is not US letter.
The RULES are per court and live in the registry (`insolvia_core.courts`,
`CourtDistrict.pdf`) and are applied by `core/filing_set.py`; this module only
measures, once, at assembly — the one moment the worker holds the bytes
(packet assembly writes them to the bucket and nothing in the API reads them
back). So the measurement is stored on the packet record (`PacketPart`) and a
registry correction re-judges every stored packet without re-rendering it.

Only counts and sizes leave this module. The text a page carries is read to
decide whether it HAS any and is never kept: B121 is one of the parts, and
its text is the debtor's full Social Security number.
"""

from __future__ import annotations

import io
import re
from typing import Final

from pypdf import PageObject, PdfReader
from pypdf.errors import PyPdfError

from .packets import PacketPart

# US letter in PDF points (8.5 x 11 in at 72 pt/in), either orientation.
LETTER_POINTS: Final = (612.0, 792.0)
# Generous enough for a template whose media box was rounded in authoring,
# tight enough that A4 (595 x 842) and legal (612 x 1008) both fail.
_LETTER_TOLERANCE_POINTS: Final = 1.5

# A text-showing operator in a content stream: Tj, TJ, ' and ". The cheap
# test runs first; `extract_text` is the fallback for text drawn from a form
# XObject the page invokes rather than its own stream.
_TEXT_OPERATOR_RE: Final = re.compile(rb"(?:\bT[jJ]\b|[)\]]\s*['\"])")


def _is_letter(page: PageObject) -> bool:
    box = page.mediabox
    width, height = float(box.width), float(box.height)
    short, long = sorted((abs(width), abs(height)))
    return (
        abs(short - LETTER_POINTS[0]) <= _LETTER_TOLERANCE_POINTS
        and abs(long - LETTER_POINTS[1]) <= _LETTER_TOLERANCE_POINTS
    )


def _has_text(page: PageObject) -> bool:
    contents = page.get_contents()
    if contents is not None and _TEXT_OPERATOR_RE.search(contents.get_data()):
        return True
    # Read to decide, never returned — see the module docstring.
    return bool(page.extract_text().strip())


def measure_part(name: str, content: bytes) -> PacketPart:
    """One zip entry's measurements. A `.pdf` is opened and every page
    measured; anything else (the creditor matrix's `.txt`) is a size only.

    A PDF that cannot be parsed is measured as zero pages: the filing set
    then reports it as having no text layer and no letter pages to vouch
    for, which fails it — the right answer for a file a court's own reader
    may refuse too."""
    if not name.lower().endswith(".pdf"):
        return PacketPart(name=name, byte_size=len(content))
    try:
        reader = PdfReader(io.BytesIO(content))
        pages = list(reader.pages)
        non_letter = sum(1 for page in pages if not _is_letter(page))
        without_text = sum(1 for page in pages if not _has_text(page))
    except PyPdfError:
        return PacketPart(
            name=name,
            byte_size=len(content),
            page_count=0,
            non_letter_pages=0,
            pages_without_text=0,
            unreadable=True,
        )
    return PacketPart(
        name=name,
        byte_size=len(content),
        page_count=len(pages),
        non_letter_pages=non_letter,
        pages_without_text=without_text,
    )


def measure_parts(parts: tuple[tuple[str, bytes], ...]) -> tuple[PacketPart, ...]:
    """Every part, in the packet's own (zip) order."""
    return tuple(measure_part(name, content) for name, content in parts)
