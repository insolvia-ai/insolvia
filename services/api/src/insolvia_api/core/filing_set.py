"""The filing set and its checklist (ADR 0024 build PR 3).

ADR 0024 decided Insolvia files the case itself — but only in a district
whose driver has been verified on that court's training database (PR 10),
and only after the attorney approves (PRs 4-8). Everywhere else, and on
every hand-back, the attorney files from their own CM/ECF session. This
module is what that session works from: the documents in the order the
court dockets them, under the names it wants, each checked against the
court's PDF rules, and a checklist built from the case record saying what is
ready, what is missing and why, and where in the app to fix it.

It files nothing, reads no credential and prints nothing. It reuses packet
assembly rather than defining a second one: `packet_form_series` says which
forms this case files, `completeness_problems` says what is wrong, the
packet record's measured parts (`core/pdf_measure.py`) are what the PDF
checks judge, and the default file names are the zip's own
(`packet_assembly.part_file_name`).

**Per-district facts come from the court registry, never from a branch
here.** The docket order, file names, size cap, text-layer rule, signature
instrument, B121 handling, local forms, fee rule and registration notes are
all `insolvia_core.courts` facts. Where a fact is unknown the module falls
back to a DOCUMENTED COMMON DEFAULT and says so on the document or the
checklist item, with the registry's own note:

- docket order — the packet's own filing order (the national forms' order),
  then the creditor matrix, then a court instrument filed as its own event;
- file names — the packet's own zip names;
- size cap — the strictest cap any district in the same release records
  (`default_max_bytes`): a document under it is under every cap we know;
- text layer — a page without text is a warning where the court's rule is
  unknown and a failure where the court requires text-searchable PDF;
- page size — US letter everywhere (the official forms are letter); this is
  Insolvia's own preflight, not a court fact;
- B121 — submitted under its own restricted event unless the registry says
  the court does not take it (`not_filed`). Never part of anything public.

An unverified registry value is USED (it is the best reading we have) and
surfaced as `confirm` on the checklist, with its note. A `verified` one is
trusted.

**B121 carries the full SSN; this module never sees it.** The filing set is
built from `read_case_data` without `disclose_tax_ids`, and the only facts
it reads about B121's file are its measured size and page counts.
"""

from __future__ import annotations

from collections.abc import Mapping, Sequence
from dataclasses import dataclass
from datetime import date
from typing import Final, Literal, TypeVar

from insolvia_core import courts
from insolvia_core.cases import is_filed

from .creditor_matrix import MATRIX_FILE_NAME
from .forms_hub import resolve_case_form
from .packet_assembly import (
    CaseData,
    PacketProblem,
    completeness_problems,
    packet_form_series,
    part_file_name,
)
from .packets import Packet, PacketPart, is_filing_set

CheckName = Literal["size", "text_layer", "page_size"]
CheckOutcome = Literal["pass", "fail", "warn", "unmeasured"]
# How a document reaches the docket: uploaded as listed, as an event of its
# own, under the court's restricted SSN-statement event, or not at all.
Handling = Literal["file", "own_event", "restricted", "not_filed"]
# Where the document comes from: Insolvia's packet, or prepared outside it.
DocumentSource = Literal["packet", "outside"]
ItemStatus = Literal["ready", "missing", "action", "confirm"]
Basis = Literal["court", "court_unverified", "default"]

T = TypeVar("T")

B121_SERIES: Final = "form/b121"
MATRIX_KEY: Final = "creditor_matrix"
SIGNATURE_KEY: Final = "signature_instrument"
CERTIFICATION_KEY: Final = "matrix_certification"

_MB: Final = 1024 * 1024

# Where a problem's fix lives in the app, by `PacketProblem.source`. The
# forms hub groups every remaining problem per form, so it is the fallback.
_PROBLEM_LINKS: Final[Mapping[str, str]] = {
    "case": "",
    "debtors": "intake",
    "petitions": "petition",
    "employments": "income",
    "income_summaries": "income",
    "pay_period_records": "income",
    "other_income_records": "income",
    "means_test_inputs": "means-test",
    "plans": "plan",
    "creditors": "creditor-matrix",
}


