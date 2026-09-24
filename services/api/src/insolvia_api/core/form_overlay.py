"""Output options (issue 13.11): a "Draft" watermark, a printed date/time
stamp, signature-page selection, `/s/` electronic signatures, and a chosen
subset of forms — for the assembled packet (9.6) and the single-form preview
(13.2) alike, so both features share one options model and one behaviour.

**Two different mechanisms, on purpose.**

- `/s/ Name` and the paired signature date are real AcroForm text fields —
  the SAME fields `core/form_projections` leaves blank on purpose ("the
  signature lines stay wet"). Filling them is therefore an ordinary
  `form_fill.FieldValues` augmentation, applied to the projection's own
  output BEFORE `fill_form` runs (`apply_signature_options`). It goes
  through the untouched deterministic engine, not this module's overlay
  pass — `form_fill.py` itself is unmodified, and a render with
  `sign_electronically=False` sends `fill_form` exactly the values the
  projection produced, byte-identical to today's golden.
- The "Draft" watermark and the top-margin date/time stamp are marks that
  have no field to land in — printed on top of an already-filled page. Those
  run as a SEPARATE overlay pass, strictly after `fill_form` has returned
  bytes (`stamp_pages`), drawn with pypdf's own low-level content-stream
  primitives (`pypdf.generic`) rather than adding reportlab: this service
  already depends on pypdf for the fill engine, and straight, single-colour
  text — no scene graph, no image codec, no transparency group — does not
  need a second PDF library's weight. A render with both stamps off never
  calls this pass at all, so the plain bytes stay exactly what `fill_form`
  produced.

Signature-page selection (`select_pages`) sits between the two: it drops
whole pages from the ALREADY-FILLED document using `FormRelease.signature_pages`
(forms/README.md's curated, checker-verified page list) — reordering pages
never touches a field value, so it is grouped with the overlay pass rather
than the fill.

Every function here is pure over its inputs (bytes in, bytes out) except
where it says otherwise; no store, no clock read internally — a caller-supplied
`printed_at` is what the date stamp prints, so the same options always
produce the same bytes for the same case data and the same clock reading.
"""

from __future__ import annotations

import io
import math
import re
from collections.abc import Mapping
from dataclasses import dataclass
from datetime import date, datetime
from typing import Literal, get_args

from insolvia_core.errors import FieldValidationError
from pypdf import PageObject, PdfReader, PdfWriter
from pypdf.generic import DictionaryObject, NameObject, StreamObject

from .form_fill import FieldFill, FieldValues, Text
from .form_projections.shared import CaseFile, format_date, full_name
from .form_templates import FieldSpec, FormRelease

SignaturePagesMode = Literal["all", "only", "omit"]
SIGNATURE_PAGES_MODES: tuple[SignaturePagesMode, ...] = get_args(SignaturePagesMode)


@dataclass(frozen=True)
class OutputOptions:
    """One set of output options, shared by packet assembly and the
    single-form preview (issue 13.11). Every field defaults to "the plain
    filing set" so a render with no options at all is exactly what packet
    assembly has always produced.

    `forms` names the subset of the case's own filed series to render, by
    short form key ("b101", not "form/b101") — the vocabulary the forms hub
    route already uses in its URL segment. `None` means every form the case
    files; only packet assembly reads it (the preview already names its one
    form in the URL), and it is refused there — see `parse_output_options`.
    """

    draft_watermark: bool = False
    print_date: bool = False
    signature_pages: SignaturePagesMode = "all"
    sign_electronically: bool = False
    forms: tuple[str, ...] | None = None

    @property
    def is_default(self) -> bool:
        """True for the plain filing set — no stamp, no page filter, no
        subset. What a packet record calls "a filing set" rather than "a
        draft" after the fact (issue 13.11's own done-when)."""
        return self == OutputOptions()


DEFAULT_OUTPUT_OPTIONS = OutputOptions()


def output_options_json(options: OutputOptions) -> dict[str, object]:
    """The wire shape recorded on a packet and echoed by the preview route.
    Always the full shape (never the api_client's "omit when default" rule —
    that rule is for OUTGOING requests; this is what the SERVER stamped an
    already-produced artifact with, and "the default was chosen" is itself
    a fact worth stating explicitly, not one to omit)."""
    body: dict[str, object] = {
        "draftWatermark": options.draft_watermark,
        "printDate": options.print_date,
        "signaturePages": options.signature_pages,
        "signElectronically": options.sign_electronically,
    }
    if options.forms is not None:
        body["forms"] = list(options.forms)
    return body


_FORM_KEY_RE = re.compile(r"^[a-z0-9]+$")


