#!/usr/bin/env python3
"""Validate the form field specs in forms/specs/ against the AcroForm dumps in forms/acroform/.

Stdlib only, so a plain `python3 forms/scripts/check.py` works on a fresh clone.

What is enforced, and why:

1. Structure — every spec file has the required keys, every field has exactly
   one mapping, every enum value comes from a closed set. This is the schema;
   it lives here rather than in a JSON Schema file so there is one validator,
   not two that drift.
2. Coverage — every fillable AcroForm field in the official PDF is claimed by
   exactly one spec field, and every claim resolves. This is the executable
   version of the issue's "a spot-check against the published PDFs finds no
   missing fields": a field the spec forgot fails the build. Pushbuttons
   (print/reset/save/attach) are UI chrome, not data, and are exempt.
3. Consistency — a spec field's type must agree with the PDF widget kind it
   claims, and a radio's options must be exactly the states the PDF declares.
4. Vocabulary — `maps_to.entity` must name an entity from
   docs/reference/case-data-model.md. The list below mirrors that document's
   "Core entities" table; when the model gains or loses an entity, update both.
5. Flat forms — a Director's Form published with NO AcroForm (B2010, B2030)
   has nothing to claim, so its spec fields claim `overlay` boxes instead:
   page positions, measured from the PDF's own operators, that the fill
   engine draws text (or an X) onto. A box name is unique per form, its page
   exists, and a checkbox box carries a height. Overlay claims are allowed
   ONLY on a form whose dump has no fillable widget — a fillable form is
   filled through its widgets, never painted over — and a flat form with
   nothing to print at all (B2010 is a notice) may declare no fields.
"""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path

FORMS_DIR = Path(__file__).resolve().parent.parent
SPECS = FORMS_DIR / "specs"
ACROFORM = FORMS_DIR / "acroform"

# Mirrors docs/reference/case-data-model.md § Core entities (case-scoped types
# a form can draw from; document/extraction_candidate never feed a form).
ENTITIES = {
    "case",
    "petition",
    "debtor",
    "prior_case",
    "related_case",
    "sole_proprietorship",
    "filing_professional",
    "creditor",
    "claim",
    "asset",
    "exemption",
    "contract_lease",
    "codebtor",
    "community_household_member",
    "employment",
    "pay_period_record",
    "other_income_record",
    "means_test_input",
    "income_summary",
    "household",
    "expense",
    "dependent",
    "sofa_entry",
}

FIELD_TYPES = {
    # free-entry widgets (PDF kind: text or choice)
    "text",       # unconstrained text
    "money",      # fixed-scale decimal, 2 places (case-data-model value types)
    "integer",    # counts: number of years, percentages entered as whole numbers
    "date",       # calendar date, no time
    "year",       # four-digit year
    "state",      # two-letter state, usually a PDF choice widget
    "district",   # the bankruptcy-district choice widget in every caption
    "ssn_itin",   # social security / taxpayer id boxes
    "ein",        # employer id boxes
    "phone",
    "email",
    "signature",  # a signature line rendered as a text widget
    # button widgets (PDF kind: checkbox or radio)
    "checkbox",   # one on/off box
    "radio",      # one exclusive group; `options` lists its states
}

REQUIRED_SPEC_KEYS = {
    "form", "official_number", "title", "revision", "effective_date",
    "parts", "fields",
}
REQUIRED_FIELD_KEYS = {"id", "label", "type", "maps_to", "pdf"}
OPTIONAL_FIELD_KEYS = {"part", "line", "options", "repeats", "notes"}
MAPPING_KEYS = {"entity", "derived", "unmapped", "constant"}
# One overlay box: where on which page the engine draws a value (PDF user
# space, points, origin bottom-left; `y` is the baseline for text and the
# bottom edge for a checkbox). `h` is required for a checkbox and forbidden
# otherwise — text is drawn at a baseline, an X is drawn across a box.
OVERLAY_BOX_KEYS = {"name", "page", "x", "y", "w", "h"}

errors: list[str] = []


def err(form: str, msg: str) -> None:
    errors.append(f"{form}: {msg}")


