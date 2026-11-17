"""B2010 @ 2020-12-01 (revision 12/20) — the § 342(b) notice.

Nothing projects. The Director's Form is a four-page NOTICE the attorney
delivers to the debtor, published flat (no AcroForm) and carrying no
caption, certification or signature block: the debtor's acknowledgement of
having received it and the attorney's certification of having delivered it
are both printed declarations in B101 Part 7, which the B101 mapping
already dates. The packet files the court's own bytes verbatim — the fill
engine ships a flat release with nothing to draw untouched — so the
mapping is empty by design, and stays registered so the release cannot
render through another revision's mapping should the notice ever gain a
field (issue #351).
"""

from __future__ import annotations

from ..form_templates import FormRelease
from .shared import CaseFile, FieldValues


def project_b2010_1220(release: FormRelease, case_file: CaseFile) -> FieldValues:
    del release, case_file  # the notice has no field to project onto
    return {}