def parse_output_options(raw: object, *, allow_forms: bool) -> OutputOptions:
    """Validate the JSON shape `output_options_json` produces, the packet
    assembly job body's own `options` object. Every key is optional; an
    absent key keeps its default. Raises FieldValidationError (one message
    per bad field, `parse_job_acceptance`'s own shape) rather than letting a
    malformed body reach the worker.

    `allow_forms=False` is the preview route's rule: the form is already
    named in the URL, so an "options.forms" there is not a subset — it would
    be a second, possibly contradictory way to name the same thing.
    """
    if raw is None:
        raw = {}
    if not isinstance(raw, Mapping):
        raise FieldValidationError({"options": "options must be a JSON object."})

    errors: dict[str, str] = {}
    # "forms" is always a KNOWN key here — whether or not this caller allows
    # it — so a disallowed "forms" earns the specific message below, not a
    # generic "unknown option" alongside it.
    known = {
        "draftWatermark",
        "printDate",
        "signaturePages",
        "signElectronically",
        "forms",
    }
    unknown = sorted(set(raw) - known)
    if unknown:
        errors["options"] = f"unknown option(s): {', '.join(unknown)}."

    def _bool(key: str) -> bool:
        value = raw.get(key, False)
        if not isinstance(value, bool):
            errors[key] = "must be a boolean."
            return False
        return value

    draft_watermark = _bool("draftWatermark")
    print_date = _bool("printDate")
    sign_electronically = _bool("signElectronically")

    signature_pages_raw = raw.get("signaturePages", "all")
    if signature_pages_raw not in SIGNATURE_PAGES_MODES:
        errors["signaturePages"] = (
            "must be one of " + ", ".join(SIGNATURE_PAGES_MODES) + "."
        )
        signature_pages: SignaturePagesMode = "all"
    else:
        signature_pages = signature_pages_raw

    forms: tuple[str, ...] | None = None
    if allow_forms and "forms" in raw:
        raw_forms = raw["forms"]
        if (
            not isinstance(raw_forms, list)
            or not raw_forms
            or not all(isinstance(f, str) and _FORM_KEY_RE.match(f) for f in raw_forms)
        ):
            errors["forms"] = (
                'must be a non-empty list of lower-case form keys (e.g. "b101").'
            )
        elif len(set(raw_forms)) != len(raw_forms):
            errors["forms"] = "names the same form more than once."
        else:
            forms = tuple(raw_forms)
    elif not allow_forms and "forms" in raw:
        errors["forms"] = "this endpoint already names one form; omit it."

    if errors:
        raise FieldValidationError(errors)

    return OutputOptions(
        draft_watermark=draft_watermark,
        print_date=print_date,
        signature_pages=signature_pages,
        sign_electronically=sign_electronically,
        forms=forms,
    )


def parse_output_options_query(args: Mapping[str, str]) -> OutputOptions:
    """The preview route's adapter: Flask's `request.args` are always
    strings, so this turns the query-string spelling into the JSON shape
    `parse_output_options` validates, rather than teaching that function two
    wire formats. Boolean query params follow the common "present and not
    'false'/'0'" convention; absent means False, matching the JSON body's
    "absent key keeps its default" rule."""

    def _query_bool(key: str) -> bool | None:
        if key not in args:
            return None
        return args[key].strip().lower() not in ("", "0", "false")

    raw: dict[str, object] = {}
    for key in ("draftWatermark", "printDate", "signElectronically"):
        value = _query_bool(key)
        if value is not None:
            raw[key] = value
    if "signaturePages" in args:
        raw["signaturePages"] = args["signaturePages"]
    if "forms" in args:
        # Forwarded (not silently dropped) so `allow_forms=False` below gives
        # a real 400 — a client that pastes packet-assembly's query shape
        # onto this route gets told why, not a preview that ignored it.
        raw["forms"] = args["forms"].split(",")
    return parse_output_options(raw, allow_forms=False)


# ── `/s/` electronic signatures ────────────────────────────────────────────
# Real field values, applied BEFORE fill_form — see the module docstring.

_ID_SUFFIX_RE = re.compile(
    r"[._](signature_date|executed_on|date_signed|signature|date)$"
)
_DEBTOR_ID_RE = re.compile(r"debtor(1|2)", re.IGNORECASE)


def _signature_stem(field_id: str) -> str:
    """The field id with its trailing signature/date role stripped — "which
    signature line" independent of "signature, or its date". Two fields on
    the same form share a stem exactly when they are the same printed
    signature block's two halves (`sign.debtor1_signature` /
    `sign.debtor1_executed_on`, `debtor1_signature` / `debtor1_signature_date`,
    …) — the id structure every form in this set already carries, not a new
    curated fact."""
    return _ID_SUFFIX_RE.sub("", field_id)


