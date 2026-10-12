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

The case's `status=filed` and its court case number are written by the
worker's capture in the same transaction as the record's `filed`
(worker._capture, ADR 0024 PR 8).
"""

from __future__ import annotations

import io
import re
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
)
from insolvia_core.errors import ValidationError
from insolvia_core.filings import Confirmation, Filing
from pypdf import PdfWriter

RECEIPT_KIND: Final = "court_notice"
RECEIPT_CONTENT_TYPE: Final = "application/pdf"
RECEIPT_FILE_NAME: Final = "court-filing-receipt.pdf"
CONFIRMATION_CONTENT_TYPE: Final = "text/html; charset=utf-8"

_WIDTH: Final = 612.0
_HEIGHT: Final = 792.0
_MARGIN: Final = 72.0


_UUID_RE: Final = re.compile(
    r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\Z"
)


def filing_object_key(case_id: str, filing_id: str, object_id: str) -> str:
    """Where a filing's own objects live: cases/<case>/filings/<filing>/<id>.

    A prefix of its own, as packets have (`packets.packet_object_key`), so the
    worker's write grant is `cases/*/filings/*` and can never touch an
    uploaded source document (cases/<case>/<id>) or a packet. Server-minted
    uuids only, checked rather than sanitised — documents.object_key's
    no-PII-in-a-key rule."""
    for value in (case_id, filing_id, object_id):
        if not _UUID_RE.match(value):
            raise ValidationError("object keys are built from server-minted uuids only")
    return f"cases/{case_id}/filings/{filing_id}/{object_id}"


def confirmation_ref(case_id: str, filing_id: str) -> str:
    """Where the verbatim confirmation page is stored."""
    return filing_object_key(case_id, filing_id, str(uuid.uuid4()))


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


# The receipt document's id is DERIVED from the filing's (uuid5 under this
# namespace) rather than minted: one filing has one receipt, and a redelivered
# job that finishes a capture finds the first run's row by id instead of
# writing a second receipt (worker._store_receipt). Still a uuid, so
# `filing_object_key`'s server-minted-uuids-only check holds. The namespace
# is a fixed random uuid; changing it would orphan nothing but would let a
# capture in flight across the deploy write a second row — so never change it.
_RECEIPT_NAMESPACE: Final = uuid.UUID("5d0f5e4e-2f58-4a7e-9a0b-4f3c1f8e2b61")


def receipt_document_id(filing_id: str) -> str:
    return str(uuid.uuid5(_RECEIPT_NAMESPACE, f"receipt:{filing_id}"))


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
    document_id = receipt_document_id(filing.filing_id)
    return replace(
        document,
        id=document_id,
        status=STATUS_STORED,
        storage_ref=filing_object_key(filing.case_id, filing.filing_id, document_id),
    )