@dataclass(frozen=True)
class Check:
    check: CheckName
    outcome: CheckOutcome
    message: str


@dataclass(frozen=True)
class FilingDocument:
    """One document of the filing set, in docket order."""

    key: str
    title: str
    file_name: str
    source: DocumentSource
    handling: Handling
    checks: tuple[Check, ...]
    note: str = ""
    # The packet file this document IS, as the assembly worker measured it
    # (size, pages, and the SHA-256 the per-filing approval binds to — ADR
    # 0024 PR 6). None for a document prepared outside Insolvia, and for a
    # packet document the latest packet does not contain.
    part: PacketPart | None = None


@dataclass(frozen=True)
class ChecklistItem:
    """One line of the hand-off checklist. `link` is an app segment under
    `/cases/<id>/` ("" is the case overview), or None when the step happens
    outside Insolvia (in the court's own session, or on paper)."""

    id: str
    status: ItemStatus
    title: str
    detail: str
    link: str | None = None


@dataclass(frozen=True)
class FilingSet:
    court_code: str | None
    court_name: str | None
    division_name: str | None
    registry_release: str
    # Every district is prepare-and-hand-off until its driver is verified
    # (ADR 0024 PR 10); none is yet, so this is always "hand_off" today.
    filing_method: Literal["hand_off"]
    packet: Packet | None
    order_basis: Basis
    names_basis: Basis
    max_bytes: int
    max_bytes_basis: Basis
    documents: tuple[FilingDocument, ...]
    checklist: tuple[ChecklistItem, ...]


def default_max_bytes(release: courts.CourtsRelease) -> int:
    """The strictest size cap any district of the release records — the
    common default for a court whose own cap is unknown."""
    caps = [
        d.pdf.max_bytes.value
        for d in release.districts
        if d.pdf.max_bytes.value is not None
    ]
    if not caps:
        raise LookupError(f"{release.release_id} records no PDF size cap at all")
    return min(caps)


def latest_filing_set_packet(packets: Sequence[Packet]) -> Packet | None:
    """The newest packet a court could be handed (`is_filing_set`), from a
    newest-first listing."""
    return next((p for p in packets if is_filing_set(p.options)), None)


def _basis(fact: courts.Fact[T]) -> Basis:
    if fact.value is None:
        return "default"
    return "court" if fact.verified else "court_unverified"


def _megabytes(byte_count: int) -> str:
    return f"{byte_count / _MB:.1f} MB"


def _checks(
    part: PacketPart | None,
    *,
    packet: Packet | None,
    max_bytes: int,
    text_required: bool | None,
) -> tuple[Check, ...]:
    """The three PDF checks (one, size, for the text matrix) of one file."""
    if part is None:
        why = (
            "No filing-set packet has been assembled yet — assemble it so the"
            " file can be checked."
            if packet is None
            else "This file is not in the latest packet, or that packet was"
            " assembled before files were measured — assemble again."
        )
        return (Check("size", "unmeasured", why),)
    size = (
        Check(
            "size",
            "pass",
            f"{_megabytes(part.byte_size)}, under the {_megabytes(max_bytes)} limit.",
        )
        if part.byte_size <= max_bytes
        else Check(
            "size",
            "fail",
            f"{_megabytes(part.byte_size)} is over the {_megabytes(max_bytes)}"
            f" limit — split it into {-(-part.byte_size // max_bytes)} files"
            " under the limit before uploading.",
        )
    )
    if part.page_count is None:  # the matrix: a text file
        return (size,)
    if part.unreadable:
        return (
            size,
            Check("text_layer", "fail", "The PDF could not be read."),
            Check("page_size", "fail", "The PDF could not be read."),
        )
    if part.pages_without_text:
        outcome: CheckOutcome = "fail" if text_required else "warn"
        rule = (
            "the court requires text-searchable PDFs"
            if text_required
            else "this court's rule is not recorded; two launch courts require"
            " text-searchable PDFs"
        )
        text = Check(
            "text_layer",
            outcome,
            f"{part.pages_without_text} of {part.page_count} pages have no text"
            f" layer — {rule}.",
        )
    else:
        text = Check("text_layer", "pass", "Every page has a text layer.")
    page = (
        Check(
            "page_size",
            "fail",
            f"{part.non_letter_pages} of {part.page_count} pages are not US"
            " letter (8.5 x 11 in).",
        )
        if part.non_letter_pages
        else Check("page_size", "pass", "Every page is US letter.")
    )
    return (size, text, page)