def _signature_debtor_role(field_id: str) -> str | None:
    match = _DEBTOR_ID_RE.search(field_id)
    return f"debtor_{match.group(1)}" if match else None


def _paired_date_field(
    release: FormRelease, signature_field: FieldSpec
) -> FieldSpec | None:
    stem = _signature_stem(signature_field.id)
    for candidate in release.fields:
        if (
            candidate.type == "date"
            and candidate.id != signature_field.id
            and _signature_stem(candidate.id) == stem
        ):
            return candidate
    return None


def apply_signature_options(
    release: FormRelease,
    values: FieldValues,
    *,
    case_file: CaseFile,
    options: OutputOptions,
    today: date,
) -> FieldValues:
    """The projection's own FieldValues, with `/s/ <name>` (and, when
    `print_date` is also on, today's date) filled onto every debtor
    signature line this release has — a no-op, returning `values` itself,
    when `sign_electronically` is off, so the plain render's FieldValues (and
    therefore `fill_form`'s output) are untouched.

    Only DEBTOR signature lines are filled: a field whose id names neither
    debtor (the attorney's own signature block) is left wet on purpose — an
    attorney's signature is that attorney's own act, not a box this feature
    signs on their behalf. A debtor with no record in this case (no Debtor 2)
    or no name on file leaves that line wet too, same as an unset value
    always has.
    """
    if not options.sign_electronically:
        return values

    augmented: dict[str, FieldFill | Mapping[str, FieldFill]] = dict(values)
    for field in release.fields:
        if field.type != "signature" or len(field.pdf_names) != 1:
            continue
        role = _signature_debtor_role(field.id)
        if role is None:
            continue
        debtor = case_file.debtor(role)
        if debtor is None:
            continue
        name = full_name(debtor.name)
        if not name:
            continue
        augmented[field.id] = Text(f"/s/ {name}")

        if options.print_date:
            date_field = _paired_date_field(release, field)
            if date_field is not None and len(date_field.pdf_names) == 1:
                augmented[date_field.id] = Text(format_date(today.isoformat()))
    return augmented


# ── Page selection ──────────────────────────────────────────────────────────


def select_pages(
    pdf_bytes: bytes, release: FormRelease, *, mode: SignaturePagesMode
) -> bytes | None:
    """Keep every page (`mode="all"`), only the signature pages
    (`"only"`), or every page EXCEPT the signature pages (`"omit"`) —
    `FormRelease.signature_pages` is the 1-based page list, curated and
    checker-verified (forms/README.md).

    Returns `None` when the requested mode leaves no pages at all: a form
    with no signature line has nothing to keep under "only" (B106A/B has no
    signature block), and a form that is ENTIRELY a signature page has
    nothing left under "omit" (B106Dec's one page is nothing else). Either
    way there is no PDF to hand back — the caller decides what "nothing to
    render" means for it (skip the form in a packet; a problem in a preview).
    """
    if mode == "all":
        return pdf_bytes

    total_pages = len(PdfReader(io.BytesIO(pdf_bytes)).pages)
    signature_pages = set(release.signature_pages)
    if mode == "only":
        keep = sorted(p for p in signature_pages if 1 <= p <= total_pages)
    else:
        keep = [p for p in range(1, total_pages + 1) if p not in signature_pages]
    if not keep:
        return None

    writer = PdfWriter(clone_from=io.BytesIO(pdf_bytes))
    # Delete from the end so earlier indexes never shift under us.
    keep_zero_based = {p - 1 for p in keep}
    for index in range(len(writer.pages) - 1, -1, -1):
        if index not in keep_zero_based:
            writer.remove_page(index)

    out = io.BytesIO()
    writer.write(out)
    return out.getvalue()


# ── Stamps: "Draft" watermark, top-margin date/time ─────────────────────────
# Raw content-stream drawing with a standard-14 font (Helvetica — no
# embedding, every PDF reader carries it), pypdf's own primitives.

_OVERLAY_FONT_RESOURCE = "/InsolviaOverlay"
_WATERMARK_TEXT = "DRAFT"
_WATERMARK_GRAY = 0.75
_WATERMARK_ROTATE_DEGREES = 45.0
_STAMP_GRAY = 0.25
_STAMP_FONT_SIZE = 8.0
_STAMP_MARGIN_POINTS = 24.0


def _pdf_escape(text: str) -> str:
    return text.replace("\\", r"\\").replace("(", r"\(").replace(")", r"\)")


