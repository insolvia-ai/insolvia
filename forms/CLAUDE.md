# `forms/` — agent rules

Field specs for the official bankruptcy forms. Human docs — layout, spec shape,
revision table, the official PDFs' defects: [`README.md`](README.md). Read it
before editing anything here.

- **`acroform/*.json` is machine-extracted ground truth — never hand-edit it.**
  It describes the official PDF as published (SHA-256 included). If it looks
  wrong, the fix is re-extracting from the PDF, not editing the JSON. The
  quirks it captures (misnamed fields, shared widgets, broken button groups)
  are real properties of the court's PDFs.
- **`specs/*.json` is the curated layer** — labels, types, repetition, entity
  mappings. Every change must keep `forms/scripts/dev-test.sh` green — the
  spec tests in
  [`backend/tests/Insolvia.Forms.Tests/Specs`](../backend/tests/Insolvia.Forms.Tests/Specs),
  run filtered: they enforce that every PDF widget is claimed exactly once and
  that radios list the PDF's exact export states.
- **The entity vocabulary mirrors
  [`docs/reference/case-data-model.md`](../docs/reference/case-data-model.md)**
  (`FormSpecChecker.Entities` in that directory). When the model gains or loses an
  entity, update both in the same PR. Attribute names beyond what that document
  spells out are this directory's proposals — keep them consistent across
  forms, and let the forms engine (9.3) and intake map (8.1) firm them up.
- **Derived values stay derived.** If a form line is arithmetic, a
  cross-schedule copy, or an existence question, map it `derived` — do not
  invent a stored attribute for it. case-data-model.md's "Derived values are
  computed, never stored" owns the reasoning.
- **A flat form (no AcroForm — B2010, B2030) claims `overlay` boxes, never
  hand-added widgets.** The template stays the court's bytes verbatim; the
  engine draws onto an appended stream. Boxes are measured from the PDF's
  own operators and are allowed only where the dump has no fillable widget.
  [`README.md`](README.md) "Flat forms" owns the shape.
- **A new form revision is a new dump + spec update together**, with
  `revision`/`effective_date` bumped in both — the checker refuses a mismatch.
  Which revision is *current* is the regulatory source register's fact; how
  revisions are versioned per case is the effective-date model's (9.1).
- **A new check is a new test.** The checker (`FormSpecChecker.cs`) and one
  red case per check (`FormSpecCheckerTests.cs`) change together: a check with
  no case that breaks it can stop firing unnoticed. It needs only the .NET SDK
  `backend/global.json` pins — no database, no Docker.
