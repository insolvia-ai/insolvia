"""Projection: case data in, fill-engine values out — per form, per revision.

The forms are projections of facts (case-data-model.md): entities hold the
facts, and THIS package owns which fact lands on which line of which printed
revision, including the arithmetic the model refuses to store ("Derived
values are computed, never stored"). The fill engine (core/form_fill.py)
stays verbatim and structural; everything semantic — money and date
formatting, enum-to-export-state mapping, composing a person's name into a
one-line box, summing line 4 from lines 2 and 3 — happens here, where a
reviewer can read the whole mapping for a form in one place: one module per
form, `shared.py` for what they all use, and this registry deciding which
mapping a release gets.

Projections are REGISTERED PER RELEASE. A form revision bump is a template
change with its own mapping and its own goldens, never an in-place edit
(issue #93): when B101 revises, the new release gets a new projector entry
(usually delegating to the old one plus the delta), and a case pinned to the
old revision keeps projecting through the mapping it was prepared against.
`project` refuses a release nothing was written for — rendering a revision
through another revision's mapping is exactly the silent drift this registry
exists to prevent.

Three rules the mappings follow:

- **Absent facts leave blank boxes.** Intake is progressive; a None simply
  does not emit a fill. Whether a blank is ACCEPTABLE is the pre-filing
  completeness gate's question, not a projection error.
- **Present facts that cannot land are errors.** A third alias when the form
  prints two rows, an explanation longer than the printed lines — dropping
  either silently would put a signed form in front of a debtor with facts
  missing, so `FormProjectionError` names every one instead.
- **Enum values map by MEANING, spelled per revision.** The stored vocabulary
  is stable; each mapping owns the translation to that revision's exact
  export states — including B101's misspelled ones — and to option ORDER
  where the exports themselves are unreliable (lines 18-20's bands, where
  line 19's fourth-bracket export is missing a digit that line 20's has).

Blanks every mapping leaves deliberately, each until its owner lands: tax
identifiers (encrypted storage with audited reads is its own work), the
amended-filing caption (no `case.is_amended` yet), wet-signature lines
(never machine-filled), the court's case number, and pagination plus row
numbering and cross-schedule line references ("Schedule D, line __"), which
the specs assign to packet assembly (9.6) — the packet decides page and row
placement across continuation sheets, so the projection cannot know them.
"""

from __future__ import annotations

from collections.abc import Callable, Mapping
from typing import Final

from ..form_templates import FormRelease
from .b101 import project_b101_0624
from .b106ab import project_b106ab_1215
from .b106c import project_b106c_0425
from .b106i import project_b106i_1215
from .shared import (
    CaseFile,
    FieldValues,
    FormProjectionError,
    format_date,
    format_money,
    full_name,
    wrap_lines,
)

__all__ = [
    "PROJECTIONS",
    "CaseFile",
    "FieldValues",
    "FormProjectionError",
    "Projector",
    "format_date",
    "format_money",
    "full_name",
    "project",
    "wrap_lines",
]

Projector = Callable[[FormRelease, CaseFile], FieldValues]

PROJECTIONS: Final[Mapping[tuple[str, str], Projector]] = {
    ("form/b101", "2024-06-22"): project_b101_0624,
    ("form/b106ab", "2015-12-01"): project_b106ab_1215,
    ("form/b106c", "2025-04-01"): project_b106c_0425,
    ("form/b106i", "2015-12-01"): project_b106i_1215,
}


def project(release: FormRelease, case_file: CaseFile) -> FieldValues:
    """The values for one form release, from one case's facts.

    Raises KeyError when no mapping exists for the release — a new revision
    must bring its own mapping and goldens before anything renders it."""
    projector = PROJECTIONS.get((release.series_id, release.pin))
    if projector is None:
        raise KeyError(
            f"no projection is written for {release.release_id}; a revision "
            "bump is a template change with its own mapping and goldens"
        )
    return projector(release, case_file)
