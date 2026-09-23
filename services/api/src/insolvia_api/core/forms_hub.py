"""The forms hub (issue 13.2 / #343): per-form status, and a single-form
PDF preview, for the forms a case's chapter and pin require.

`packet_assembly.py` gates the WHOLE Chapter 7 set at once and returns one
ZIP — the right shape for filing, the wrong one for a preparer who wants to
know "is Schedule G done" without touching Schedule D. This module answers
that question by walking `packet_assembly`'s own functions per form rather
than defining a second "required forms" or "problems" — `packet_form_series`
still says which forms this case files, and `completeness_problems` still
says what is wrong; the only new thing here is GROUPING that single problem
list by the form each entry belongs to, and rendering one form at a time
through the same fill pipeline `assemble` uses for all of them.

**Which form a problem belongs to.** `PacketProblem.source` is a collection
name ("claims", "households", …), "case"/"debtors" for the two non-generic
records, or a form series id for a projection/fill refusal — but
`completeness_problems` never emits the last kind (those come from
`assemble`'s later stages, out of scope here per the issue). So this module
carries its own map from a collection source to the form(s) it feeds,
transcribed from case-data-model.md's "Core entities" table (the `Feeds`
column) — the SAME facts, not a second guess at them. `case` and `debtors`
problems are universal: every form's caption prints the case's district and
the debtor's name, so a missing Debtor 1 blocks every form, not just B101.
"""

from __future__ import annotations

import re
import uuid
from dataclasses import dataclass
from datetime import date
from typing import Final, Literal

from insolvia_core.cases import Case
from insolvia_core.errors import ValidationError

from .case_summary import summarise
from .form_fill import FormFillError, fill_form
from .form_projections import CaseFile, FormProjectionError, project
from .form_projections.shared import claims_of
from .form_templates import FormRelease, get_form, resolve_form
from .packet_assembly import (
    CaseData,
    PacketProblem,
    completeness_problems,
    packet_form_series,
    to_case_file,
)

# Collection source -> every form series it feeds, per case-data-model.md's
# Feeds column. A source missing here either never appears on a
# `PacketProblem` (assets, contract_leases, sofa_entries, creditors — none of
# `completeness_problems`'s checks name them) or has no packet-form counterpart
# ("creditors" problems come only from the matrix, out of this issue's scope).
FORM_PROBLEM_SOURCES: Final[dict[str, tuple[str, ...]]] = {
    "petitions": ("form/b101",),
    "means_test_inputs": ("form/b122a2",),
    "households": ("form/b106j", "form/b106j2"),
    "income_summaries": ("form/b106i",),
    "claims": ("form/b106d", "form/b106ef"),
    "exemptions": ("form/b106c",),
    "employments": ("form/b106i",),
    "pay_period_records": ("form/b122a1", "form/b122a2"),
    "other_income_records": ("form/b122a1", "form/b122a2"),
    "expenses": ("form/b106j", "form/b106j2"),
    "dependents": ("form/b106j", "form/b106j2"),
    "codebtors": ("form/b106h",),
}

# The two sources that gate every form: the case's own header (district,
# chapter, filed status) and Debtor 1's identity print on every form's
# caption, so a problem here is not specific to any one schedule.
_UNIVERSAL_SOURCES: Final = ("case", "debtors")


def group_problems(data: CaseData) -> dict[str, tuple[PacketProblem, ...]]:
    """Every `completeness_problems` entry, grouped by the form series it
    belongs to — one dict entry per form this case files (`packet_form_series`),
    in that same order. A problem whose source feeds more than one form (a
    shared household feeding both 106J and 106J-2) appears under each; a
    universal one (`case`, `debtors`) appears under all of them.

    This is the ONE grouping both the hub listing and the single-form preview
    read, so the two features can never disagree about which problems block
    which form.
    """
    series_ids = packet_form_series(data)
    by_form: dict[str, list[PacketProblem]] = {series: [] for series in series_ids}
    for problem in completeness_problems(data):
        if problem.source in _UNIVERSAL_SOURCES:
            targets: tuple[str, ...] = series_ids
        else:
            targets = tuple(
                series
                for series in FORM_PROBLEM_SOURCES.get(problem.source, ())
                if series in by_form
            )
        for series in targets:
            by_form[series].append(problem)
    return {series: tuple(problems) for series, problems in by_form.items()}


