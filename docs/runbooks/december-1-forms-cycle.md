# The December 1 forms cycle

Every December 1, amendments to the Official Bankruptcy Forms (and the Federal
Rules of Bankruptcy Procedure) take effect. This runbook is the procedure a
maintainer executes each cycle: learn what changed, re-diff every supported
form, register the new revisions, release updated templates, and verify nothing
assembles on a superseded revision. The
[regulatory source register](../business/regulatory-source-register.html) owns
the cadence claim this operationalizes: *re-diff every form each cycle, and
treat each update as a release.*

> **Walked once, against the December 1, 2026 cycle, on 2026-09-01** — see the
> [cycle log](#cycle-log) at the bottom. Run again every cycle; the log is the
> record that you did.

Several steps act on systems that are still open issues. Those steps say so
inline and name the issue — execute the manual fallback until the issue lands,
then replace the fallback with the built thing.

## The calendar you are watching

There is **no push feed**. uscourts.gov announces amendments on web pages you
must poll. The cycle behind those pages (verified against
[Pending Rules and Forms Amendments](https://www.uscourts.gov/forms-rules/pending-rules-and-forms-amendments)
and the courts' public-comment notices, 2026-09-01):

- **Mid-August, year one** — proposed amendments are published for public
  comment; the window runs about six months, closing mid-February (the
  August 2025 draft closed 2026-02-16; the August 2026 draft closes
  2027-02-15).
- **September, year two** — the Judicial Conference approves. Its approval is
  final for Official Forms (they issue under FRBP 9009 — no statute is
  amended), so a standalone form change can take effect the **December 1
  immediately after** approval.
- **Year three, for form changes tied to rule amendments** — the paired rule
  goes to the Supreme Court (order to Congress in April; 2026's was April 8)
  and takes effect December 1 absent congressional action; the conforming form
  waits and takes effect the same day.
- **Off-cycle, any time** — interim and technical amendments (the six 410C13
  mortgage forms landed 2025-12-01 this way), Director's Forms (Advisory
  Committee approval only), and the § 104 dollar-amount adjustment every third
  April 1 (last 2025, next 2028), which rewrites printed figures on several
  forms **including 106C and 107**. The register owns the § 104 figures and
  dates; [`case-data-model.md`](../reference/case-data-model.md#statutory-constants-are-configuration)
  owns how cases record which constant set applied.

So one December 1 can carry: form amendments approved the previous September,
form amendments published for comment fifteen months earlier and tied to rules,
or nothing at all for our set. The only way to know is to look.

## When to run

Four checkpoints a year, calendar-driven:

1. **Mid-April** — the Supreme Court's order is out: the next December 1 is now
   known with near-certainty. In a § 104 year (2028, 2031, …) also pick up the
   April 1 dollar adjustments — those do not wait for December.
2. **Late September** — the Judicial Conference has met: standalone form
   approvals for *this* December 1, and early warning for the next.
3. **Late November** — the amended PDFs are posted. Execute steps 2–5 so
   templates flip on the effective date, not after it.
4. **First week of December** — execute step 6 (verify).

## The procedure

### 1 · Watch: establish what changes this December 1

Read, in order:

- [Pending or Recent Changes in the Bankruptcy Forms](https://www.uscourts.gov/forms-rules/pending-rules-and-forms-amendment/pending-or-recent-changes-bankruptcy-forms)
  — the authoritative list of form amendments, effective dates, and approval
  status.
- [Pending Rules and Forms Amendments](https://www.uscourts.gov/forms-rules/pending-rules-and-forms-amendments)
  — the pipeline by projected effective year. Note anything aimed at *future*
  Decembers too; that is next cycle's early warning and this section of the
  cycle log.
- [Bankruptcy Forms](https://www.uscourts.gov/forms-rules/forms/bankruptcy-forms)
  — the live inventory. The list page shows no dates; each form's own page
  states its effective date, and every Official Form PDF prints its revision
  date in the footer.

Write down: which forms change, effective when, and whether any of them is in
our supported set (the Chapter 7 individual set —
[#92](https://github.com/insolvia-ai/insolvia/issues/92) owns the list: B101,
B106A–J + Summary + Declaration, B107). A cycle that touches none of ours still
gets a cycle-log entry saying so — that is the walk.

### 2 · Fetch: download each amended form and pin its identity

For each supported form that changed: download the new PDF from its uscourts.gov
form page, and record the revision date from the PDF footer plus the effective
date from the form page. If the footer date does not match the announced
revision, stop — you have a stale or mirrored PDF; fetch from the form's own
page, not a search result.

> **Stubbed until [#92](https://github.com/insolvia-ai/insolvia/issues/92)
> lands:** where the official PDFs and field specs live in-repo is decided
> there. Until then, keep the downloaded PDFs with the cycle-log entry's PR.

### 3 · Re-diff: classify every change against the field specs

Compare the new PDF against the current field spec for that form and classify:

- **Cosmetic** — layout, wording, printed dollar figures (§ 104), instructions.
  No field added, removed, retyped, renumbered, or re-conditioned.
- **Field-level** — anything else. A field-level change ripples: the spec
  ([#92](https://github.com/insolvia-ai/insolvia/issues/92)), possibly the case
  schema ([`case-data-model.md`](../reference/case-data-model.md) — its B101/
  B106/B107 mapping is explicit about what it tracks), and the intake map.

> **Stubbed until [#92](https://github.com/insolvia-ai/insolvia/issues/92)
> lands:** the field specs are its output; the diff is against them. Until
> then, diff PDF-to-PDF (old revision vs new) and record the classification in
> the cycle log — it becomes input to #92 rather than output of this step.

### 4 · Register: give the revision an effective date, never an overwrite

Each amended form becomes a **new form-version record** with its effective
date, alongside — never replacing — the superseded revision. Cases keep
resolving against the revision in force on their assembly date.

> **Stubbed until [#91](https://github.com/insolvia-ai/insolvia/issues/91)
> lands:** the effective-date and versioning model is designed there, and this
> step becomes "add a release in that model". Until then, the cycle-log entry
> *is* the register: form, old revision, new revision, effective date.

### 5 · Release: new template, new goldens, version bump

Per the forms engine's rule, a revision bump is a **new template version with
its own golden files**, never an in-place edit. Build the template for the new
revision, add goldens diffed against the newly published official PDF, keep the
old template and goldens for cases pinned to the old revision, and ship it as a
release effective December 1 — not merged-and-live whenever CI happens to pass.

> **Stubbed until [#93](https://github.com/insolvia-ai/insolvia/issues/93)
> lands:** templates, goldens, and the CI gate are built there. Until then this
> step is a no-op beyond the cycle log — there is no template to update.

### 6 · Verify: nothing files on a superseded revision

On or just after December 1:

- Assemble a test packet locally for a case dated on/after the effective date;
  every output PDF's footer must show the **new** revision date.
- Assemble one for a case pinned before the effective date; it must still
  render the old revision (the register in step 4 is what makes this possible).
- Confirm the goldens for the new revision are green in CI and the old
  revision's goldens were not deleted.

> **Stubbed until [#93](https://github.com/insolvia-ai/insolvia/issues/93) and
> [#91](https://github.com/insolvia-ai/insolvia/issues/91) land:** assembly and
> pinning are theirs. Until then, verification is step 1 re-run: confirm no
> supported form changed without a cycle-log entry.

### 7 · Record: append to the cycle log

Append an entry below: date walked, what uscourts.gov announced, what changed
in our set, classifications, and what is visible in the pipeline for future
cycles. If the calendar itself moved (a new cadence, a new page location),
update the register — it owns the cadence facts.

## Done when

- The cycle-log entry for this December 1 exists, even if it says "no changes
  to our set".
- Every supported form that changed has: a classification (step 3), a
  registered revision with its effective date (step 4), and a released template
  with goldens (step 5) — or an explicit stub note naming the issue that
  blocks it.
- The step-6 checks pass, or their stub fallback is recorded in the log.

## Cycle log

### December 1, 2026 — walked 2026-09-01

- **Our supported set (B101, B106A–J, B107): no amendments take effect
  2026-12-01.** The cycle is rules-only: FRBP 1007, 2007.1, 3001, 3018, 5009,
  9006, 9014, 9017 amended and new Rule 7043 added (Supreme Court order
  2026-04-08). No Official Form in our set changes.
- **Pipeline warning for December 1, 2027:** amendments to **Official Forms 101
  and 106C** (with Rule 2002) — both in our supported set — were published for
  comment August 2025 (closed 2026-02-16) and are projected effective
  2027-12-01, pending Judicial Conference approval expected September 2026.
  Next cycle's walk starts here.
- **Pipeline, December 1, 2028:** preliminary draft published August 2026
  (comments close 2027-02-15) proposes amendments to Bankruptcy Rules 2003,
  5005, 8011, 9006, 9036, 9037 — no Official Forms in our set flagged so far.
- **Recent interim activity, for completeness:** six 410C13 mortgage forms and
  410S1 effective 2025-12-01; § 104 dollar adjustments effective April 2025
  touched seven Official Forms including 106C and 107 (figures owned by the
  register). All predate our first template, so nothing to register.
- Steps 2–6 were exercised as their stub fallbacks: nothing in our set changed,
  so there was nothing to fetch, diff, register, or release; verification
  reduces to this entry.

## Related

- [Regulatory source register](../business/regulatory-source-register.html) —
  the maintenance calendar and the authoritative source per form row.
- [`case-data-model.md`](../reference/case-data-model.md#statutory-constants-are-configuration)
  — how cases record which constant set and form mapping applied.
- [#91](https://github.com/insolvia-ai/insolvia/issues/91) effective-date
  model · [#92](https://github.com/insolvia-ai/insolvia/issues/92) field
  specs · [#93](https://github.com/insolvia-ai/insolvia/issues/93) forms
  engine — the three systems steps 2–6 will run on.