def _text_ops(
    text: str, *, x: float, y: float, size: float, rotate_degrees: float, gray: float
) -> bytes:
    radians = math.radians(rotate_degrees)
    a, b = math.cos(radians), math.sin(radians)
    c, d = -math.sin(radians), math.cos(radians)
    ops = (
        "q\n"
        f"{a:.6f} {b:.6f} {c:.6f} {d:.6f} {x:.2f} {y:.2f} cm\n"
        f"{gray:.2f} g\n"
        "BT\n"
        f"{_OVERLAY_FONT_RESOURCE} {size:.1f} Tf\n"
        "0 0 Td\n"
        f"({_pdf_escape(text)}) Tj\n"
        "ET\n"
        "Q\n"
    )
    return ops.encode("latin-1")


def _add_helvetica(writer: PdfWriter) -> object:
    font = DictionaryObject(
        {
            NameObject("/Type"): NameObject("/Font"),
            NameObject("/Subtype"): NameObject("/Type1"),
            NameObject("/BaseFont"): NameObject("/Helvetica"),
            NameObject("/Encoding"): NameObject("/WinAnsiEncoding"),
        }
    )
    return writer._add_object(font)


def _draw_on_page(
    writer: PdfWriter, page: PageObject, font_ref: object, ops: bytes
) -> None:
    resources = page.get("/Resources")
    resources_obj = (
        resources.get_object() if resources is not None else DictionaryObject()
    )
    fonts = resources_obj.get("/Font")
    fonts_obj = fonts.get_object() if fonts is not None else DictionaryObject()
    fonts_obj[NameObject(_OVERLAY_FONT_RESOURCE)] = font_ref
    resources_obj[NameObject("/Font")] = fonts_obj
    page[NameObject("/Resources")] = resources_obj

    existing = page.get_contents()
    combined = (existing.get_data() + b"\n" + ops) if existing is not None else ops

    stream = StreamObject()
    stream.set_data(combined)
    page[NameObject("/Contents")] = writer._add_object(stream)


def _stamp_text(printed_at: datetime) -> str:
    """The top-margin mark: date and time, the issue's own wording — UTC,
    stated explicitly so a filer in another timezone is never misled by an
    unlabeled clock reading."""
    return f"Printed {printed_at.strftime('%Y-%m-%d %H:%M')} UTC"


def stamp_pages(
    pdf_bytes: bytes,
    *,
    draft_watermark: bool,
    print_date: bool,
    printed_at: datetime,
) -> bytes:
    """Draw the requested marks on every page. A no-op (returns `pdf_bytes`
    itself, no PdfWriter round-trip) when both stamps are off, so a plain
    render never re-serializes the filled bytes at all."""
    if not draft_watermark and not print_date:
        return pdf_bytes

    writer = PdfWriter(clone_from=io.BytesIO(pdf_bytes))
    font_ref = _add_helvetica(writer)
    stamp_text = _stamp_text(printed_at) if print_date else None

    for page in writer.pages:
        box = page.mediabox
        width, height = float(box.width), float(box.height)
        ops = b""
        if draft_watermark:
            ops += _text_ops(
                _WATERMARK_TEXT,
                x=width * 0.22,
                y=height * 0.30,
                size=min(width, height) * 0.16,
                rotate_degrees=_WATERMARK_ROTATE_DEGREES,
                gray=_WATERMARK_GRAY,
            )
        if stamp_text is not None:
            ops += _text_ops(
                stamp_text,
                x=_STAMP_MARGIN_POINTS,
                y=height - _STAMP_MARGIN_POINTS,
                size=_STAMP_FONT_SIZE,
                rotate_degrees=0.0,
                gray=_STAMP_GRAY,
            )
        _draw_on_page(writer, page, font_ref, ops)

    out = io.BytesIO()
    writer.write(out)
    return out.getvalue()


def apply_output_stamps(
    pdf_bytes: bytes,
    release: FormRelease,
    *,
    options: OutputOptions,
    printed_at: datetime,
) -> bytes | None:
    """Page selection, then stamps — the whole post-fill pass for one
    rendered form. `None` means the requested signature-page mode leaves
    nothing of this form to show (`select_pages`'s own contract); the caller
    decides what that means for a packet vs. a preview.

    Order matters: dropping pages first means a stamp is never drawn on a
    page about to be discarded, and a form reduced to zero pages skips the
    (otherwise pointless) re-serialization stamping would do."""
    selected = select_pages(pdf_bytes, release, mode=options.signature_pages)
    if selected is None:
        return None
    return stamp_pages(
        selected,
        draft_watermark=options.draft_watermark,
        print_date=options.print_date,
        printed_at=printed_at,
    )