def resolve_case_form(case: Case, series_id: str, *, as_of: date) -> FormRelease:
    """The release this case's forms print from: the release packet assembly
    already pinned for this series once it has (effective-dating.md's
    "pinned" phase), the currently-effective release while the case still
    floats — applied per form, the way `resolve_form`/`get_form` apply it to
    the whole set in `packet_assembly.assemble`.

    Raises `LookupError` exactly as `resolve_form` does when no release is
    effective on or before `as_of` — the caller folds that into a problem
    like `assemble` does, rather than letting it surface as a 500.
    """
    pin = (case.form_revisions or {}).get(series_id)
    if pin is not None:
        # `case.form_revisions` stores the PIN (`effective_date[+sequence]`,
        # `FormRelease.pin`), not the full release id `get_form` takes
        # (`FormRelease.release_id`, `<series_id>@<pin>`) — the same
        # reconstruction `form_revisions_as_of`'s callers do everywhere else
        # a pin comes back off a case.
        return get_form(series_id, f"{series_id}@{pin}")
    return resolve_form(series_id, as_of)


FormMetricKind = Literal["count", "total"]


@dataclass(frozen=True)
class FormMetric:
    """The hub row's one number: how many entities this schedule prints
    (Schedules A/B-H), or the dollar figure it totals (I, J) — straight from
    `case_summary.summarise`, never re-summed here (ADR 0001)."""

    kind: FormMetricKind
    value: str


@dataclass(frozen=True)
class FormSummary:
    """One row of the forms hub: a form this case files, what it is called,
    its one metric (when the issue defines one for it), and the completeness
    problems that belong to it."""

    series: str
    form: str
    title: str
    official_number: str
    metric: FormMetric | None
    problems: tuple[PacketProblem, ...]


def _metrics(data: CaseData, case_file: CaseFile) -> dict[str, FormMetric]:
    """Item counts for Schedules A/B-H, dollar totals for I and J — exactly
    the issue's two rules, and nothing for the forms it does not name
    (B101, B106Sum, the declaration, B107, B122A-1/2): those rows carry no
    metric, only problems.

    Every count is of the entities THAT FORM prints, read straight off
    `CaseData`/`CaseFile` — `claims_of` is the same class filter 106D and
    106E/F project through, so "how many claims are secured" cannot drift
    from "which claims 106D prints". The two dollar totals come from
    `case_summary.summarise`, the same functions 106I line 12 and 106J line
    22c print from — never re-derived here.
    """
    totals = summarise(data).totals
    return {
        "form/b106ab": FormMetric("count", str(len(data.assets))),
        "form/b106c": FormMetric("count", str(len(data.exemptions))),
        "form/b106d": FormMetric("count", str(len(claims_of(case_file, "secured")))),
        "form/b106ef": FormMetric(
            "count",
            str(
                len(claims_of(case_file, "priority_unsecured", "nonpriority_unsecured"))
            ),
        ),
        "form/b106g": FormMetric("count", str(len(data.contract_leases))),
        "form/b106h": FormMetric(
            "count", str(len(data.codebtors) + len(data.community_household_members))
        ),
        "form/b106i": FormMetric("total", str(totals.monthly_income)),
        "form/b106j": FormMetric("total", str(totals.monthly_expenses)),
    }