def check_form(spec_path: Path) -> tuple[int, int, int]:
    """Returns (spec fields, covered pdf fields, entity-mapped spec fields)."""
    spec = json.loads(spec_path.read_text())
    form = spec.get("form", spec_path.stem)

    missing = REQUIRED_SPEC_KEYS - spec.keys()
    if missing:
        err(form, f"missing top-level keys: {sorted(missing)}")
        return (0, 0, 0)
    if form != spec_path.stem:
        err(form, f'"form" ({spec["form"]}) must match the file name ({spec_path.stem})')

    dump_path = ACROFORM / f"{form}.json"
    if not dump_path.exists():
        err(form, f"no AcroForm dump at {dump_path}")
        return (0, 0, 0)
    dump = json.loads(dump_path.read_text())

    if spec["revision"] != dump["revision"]:
        err(form, f'revision {spec["revision"]} != dump revision {dump["revision"]}')
    if spec["effective_date"] != dump["effective_date"]:
        err(form, "effective_date disagrees with the dump")

    pdf_fields = {f["name"]: f for f in dump["fields"] if f["kind"] != "pushbutton"}
    claimed: dict[str, str] = {}  # pdf field name -> spec field id
    overlay_boxes: dict[str, str] = {}  # overlay box name -> spec field id (flat forms)
    ids: set[str] = set()
    mapped = 0

    part_numbers = set()
    for part in spec["parts"]:
        if set(part.keys()) != {"number", "title"}:
            err(form, f"part must have exactly number+title: {part}")
            continue
        part_numbers.add(part["number"])

    for field in spec["fields"]:
        fid = field.get("id", "<missing id>")
        keys = set(field.keys())
        if not REQUIRED_FIELD_KEYS <= keys:
            err(form, f"{fid}: missing keys {sorted(REQUIRED_FIELD_KEYS - keys)}")
            continue
        extra = keys - REQUIRED_FIELD_KEYS - OPTIONAL_FIELD_KEYS
        if extra:
            err(form, f"{fid}: unknown keys {sorted(extra)}")
        if fid in ids:
            err(form, f"duplicate field id {fid}")
        ids.add(fid)
        if not re.fullmatch(r"[a-z0-9_.]+", fid):
            err(form, f"field id not lower_snake dotted: {fid}")

        ftype = field["type"]
        if ftype not in FIELD_TYPES:
            err(form, f"{fid}: unknown type {ftype}")
        if field.get("part") is not None and field["part"] not in part_numbers:
            err(form, f"{fid}: part {field['part']} not declared in parts")

        # mapping
        maps_to = field["maps_to"]
        mkeys = set(maps_to.keys()) & MAPPING_KEYS
        if len(mkeys) != 1:
            err(form, f"{fid}: maps_to needs exactly one of {sorted(MAPPING_KEYS)}")
        elif "entity" in mkeys:
            if maps_to["entity"] not in ENTITIES:
                err(form, f"{fid}: unknown entity {maps_to['entity']}")
            if not maps_to.get("attribute"):
                err(form, f"{fid}: entity mapping needs an attribute")
            mapped += 1
        else:
            if set(maps_to.keys()) != mkeys:
                err(form, f"{fid}: {sorted(mkeys)[0]} mapping takes no other keys")
            if not isinstance(maps_to[next(iter(mkeys))], str) or not maps_to[next(iter(mkeys))]:
                err(form, f"{fid}: maps_to.{next(iter(mkeys))} must be a non-empty reason/description")

        # resolve pdf claims: exact names and/or a regex over dump names —
        # or, on a flat form, overlay boxes (rule 5 above)
        pdf = field["pdf"]
        if not isinstance(pdf, dict) or not pdf or not (
            set(pdf.keys()) <= {"names", "pattern"} or set(pdf.keys()) == {"overlay"}
        ):
            err(form, f"{fid}: pdf must be {{names: [...]}} and/or {{pattern: ...}}, or {{overlay: [...]}}")
            continue
        if "overlay" in pdf:
            if pdf_fields:
                err(form, f"{fid}: overlay boxes are only for a form the PDF cannot fill; this one has widgets")
            if ftype == "radio":
                err(form, f"{fid}: a flat form has no export states — model the choice as checkbox boxes")
            boxes = pdf["overlay"]
            if not isinstance(boxes, list) or not boxes:
                err(form, f"{fid}: overlay must be a non-empty list of boxes")
                continue
            for box in boxes:
                if not isinstance(box, dict) or not {"name", "page", "x", "y", "w"} <= set(box.keys()):
                    err(form, f"{fid}: overlay box needs name, page, x, y, w: {box}")
                    continue
                if set(box.keys()) - OVERLAY_BOX_KEYS:
                    err(form, f"{fid}: overlay box has unknown keys: {sorted(set(box.keys()) - OVERLAY_BOX_KEYS)}")
                name = box["name"]
                if not isinstance(name, str) or not name:
                    err(form, f"{fid}: overlay box name must be a non-empty string")
                    continue
                if name in overlay_boxes:
                    err(form, f"overlay box {name!r} claimed by both {overlay_boxes[name]} and {fid}")
                overlay_boxes[name] = fid
                if not isinstance(box["page"], int) or not 1 <= box["page"] <= dump.get("pages", 0):
                    err(form, f"{fid}: overlay box {name!r} page {box['page']!r} is not a page of the PDF")
                for key in ("x", "y", "w", "h"):
                    if key in box and (isinstance(box[key], bool) or not isinstance(box[key], (int, float)) or box[key] < 0):
                        err(form, f"{fid}: overlay box {name!r} {key} must be a non-negative number")
                if (ftype == "checkbox") != ("h" in box):
                    err(form, f"{fid}: overlay box {name!r} carries h only when the field is a checkbox")
            if "options" in field:
                err(form, f"{fid}: options only belong on radio/checkbox widget fields")
            continue
        matched: list[str] = []
        for name in pdf.get("names", []):
            if name in pdf_fields:
                matched.append(name)
            else:
                err(form, f"{fid}: pdf field not in the official form: {name!r}")
        if "pattern" in pdf:
            rx = re.compile(pdf["pattern"])
            hits = [n for n in pdf_fields if rx.fullmatch(n)]
            if not hits:
                err(form, f"{fid}: pattern matched nothing: {pdf['pattern']!r}")
            matched.extend(hits)
        for name in matched:
            if name in claimed:
                err(form, f"pdf field {name!r} claimed by both {claimed[name]} and {fid}")
            claimed[name] = fid

        # type <-> widget kind agreement, and radio option/state agreement
        kinds = {pdf_fields[n]["kind"] for n in matched if n in pdf_fields}
        if ftype in ("checkbox", "radio"):
            if not kinds <= {"checkbox", "radio"}:
                err(form, f"{fid}: type {ftype} but claims non-button widgets ({sorted(kinds)})")
        else:
            if not kinds <= {"text", "choice", "signature"}:
                err(form, f"{fid}: type {ftype} but claims button widgets")
        if ftype == "radio":
            states: set[str] = set()
            for n in matched:
                states.update(pdf_fields[n].get("states", []))
            opts = {o["value"] for o in field.get("options", [])}
            if opts != states:
                err(form, f"{fid}: options {sorted(opts)} != PDF states {sorted(states)}")
        elif ftype == "checkbox":
            for n in matched:
                st = pdf_fields[n].get("states", [])
                if len(st) > 1:
                    err(form, f"{fid}: {n!r} has states {st}; model it as a radio")
        if "options" in field:
            if ftype not in ("radio", "checkbox"):
                err(form, f"{fid}: options only belong on radio/checkbox fields")
            for o in field["options"]:
                if set(o.keys()) - {"value", "label", "maps_to_value", "notes"}:
                    err(form, f"{fid}: option has unknown keys: {o}")

    uncovered = sorted(set(pdf_fields) - set(claimed))
    if uncovered:
        err(form, f"{len(uncovered)} official PDF fields not covered by any spec field: {uncovered[:8]}{' …' if len(uncovered) > 8 else ''}")
    if not pdf_fields and not overlay_boxes and spec["fields"]:
        err(form, "a flat form's fields must claim overlay boxes")

    return (len(spec["fields"]), len(claimed) + len(overlay_boxes), mapped)


def main() -> int:
    spec_paths = sorted(SPECS.glob("*.json"))
    dump_stems = {p.stem for p in ACROFORM.glob("*.json")}
    spec_stems = {p.stem for p in spec_paths}
    for stem in sorted(dump_stems - spec_stems):
        errors.append(f"{stem}: AcroForm dump has no spec in forms/specs/")

    total = (0, 0, 0)
    for p in spec_paths:
        counts = check_form(p)
        total = tuple(a + b for a, b in zip(total, counts))
        print(f"  {p.stem}: {counts[0]} spec fields covering {counts[1]} PDF fields ({counts[2]} entity-mapped)")

    print(f"TOTAL: {total[0]} spec fields covering {total[1]} PDF fields across {len(spec_paths)} forms")
    if errors:
        print(f"\n{len(errors)} error(s):", file=sys.stderr)
        for e in errors:
            print(f"  {e}", file=sys.stderr)
        return 1
    print("OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