def _outside_checks(max_bytes: int) -> tuple[Check, ...]:
    return (
        Check(
            "size",
            "unmeasured",
            "Prepared outside Insolvia — before uploading, check it is a"
            f" text-searchable, letter-size PDF under {_megabytes(max_bytes)}.",
        ),
    )


def _form_title(data: CaseData, series_id: str, as_of: date) -> tuple[str, str]:
    """(title, short form key) for a form series, falling back to the id."""
    try:
        release = resolve_case_form(data.case, series_id, as_of=as_of)
    except LookupError:
        short = series_id.removeprefix("form/")
        return short.upper(), short
    label = release.official_number or release.title
    return f"{label} — {release.title}" if release.official_number else label, (
        release.form
    )


def _ordered(
    documents: list[FilingDocument], order: tuple[str, ...] | None
) -> list[FilingDocument]:
    """The court's recorded order, documents it does not name kept after in
    the default order; the default order unchanged when none is recorded."""
    if order is None:
        return documents
    rank = {key: index for index, key in enumerate(order)}
    return sorted(documents, key=lambda d: rank.get(d.key, len(rank)))


def build_filing_set(
    data: CaseData,
    *,
    packets: Sequence[Packet],
    release: courts.CourtsRelease,
    as_of: date,
) -> FilingSet:
    """The filing set and checklist for a case, against one registry
    release. Pure over its inputs; `packets` is the case's listing, newest
    first."""
    case = data.case
    district = release.district(case.court) if case.court else None
    division = (
        district.division(case.division)
        if district is not None and case.division
        else None
    )
    packet = latest_filing_set_packet(packets)
    measured = {part.name: part for part in packet.parts} if packet else {}
    fallback_cap = default_max_bytes(release)

    max_bytes_basis: Basis
    if district is not None and district.pdf.max_bytes.value is not None:
        max_bytes = district.pdf.max_bytes.value
        max_bytes_basis = _basis(district.pdf.max_bytes)
    else:
        max_bytes, max_bytes_basis = fallback_cap, "default"
    text_required = (
        district.pdf.text_searchable_required.value if district is not None else None
    )
    ssn = district.opening.ssn_statement if district is not None else None
    ssn_handling = ssn.value if ssn is not None else None
    file_names: Mapping[str, str] = (
        (district.opening.file_names.value or {}) if district is not None else {}
    )

    documents: list[FilingDocument] = []
    for position, series_id in enumerate(packet_form_series(data), start=1):
        title, form = _form_title(data, series_id, as_of)
        default_name = part_file_name(position, form)
        handling: Handling = "file"
        note = ""
        if series_id == B121_SERIES:
            if ssn_handling == "not_filed":
                handling = "not_filed"
                note = (
                    "This court does not take B121: the debtor's full Social"
                    " Security number is entered on the court's case-opening"
                    " screen instead. Do not upload this file."
                )
            else:
                handling = "restricted"
                note = (
                    "Carries the full Social Security number. Submit it only"
                    " under the court's Statement of Social Security Number"
                    " event, which keeps it off the public docket — never"
                    " inside the petition PDF."
                )
        part = measured.get(default_name)
        documents.append(
            FilingDocument(
                key=series_id,
                title=title,
                file_name=file_names.get(series_id, default_name),
                source="packet",
                handling=handling,
                checks=_checks(
                    part,
                    packet=packet,
                    max_bytes=max_bytes,
                    text_required=text_required,
                ),
                note=note,
                part=part,
            )
        )
    documents.append(
        FilingDocument(
            key=MATRIX_KEY,
            title="Creditor matrix (list of creditors, text file)",
            file_name=file_names.get(MATRIX_KEY, MATRIX_FILE_NAME),
            source="packet",
            handling="file",
            checks=_checks(
                measured.get(MATRIX_FILE_NAME),
                packet=packet,
                max_bytes=max_bytes,
                text_required=None,
            ),
            part=measured.get(MATRIX_FILE_NAME),
        )
    )
    if district is not None:
        instrument = district.opening.signature_instrument.value
        if instrument is not None and instrument.own_docket_event:
            documents.append(
                FilingDocument(
                    key=SIGNATURE_KEY,
                    title=instrument.title,
                    file_name=file_names.get(SIGNATURE_KEY, "signature-instrument.pdf"),
                    source="outside",
                    handling="own_event",
                    checks=_outside_checks(max_bytes),
                    note="Signed by the debtor in wet ink; scan it and file it"
                    " as its own docket event.",
                )
            )
        if (
            instrument is not None and instrument.kind == "matrix_certification"
        ) or district.matrix.certification_required.value is True:
            documents.append(
                FilingDocument(
                    key=CERTIFICATION_KEY,
                    title="Certification of the creditor mailing matrix",
                    file_name=file_names.get(
                        CERTIFICATION_KEY, "matrix-certification.pdf"
                    ),
                    source="outside",
                    handling="own_event",
                    checks=_outside_checks(max_bytes),
                    note="The court's signed certification that the matrix"
                    " lists every creditor — prepared on the court's own form.",
                )
            )
    order_fact = district.opening.docket_order if district is not None else None
    ordered = _ordered(documents, order_fact.value if order_fact else None)

    return FilingSet(
        court_code=district.code if district is not None else None,
        court_name=district.name if district is not None else None,
        division_name=division.name if division is not None else None,
        registry_release=release.release_id,
        filing_method="hand_off",
        packet=packet,
        order_basis=_basis(order_fact) if order_fact else "default",
        names_basis=(
            _basis(district.opening.file_names) if district is not None else "default"
        ),
        max_bytes=max_bytes,
        max_bytes_basis=max_bytes_basis,
        documents=tuple(ordered),
        checklist=_checklist(
            data,
            district=district,
            division=division,
            packet=packet,
            documents=ordered,
            max_bytes_basis=max_bytes_basis,
        ),
    )