def forms_hub(data: CaseData, *, as_of: date) -> tuple[FormSummary, ...]:
    """Every form this case's chapter and pin require, each with its metric
    and its own slice of the completeness list — `packet_form_series`'s own
    order, which is filing order.

    A release that fails to resolve (a series with no release effective on
    or before `as_of`, which `assemble` would also refuse on) still produces
    a row: title and official number fall back to the series id, and the
    resolution failure joins that form's problems, so one bad release cannot
    make the whole hub 500.
    """
    case_file = to_case_file(data)
    grouped = group_problems(data)
    metrics = _metrics(data, case_file)

    summaries: list[FormSummary] = []
    for series_id in packet_form_series(data):
        problems = grouped.get(series_id, ())
        try:
            release = resolve_case_form(data.case, series_id, as_of=as_of)
            title, official_number, form = (
                release.title,
                release.official_number,
                release.form,
            )
        except LookupError as error:
            title, official_number = "", ""
            form = series_id.removeprefix("form/")
            problems = (
                *problems,
                PacketProblem(
                    source=series_id, item_id=None, field="", message=str(error)
                ),
            )
        summaries.append(
            FormSummary(
                series=series_id,
                form=form,
                title=title,
                official_number=official_number,
                metric=metrics.get(series_id),
                problems=problems,
            )
        )
    return tuple(summaries)


def form_metric_json(metric: FormMetric) -> dict[str, str]:
    return {"kind": metric.kind, "value": metric.value}


def render_form_preview(
    data: CaseData, series_id: str, *, as_of: date
) -> bytes | tuple[PacketProblem, ...]:
    """Render exactly ONE form through the packet's own pipeline — the same
    structural gate, the same projection, the same fill engine `assemble`
    runs for the whole set — so a previewed form is either byte-identical to
    what the packet would produce, or refused with the reasons why. Never a
    partial render, the packet's own rule applied to one form.

    Returns the filled PDF's bytes, or every problem found (never both,
    matching `assemble`'s contract). The caller (the route) turns an unknown
    or unfiled-by-this-case `series_id` into a 404 by checking
    `packet_form_series` itself — this function assumes it is one of them.
    """
    problems = list(group_problems(data).get(series_id, ()))
    if problems:
        return tuple(problems)

    case_file = to_case_file(data)
    try:
        release = resolve_case_form(data.case, series_id, as_of=as_of)
    except LookupError as error:
        return (
            PacketProblem(source=series_id, item_id=None, field="", message=str(error)),
        )

    try:
        values = project(release, case_file)
    except FormProjectionError as error:
        return tuple(
            PacketProblem(source=series_id, item_id=None, field="", message=message)
            for message in error.problems
        )

    try:
        return fill_form(release, values)
    except FormFillError as error:
        return tuple(
            PacketProblem(source=series_id, item_id=None, field="", message=message)
            for message in error.problems
        )


# ── Where a rendered preview's bytes live ────────────────────────────────
#
# NOT under cases/<case_id>/… like a document or a packet: those are records
# this service tracks (a Document row, a Packet row) and the bucket policy's
# "everything under cases/* is this case's own data" comment is written with
# that in mind. A preview has no row — it is rendered fresh on every request,
# thrown away by the caller the moment the PDF is shown, and nothing here
# would ever list it back. Giving it its own top-level prefix
# (form-previews/<case_id>/<preview_id>.pdf) is what lets ONE lifecycle rule
# (infra/modules/case_documents) reap every preview by a plain key prefix,
# without a tag and without touching the packet worker's narrower grant —
# case_id still opens the key (an operator reading the bucket can tell whose
# preview a stray object was), it is just not the FIRST segment.
_UUID_RE: Final = re.compile(
    r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\Z"
)


def new_preview_id() -> str:
    return str(uuid.uuid4())


def form_preview_object_key(case_id: str, preview_id: str) -> str:
    """Where one rendered preview's bytes live:
    form-previews/<case_id>/<preview_id>.pdf.

    Server-minted uuids only, structurally, same rule
    `packets.packet_object_key` and `documents.object_key` enforce — never a
    form name or anything a caller typed."""
    if not _UUID_RE.match(case_id) or not _UUID_RE.match(preview_id):
        raise ValidationError("object keys are built from server-minted uuids only")
    return f"form-previews/{case_id}/{preview_id}.pdf"
