# `forms/` — official-form field specs

The machine-readable field inventory of the Chapter 7 individual filing set —
B101, B106 Summary + Declaration, Schedules A/B–J (J-2 included), B107 — that
the forms engine ([issue 9.3](https://github.com/insolvia-ai/insolvia/issues/93))
fills from and the intake map (8.1) refines against. Produced for
[issue 9.2](https://github.com/insolvia-ai/insolvia/issues/92) by desk research
against uscourts.gov.

Two layers per form, deliberately:

| Directory | What | Where it comes from |
|---|---|---|
| `acroform/<form>.json` | Every AcroForm widget of the official fillable PDF — name, kind, checkbox/radio export states, pages — plus the source URL, the PDF's SHA-256, its printed revision and effective date | Extracted mechanically from the downloaded PDF (pypdf); never hand-edited |
| `specs/<form>.json` | The curated spec: each *logical* field with its label, type, options, repetition/continuation structure, and which case-data-model entity/attribute feeds it (or that it is derived/constant/unmapped) | Written by hand against the dump and the printed form |

`scripts/check.py` (stdlib-only; `scripts/dev-test.sh` wraps it) ties the two
together: every fillable widget in the dump must be claimed by exactly one spec
field, every claim must resolve, a spec field's type must agree with the widget
kind it claims, a radio's options must be exactly the PDF's export states, and
every entity mapping must name an entity from
[`docs/reference/case-data-model.md`](../docs/reference/case-data-model.md).
That makes the issue's "a spot-check against the published PDFs finds no
missing fields" an executable property, not a one-time review.

## Revisions in this set

| Form | Title | Revision | Effective |
|---|---|---|---|
| B101 | Voluntary Petition for Individuals | 06/24 | 2024-06-22 |
| B106Sum | Summary of Your Assets and Liabilities | 12/15 | 2015-12-01 |
| B106Dec | Declaration About an Individual Debtor's Schedules | 12/15 | 2015-12-01 |
| B106A/B | Schedule A/B: Property | 12/15 | 2015-12-01 |
| B106C | Schedule C: The Property You Claim as Exempt | **04/25** | 2025-04-01 |
| B106D | Schedule D: Secured Claims | 12/15 | 2015-12-01 |
| B106E/F | Schedule E/F: Unsecured Claims | 12/15 | 2015-12-01 |
| B106G | Schedule G: Executory Contracts and Unexpired Leases | 12/15 | 2015-12-01 |
| B106H | Schedule H: Your Codebtors | 12/15 | 2015-12-01 |
| B106I | Schedule I: Your Income | 12/15 | 2015-12-01 |
| B106J | Schedule J: Your Expenses | 12/15 | 2015-12-01 |
| B106J-2 | Schedule J-2: Expenses for Separate Household of Debtor 2 | 12/15 | 2015-12-01 |
| B107 | Statement of Financial Affairs | **04/25** | 2025-04-01 |
| B122A-1 | Chapter 7 Statement of Your Current Monthly Income | 12/19 | 2019-12-01 |
| B122A-2 | Chapter 7 Means Test Calculation | **04/25** | 2025-04-01 |

B106C and B107 carry the April 2025 dollar-amount adjustments (§ 104 three-year
cycle: the $214,000 homestead question, B107's $8,575 payment floor — next
adjustment 4/01/28). The
[regulatory source register](../docs/business/regulatory-source-register.html)
owns which revision is current and when it is checked; the effective-date model
(issue 9.1) owns how revisions are versioned — each spec records its own
`revision` and `effective_date` so that model can adopt these files as its
first form-version data.

## The spec shape

```jsonc
{
  "form": "b106i",              // file name, and the key into acroform/
  "official_number": "106I",
  "revision": "12/15",          // must match the dump
  "effective_date": "2015-12-01",
  "parts": [ {"number": 1, "title": "…"} ],
  "fields": [
    {
      "id": "line_2_gross_wages",         // unique per form, lower_snake dotted
      "part": 2, "line": "2",             // the printed part/line ("part": null = caption)
      "label": "…the printed caption…",
      "type": "money",                    // see FIELD_TYPES in scripts/check.py
      "options": [ {"value": "…"} ],      // radio: exactly the PDF export states
      "repeats": {"group": "…", "count": 2, "continuation": "…"},
      "maps_to": { … },                   // exactly one of the four mappings below
      "pdf": {"names": […]} | {"pattern": "…"},   // the widgets this field claims
      "notes": "…"
    }
  ]
}
```

`maps_to` is exactly one of:

- `{"entity", "attribute", "notes"?}` — a stored fact;
  the entity vocabulary mirrors case-data-model.md's core-entities table.
- `{"derived": "…"}` — arithmetic, cross-schedule copies, row/page numbering,
  or existence questions ("Do you have any…?") answered by whether records
  exist. Never stored, per case-data-model.md.
- `{"constant": "…"}` — an answer that depends on an effective-dated statutory
  figure (the § 522(q) homestead cap, B107's payment floor).
- `{"unmapped": "why"}` — a printed field with no model attribute: the
  court-assigned case number, wet-signature lines, pro se acknowledgments.

## Quirks of the official PDFs, so nobody rediscovers them

The 12/15-era PDFs are hand-built and carry real defects; the specs record each
one in a `notes` where it bites. The recurring kinds:

- **Meaningless names** — amount columns named `undefined_37`; the specs note
  the widget x-position that disambiguates each column.
- **Misnamed fields** — B106D's collateral-description box is named after the
  neighbouring caption; B107's storage-facility name box is called
  `Name of financial institution 22a`.
- **Shared fields across distinct boxes** — one field with widgets in two
  places that should be independent (B106D row 2.1's account number reappears
  in a Part 2 row; B106A/B line 24's rows one and three; B107's `ZIP Code 20a`
  / `ZIP Code 21a` / `Date1 18b` / `Date to 27b`). Filling one always fills
  the other.
- **Broken button groups** — B106D row 2.4's who-owes options all export `On`
  (checking one checks all four); B107 question 26's yes/no gate and its
  pending/appeal/concluded status are one exclusive radio group. The engine
  must set widget appearance states directly for these.
- **Inconsistent export values** — the same choice exports `Debtor 1 and 2`,
  `debtor 1 and 2`, `Dentor 2` (sic), or `On` depending on the row. Radios list
  the exact per-PDF values, with `maps_to_value` giving the canonical enum.
- **Boxes with no widget at all** — a printed box the PDF simply cannot fill.
  B122A-1's line 12a copy box; B122A-2's line 39b and most of its right-margin
  "copy here" boxes (lines 3, 25, 33e, 34, 36, 38, 39c, 39d, 41b). The shared
  widgets that DO exist there (line 4 = 39a, 9b's total = 33a, 13b/13e's
  totals = 33b/33c) are the same defect kind as B107's, used constructively:
  one value, two printed boxes, always agreeing.

## Updating for a new revision

1. Download the new official PDF from the form's uscourts.gov page and
   regenerate its `acroform/<form>.json` (widget walk + sha256 + source block —
   the extraction is ~100 lines of pypdf; the dump format is self-describing).
2. Update `specs/<form>.json` for whatever the revision changed, and bump its
   `revision`/`effective_date` to match.
3. `forms/scripts/dev-test.sh` — coverage errors are the diff between the old
   spec and the new PDF.

How versions coexist (a case pins the revision it was prepared against) is the
effective-date model's concern (9.1), not this directory's; today it holds
exactly one revision per form, the current one.