# ── The checklist ────────────────────────────────────────────────────────────


def _problem_items(problems: tuple[PacketProblem, ...]) -> list[ChecklistItem]:
    """One item per problem source, first message plus a count — the forms
    hub has the full per-form list behind the link."""
    by_source: dict[str, list[PacketProblem]] = {}
    for problem in problems:
        by_source.setdefault(problem.source, []).append(problem)
    items: list[ChecklistItem] = []
    for source, group in by_source.items():
        more = f" (and {len(group) - 1} more)" if len(group) > 1 else ""
        items.append(
            ChecklistItem(
                id=f"case_data:{source}",
                status="missing",
                title=f"Case data: {source.replace('_', ' ')}",
                detail=group[0].message + more,
                link=_PROBLEM_LINKS.get(source, "forms"),
            )
        )
    return items


def _filed_item(data: CaseData) -> ChecklistItem:
    """The filed case's one checklist line: the court's case number and the
    petition date where they are recorded, and that nothing more is filed
    from here."""
    case = data.case
    facts = [
        f"Case number {case.case_number}."
        if case.case_number
        else "No case number is recorded yet.",
        f"Filed {case.filed_at}."
        if case.filed_at
        else "No filing date is recorded yet.",
    ]
    return ChecklistItem(
        id="filed",
        status="ready",
        title="This case is filed",
        detail=" ".join(facts)
        + " No further filing is possible from this filing set: the documents"
        " above are the record of what was prepared for the court. To change a"
        " filed schedule, mark the changed items amended and assemble an"
        " amendment.",
    )


