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
  mappings. Every change must keep `python3 forms/scripts/check.py` green
  (`forms/scripts/dev-test.sh` wraps it): it enforces that every PDF widget is
  claimed exactly once and that radios list the PDF's exact export states.
- **The entity vocabulary mirrors
  [`docs/reference/case-data-model.md`](../docs/reference/case-data-model.md)**
  (the `ENTITIES` set in `scripts/check.py`). When the model gains or loses an
  entity, update both in the same PR. Attribute names beyond what that document
  spells out are this directory's proposals — keep them consistent across
  forms, and let the forms engine (9.3) and intake map (8.1) firm them up.
- **Derived values stay derived.** If a form line is arithmetic, a
  cross-schedule copy, or an existence question, map it `derived` — do not
  invent a stored attribute for it. case-data-model.md's "Derived values are
  computed, never stored" owns the reasoning.
- **A new form revision is a new dump + spec update together**, with
  `revision`/`effective_date` bumped in both — the checker refuses a mismatch.
  Which revision is *current* is the regulatory source register's fact; how
  revisions are versioned per case is the effective-date model's (9.1).
- `scripts/check.py` is stdlib-only on purpose — a fresh clone validates with
  bare `python3`, no venv. Keep it that way.
