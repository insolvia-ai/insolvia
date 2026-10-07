"""The receipt Insolvia keeps with the case once the court confirmed a filing
(ADR 0024, "Capture of what came back": "the case number, the filing date and
time, the docket entries it created, the confirmation page itself (HTML and a
rendered PDF), and the payment receipt if one was produced").

Two things are stored, under the case:

- the court's confirmation page, VERBATIM, as a blob — what the court said,
  byte for byte, for anyone reconciling later. It is not a case Document:
  HTML is deliberately outside the documents allowlist (it is script-bearing
  markup a browser would render from a presigned URL);
- a one-page PDF "Court filing receipt", a case Document (`court_notice`),
  drawn from the captured facts — the same drawing primitives the packet's
  amendment cover sheet uses (services/api core/pdf_draw.py), so no second
  PDF library joins the image. It is Insolvia's record of the court's
  answer, and says so on the page.

The case's `status=filed`, its court case number and the pin freeze are ADR
0024 PR 8, which reads the filing record this module's caller writes.
"""

from __future__ import annotations

import io
import textwrap
import uuid
from dataclasses import replace
from typing import Final

from insolvia_api.core import pdf_draw
from insolvia_core.documents import (
    STATUS_STORED,
    Document,
    DocumentDraft,
    create_document,
    object_key,
)
from pypdf import PdfWriter

from .filings import Confirmation, Filing

RECEIPT_KIND: Final = "court_notice"
RECEIPT_CONTENT_TYPE: Final = "application/pdf"
RECEIPT_FILE_NAME: Final = "court-filing-receipt.pdf"
CONFIRMATION_CONTENT_TYPE: Final = "text/html; charset=utf-8"

_WIDTH: Final = 612.0
_HEIGHT: Final = 792.0
_MARGIN: Final = 72.0


def confirmation_ref(case_id: str) -> str:
    """Where the verbatim confirmation page is stored: a fresh object under
    the case, named by uuid only (documents.object_key's no-PII rule)."""
    return object_key(case_id, str(uuid.uuid4()))


def render_receipt(filing: Filing, confirmation: Confirmation) -> bytes:
    writer = PdfWriter()
    page = writer.add_blank_page(width=_WIDTH, height=_HEIGHT)
    font = pdf_draw.add_helvetica(writer)
    ops = b""
    y = _HEIGHT - _MARGIN

    def line(text: str, *, size: float = 11.0, gray: float = 0.0) -> None:
        nonlocal ops, y
        for part in textwrap.wrap(
            text, width=int((_WIDTH - 2 * _MARGIN) / (size * 0.55))
        ) or [""]:
            ops += pdf_draw.text_ops(
                part, x=_MARGIN, y=y, size=size, rotate_degrees=0.0, gray=gray
            )
            y -= max(16.0, size * 1.3)

    line("COURT FILING RECEIPT", size=16.0)
    y -= 8
    line(
        "Recorded by Insolvia from the court's confirmation screen. The court's"
        " own notices remain the evidence of what was filed.",
        size=9.0,
        gray=0.35,
    )
    y -= 8
    line(f"Court: {filing.court.upper()}   Division: {filing.division}")
    line(f"Case number: {confirmation.case_number}")
    line(f"Filed at: {confirmation.filed_at}")
    if confirmation.receipt_number:
        line(f"Court receipt: {confirmation.receipt_number}")
    if confirmation.fee_due:
        line(f"Filing fee due: {confirmation.fee_due} - pay in your own CM/ECF session")
    y -= 8
    line("Docket entries:")
    for entry in confirmation.docket_entries:
        line(f"  {entry}", size=10.0)
    y -= 8
    line(
        f"Filing {filing.filing_id}, approval {filing.approval_id}", size=9.0, gray=0.35
    )
    pdf_draw.draw_on_page(writer, page, font, ops)
    out = io.BytesIO()
    writer.write(out)
    return out.getvalue()


def receipt_document(filing: Filing, content: bytes) -> Document:
    """The receipt's Document record — written by the worker with its bytes,
    so it is born `stored` (the packet's pattern: no presigned upload, nothing
    for the unconfirmed-upload reaper)."""
    document = create_document(
        DocumentDraft(
            kind=RECEIPT_KIND,
            file_name=RECEIPT_FILE_NAME,
            content_type=RECEIPT_CONTENT_TYPE,
            byte_size=len(content),
        ),
        case_id=filing.case_id,
        uploaded_by=filing.attorney_id,
    )
    return replace(document, status=STATUS_STORED)