def _confirm_or(fact: courts.Fact[T], status: ItemStatus) -> ItemStatus:
    return status if fact.verified else "confirm"


def _checklist(
    data: CaseData,
    *,
    district: courts.CourtDistrict | None,
    division: courts.Division | None,
    packet: Packet | None,
    documents: Sequence[FilingDocument],
    max_bytes_basis: Basis,
) -> tuple[ChecklistItem, ...]:
    case = data.case
    items: list[ChecklistItem] = []

    # 0. A filed case is ONE state, not a hand-off with a problem in it.
    # Every step below is about getting the case onto the court's docket,
    # which has happened — and `completeness_problems` refuses a filed case
    # outright (its packet is pinned, never re-assembled), which would read
    # here as a "case data" gap the attorney has nothing to fix for. So the
    # checklist is this one item; the documents above stay, as the record
    # of what was prepared. The approval's `filed` blocker is what refuses a
    # second filing (`filing_approval._blockers`).
    if is_filed(case.status):
        return (_filed_item(data),)

    # 1. The court.
    if district is None or division is None:
        items.append(
            ChecklistItem(
                id="court",
                status="missing",
                title="Court and division",
                detail="The case does not name a court and division from the"
                " court registry, so nothing below can follow that court's rules"
                " — the common defaults are shown instead.",
                link="",
            )
        )
    else:
        items.append(
            ChecklistItem(
                id="court",
                status="ready",
                title="Court and division",
                detail=f"{district.name}, {division.name}.",
            )
        )

    # 2. The record itself.
    problems = completeness_problems(data)
    if problems:
        items.extend(_problem_items(problems))
    else:
        items.append(
            ChecklistItem(
                id="case_data",
                status="ready",
                title="Case data",
                detail="The completeness check finds nothing missing.",
            )
        )

    # 3. The packet, and what its files measured.
    if packet is None:
        items.append(
            ChecklistItem(
                id="packet",
                status="missing",
                title="Filing packet",
                detail="No filing-set packet has been assembled (a draft,"
                " partial, signature-page or amendment print does not count).",
                link="packet",
            )
        )
    else:
        items.append(
            ChecklistItem(
                id="packet",
                status="ready",
                title="Filing packet",
                detail=f"Assembled {packet.created_at[:10]}. Assemble again"
                " after any change to the case.",
                link="packet",
            )
        )
    in_packet = [d for d in documents if d.source == "packet"]
    failing = [d for d in in_packet if any(c.outcome == "fail" for c in d.checks)]
    unmeasured = [
        d for d in in_packet if any(c.outcome == "unmeasured" for c in d.checks)
    ]
    warned = [d for d in in_packet if any(c.outcome == "warn" for c in d.checks)]
    if failing or unmeasured:
        names = ", ".join(d.file_name for d in (failing or unmeasured))
        items.append(
            ChecklistItem(
                id="pdf_checks",
                status="missing",
                title="File checks",
                detail=(
                    f"Failing the court's file rules: {names}."
                    if failing
                    else f"Not checked yet: {names}. Assemble the packet."
                ),
                link="packet",
            )
        )
    else:
        items.append(
            ChecklistItem(
                id="pdf_checks",
                status="confirm" if warned else "ready",
                title="File checks",
                detail=(
                    "Pages without a text layer: "
                    + ", ".join(d.file_name for d in warned)
                    + "."
                    if warned
                    else "Every file is within the size limit, text-searchable"
                    " and US letter."
                ),
            )
        )
    if max_bytes_basis != "court":
        note = (
            district.pdf.max_bytes.note
            if district is not None
            else "No court is set on the case."
        )
        items.append(
            ChecklistItem(
                id="size_limit",
                status="confirm",
                title="Size limit",
                detail=(
                    "This court's file-size limit is not verified; the filing"
                    " set checks against "
                    + (
                        "the figure on record"
                        if max_bytes_basis == "court_unverified"
                        else "the strictest limit any launch court records"
                    )
                    + f". {note}"
                ).strip(),
            )
        )

    if district is None:
        return tuple(items)

    opening = district.opening
    # 4. The order and the names the documents go up in.
    for fact_id, title, fact in (
        ("docket_order", "Docket order", opening.docket_order),
        ("file_names", "File names", opening.file_names),
    ):
        if not fact.verified:
            items.append(
                ChecklistItem(
                    id=fact_id,
                    status="confirm",
                    title=title,
                    detail=(
                        "Not recorded for this court; the packet's own is used."
                        if fact.value is None
                        else "Recorded for this court but not verified."
                    )
                    + (f" {fact.note}" if fact.note else ""),
                )
            )

    # 5. B121 — the SSN statement.
    ssn = opening.ssn_statement
    if ssn.value == "not_filed":
        ssn_detail = (
            "This court does not take B121: the debtor's full Social Security"
            " number is entered on the court's case-opening screen. Insolvia"
            " does not display it; read it from the debtor's documents."
        )
    else:
        ssn_detail = (
            "Submit B121 only under the court's Statement of Social Security"
            " Number event, which keeps it off the public docket."
        )
    items.append(
        ChecklistItem(
            id="ssn_statement",
            status=_confirm_or(ssn, "action"),
            title="Statement About Your Social Security Numbers (B121)",
            detail=ssn_detail + ("" if ssn.verified else f" {ssn.note}".rstrip()),
        )
    )

    # 6. How the debtor's signature reaches the court.
    instrument_fact = opening.signature_instrument
    instrument = instrument_fact.value
    if instrument is None or instrument.kind == "other":
        items.append(
            ChecklistItem(
                id="signature",
                status="confirm",
                title="The debtor's signature",
                detail="How this court takes the debtor's signature on an"
                " electronic filing is not recorded. " + (instrument_fact.note or ""),
            )
        )
    else:
        parts = [instrument.title + "."]
        if instrument.wet_ink_required:
            parts.append("The debtor signs in wet ink.")
        if instrument.retention:
            parts.append(f"Keep the originals {instrument.retention}.")
        items.append(
            ChecklistItem(
                id="signature",
                status=_confirm_or(instrument_fact, "action"),
                title="The debtor's signature",
                detail=" ".join(parts)
                + ("" if instrument_fact.verified else f" {instrument_fact.note}"),
            )
        )

    # 7. Local forms — each says when it is required, in the court's words.
    for form in opening.local_forms:
        items.append(
            ChecklistItem(
                id=f"local_form:{form.id}",
                status="confirm",
                title=f"Local form {form.id}: {form.title}",
                detail=f"When required: {form.when_required}"
                + (f" Form: {form.url}" if form.url else ""),
            )
        )

    # 8. The fee — paid in the attorney's own session (ADR 0024: no card
    # details are stored or entered by Insolvia).
    fee = opening.fee_rule
    items.append(
        ChecklistItem(
            id="fee",
            status=_confirm_or(fee, "action"),
            title="Filing fee",
            detail=(
                fee.value.deadline
                if fee.value is not None
                else "This court's fee procedure is not recorded — check it"
                " before filing."
            )
            + " The fee is paid in the court's own session; Insolvia does not"
            " take payment details.",
        )
    )

    # 9. The attorney's e-filing registration.
    training = district.registration.training_required
    notes = district.registration.notes
    items.append(
        ChecklistItem(
            id="registration",
            status=_confirm_or(training, "action"),
            title="E-filing registration",
            detail=f"The filing attorney must be registered to e-file in the"
            f" {district.name}"
            + (" (the court requires training first)" if training.value else "")
            + "."
            + (f" {notes}" if notes else ""),
        )
    )

    # 10. How the case is opened — the hand-off itself.
    live = district.cmecf.live_url.value
    items.append(
        ChecklistItem(
            id="filing_method",
            status="action",
            title="Open the case on the court's screens",
            detail="Automated filing is not available for this court yet."
            " Sign in to the court's CM/ECF"
            + (f" ({live})" if live else "")
            + ", open the case, and upload the documents above in this order."
            " Case Upload is not verified for this court, so enter the opening"
            " screens from the case record.",
        )
    )
    return tuple(items)
