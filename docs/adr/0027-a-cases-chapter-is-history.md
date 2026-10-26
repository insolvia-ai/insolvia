# ADR 0027 — A case's chapter is history, not an attribute

- **Status:** Accepted 2026-10-25 ([#488](https://github.com/insolvia-ai/insolvia/issues/488)),
  decided with the maintainer in that ticket's grilling. Not built: the
  build breakdown below is the work. **Lands before
  [first real data](../../GLOSSARY.md)**: it changes a key, which after
  that moment is a migration rather than a reseed (the test in
  [#476](https://github.com/insolvia-ai/insolvia/issues/476)).
- **Date:** 2026-10-25
- **Amends:** [ADR 0022](0022-a-client-is-not-a-case.md) (its lifecycle's
  history, and what Copy case is for) and
  [ADR 0024](0024-electronic-filing-path.md) (the filing worker's history
  row). Neither is superseded; see **What this amends** below.
- **Relates to:** the [case, conversion, refiling, confirmation, petition
  chapter and current chapter](../../GLOSSARY.md) terms;
  [`case-data-model.md`](../reference/case-data-model.md) § The lifecycle,
  which owns the details once built; the inputs
  [`chapter-11-13-form-gaps-2026-10-21.md`](../research/chapter-11-13-form-gaps-2026-10-21.md)
  (§3.2 *Lifecycle*, §4.2 F) and
  [`single-tenancy-assumptions-2026-10-21.md`](../research/single-tenancy-assumptions-2026-10-21.md)
  (§2.1); [ADR 0013](0013-mcp-server-replaces-direct-pms-integration.md)'s
  confirm-before-entry invariant. Unblocks
  [#490](https://github.com/insolvia-ai/insolvia/issues/490) (Chapter 13
  plan versions), [#493](https://github.com/insolvia-ai/insolvia/issues/493)
  (Chapter 11 built) and [#494](https://github.com/insolvia-ai/insolvia/issues/494)
  (Chapter 13 after filing).

## Decision

**One Insolvia case is one court case. A conversion changes its chapter and
is recorded; it never opens a new case.** The case number and the petition
date stay. A new case for the same client is a **refiling** (after a
dismissal, with its own number) and is made with Copy case, as today.

### The chapter before and after filing

- **Before filing**, the chapter is a preparation choice: editable like any
  other case attribute, with no history row. A preparer who
  switches 7 → 13 after the means test has converted nothing.
- **After filing**, the chapter changes only through a **conversion**, a
  dated court event. A `chapter` in a filed case's PATCH body is a 409.
- **A conversion is allowed only while the status is `filed`** — not from
  `discharged` or `dismissed`, and not from `closed` until a reopening has
  moved it back to `filed`. Repeat conversions (7 → 13 → 7) are allowed.
- **Insolvia records what the court did and enforces no eligibility rule**
  (§ 706(a)'s one-time right, § 1307's conditions, § 109(e)'s limits): by the
  time we learn of a conversion the court has decided it. The only checks are
  that the chapter changes and that the date is no earlier than the petition
  date or the case's previous event.

### One lifecycle history, of events

The case's history is **one ordered series of events** in its own partition,
replacing today's `STATUS#` rows:

```
PK  CASE#<case_id>
SK  LIFECYCLE#<recordedAt>
kind            status | conversion | confirmation | confirmation_denied
effectiveOn     the court's date, a plain date (never converted to UTC)
recordedAt      when Insolvia recorded it (RFC 3339, UTC)
recordedBy      the subject that recorded it
provenance      manual | court_notice (+ the candidate it was accepted from) | filing-worker
-- by kind --
status          fromStatus, toStatus, chapter (on the filed event: the petition chapter), filingId?
conversion      fromChapter, toChapter
confirmation*   (plan version: added by #490)
```

- **The case item keeps `status` and `chapter` as the current values**,
  written in the same transaction as the event and conditioned on the values
  read, exactly as status moves are today. Readers of the case are unchanged.
- **Periods are derived, never stored.** "Chapter 13 from X to Y" is read
  from the events; a stored period would have to be rewritten when it ends.
- **Dismissal and discharge stay statuses.** Moving to them is a `status`
  event in the same history. **Confirmation is not a status** — a confirmed
  case can still be dismissed, converted or discharged, and a denial is
  usually followed by an amended plan, not an outcome. It is a dated event,
  and "is this case confirmed" is read from the latest confirmation event
  *within the current chapter*.
- **Two dates on every event.** `effectiveOn` is the court's: for a
  conversion, the order's date, or for a debtor's conversion as of right the
  date the notice was filed. `recordedAt`/`recordedBy` are ours. `filed_at`
  stays the petition date on a conversion (§ 348(a)).
- **One writer module.** Every event — a status move, the filing worker's
  `filed`, a conversion, a confirmation — is written through one function in
  `insolvia_core.cases` that owns the ordering, the date checks and the
  same-transaction rule. Its interface is small (record an event against a
  case read at a version); the history's shape lives behind it.

### Petition chapter and current chapter

- **The petition chapter is the chapter the case was filed under.** It is on
  the `filed` event and on the filing record ADR 0024 already keeps, and it
  never changes. The petition packet belongs to it: a filed case never
  re-assembles, so the packet gate and the forms read the petition chapter
  for that packet, never `case.chapter`.
- **The current chapter is `case.chapter`.** Before filing it is the chapter
  being prepared, and every reader (gate, forms, means test, plan) reads it.
  After a conversion, the deadline rules, the case's views and any filing
  made after the conversion read it.
- **The conversion date is a deadline anchor.** The deadlines Rule 1019
  resets on conversion count from `effectiveOn`, not from `filed_at`.
- **The paperwork a conversion triggers** (Rule 1019, Rule 1007(c): the
  schedule of post-petition debts, a new means-test statement) is new work
  under the new chapter. It is not decided here; it is the research ticket
  *What must Insolvia prepare when a case converts?*.

### Chapters 11 and 12 are refused until built

- **One list of built chapters, `(7, 13)`, beside `CHAPTERS`.** `CHAPTERS`
  stays the list of chapters that exist, which the deadline rules and the
  registry validate against. `firms._DEFAULT_CHAPTERS`, a second owner of the
  same list, is removed.
- **Refused with a 422 and a message** ("Chapter 11 cases can't be prepared
  in Insolvia yet") at case creation, at a pre-filing chapter edit, on Copy
  case into 11 or 12, and for a firm's `default_chapter`.
- **A conversion into 11 or 12 is recorded, not refused.** It is a court
  fact, and refusing it would make our record of a real case wrong. The case
  keeps its history and its current chapter, and nothing chapter-specific can
  be prepared for it: no packet, no Chapter 11 shapes.
- #493 lifts the refusal for Chapter 11. **Chapter 12 stays refused** with
  no ticket; the research put it out of scope.

### Who writes an event

- **Staff**, on the web or through an MCP harness, with the same permission
  a status move needs today, write the event directly, with provenance
  `manual`. An MCP agent that *proposes* a conversion goes through
  confirm-before-entry like any agent write.
- **A parsed court notice** ([#369](https://github.com/insolvia-ai/insolvia/issues/369))
  is extracted data. It arrives as a candidate in the existing review queue
  and becomes an event only when a person accepts it, with provenance naming
  the notice. It is never auto-applied.
- **The filing worker's direct `filed` write stays the one exception**, as
  ADR 0024 decided: it is Insolvia's own act, with the court's receipt behind
  it.

### The wire

- `GET /v1/cases/<id>/lifecycle` returns every event, oldest first. It
  replaces `GET /v1/cases/<id>/status-history`; no outside reader exists to
  keep compatible.
- `POST /v1/cases/<id>/conversions` takes `{toChapter, effectiveOn}`.
- `POST /v1/cases/<id>/confirmations` takes `{outcome: confirmed | denied,
  effectiveOn}`; on a case whose current chapter is not 13 it is a 409.
  #490 adds the plan version it ruled on.
- Status moves stay on the case PATCH.
- MCP gets the same three operations, under confirm-before-entry.
- **The hardship discharge** (3180WH) becomes an attribute of the
  `discharged` event; it is #494's to decide.

## Context

`case.chapter` is a single mutable integer (`cases.py`) and a PATCH
overwrites it. Once a real case converts — 13 → 7 is common — the only way to
recover what chapter it was in, and when, is a person reading the docket.
Chapter 13 confirmation, which binds the plan (Rule 3015(g)) and which every
modification is filed against, has nowhere to live. And `CHAPTERS =
(7, 11, 12, 13)` lets a Chapter 11 or 12 case be opened today: its business
facts would land in Chapter 7 shapes that no script could later turn into the
operating-business, plan-class and monthly-report records Chapter 11 needs.
Each of these fails #476's test — a later migration could not be a
deterministic script over what is stored — so this lands before first real
data.

The working understanding of the court's side, which the research ticket
checks against the rules: a converted case keeps its number and its
petition date (§ 348(a)); schedules filed before conversion count as filed
in the new chapter (Rule 1019(1)(A)); a debtor's 13 → 7 conversion under
§ 1307(a) takes effect on the notice's filing; a conversion resets some
deadlines (Rule 1019).

## Weighed and rejected

- **A conversion opens a new, linked case** (the ticket's leading option),
  keeping `case.chapter` fixed per case and carrying the data across as Copy
  case does. Rejected because the court does none of that: there is one case
  number, one petition date and one docket. Two Insolvia cases would share
  one `caseNumberKey`, so every court notice (#369) would match both, and the
  pre-conversion schedules — which the court treats as filed in the new
  chapter — would be split from the case that now needs them. It is what a
  tool with no chapter history has to do; a history makes it unnecessary.
- **Separate streams** (`STATUS#`, `CHAPTER#`, `CONFIRMATION#`). Rejected:
  "what happened to this case, in order" would need a merge of three queries,
  and the ordering and same-transaction rules would have three owners.
- **Stored chapter periods** (a row with from and to dates). Rejected: a
  period is derivable from events, and storing it means rewriting the open
  period on every conversion.
- **`confirmed` as a status** between `filed` and `discharged`. Rejected:
  confirmation is not where the case is in the court but a ruling within
  `filed`, after which dismissal and conversion are still possible; a denial
  has no status at all; and a Chapter 7 case would carry a status it can
  never reach.
- **Refusing a conversion into 11 or 12.** Rejected: a refusal does not stop
  the court; it only makes Insolvia's record of the case false.
- **Encoding conversion eligibility.** Rejected: the court rules before we
  record, and a rule we encode wrongly would refuse a true fact.

## What this amends

- **ADR 0022.** The lifecycle's status map stands. Its history becomes the
  lifecycle history of events above, and Copy case is the tool for a
  **refiling**, never for a conversion.
- **ADR 0024.** The filing worker's single transaction stands. The history
  row it writes becomes a `status` event in the lifecycle history and carries
  the petition chapter, so the history alone answers "which chapter, when"
  from the first filed event on.

## Consequences

- **The sort key changes** from `STATUS#` to `LIFECYCLE#`. Before first real
  data that is a reseed; this is why the ADR is on #476's gate.
- **Every lifecycle writer moves onto one module**: the case PATCH's status
  moves, the filing worker, the hand-back resolution, and the two new writes.
- **The deadline engine gains a second anchor** (the conversion date) beside
  `filed_at` and `meeting_341_at`.
- **"Is this case confirmed" is a read, not a field**: the latest
  confirmation event within the current chapter.
- **#490** links a confirmation event to the plan version it ruled on;
  **#493** lifts the Chapter 11 refusal and builds its shapes; **#494**
  decides the hardship discharge and uses the history for post-filing work.

## Build breakdown

| # | PR | Done when | Size |
|---|---|---|---|
| 1 | `core+api: built chapters — Chapters 11 and 12 refused until built` | one built-chapters list beside `CHAPTERS`; `_DEFAULT_CHAPTERS` removed; case creation, a pre-filing chapter edit, Copy case and a firm's `default_chapter` refuse 11 and 12 with a 422 and a message; unit tests over the memory adapter | S |
| 2 | `core+api+filing+seeds: the lifecycle history` | `LIFECYCLE#` events replace `STATUS#` rows through one writer in `insolvia_core.cases`; status moves, the filing worker and the hand-back resolution write through it; the `filed` event carries the petition chapter; `GET …/lifecycle` replaces `status-history`; a new seed fixture version | M |
| 3 | `api+mcp+app: conversion and confirmation` | `POST …/conversions` and `POST …/confirmations` with the checks above; a filed case's chapter PATCH is a 409; the MCP operations under confirm-before-entry; a court-notice candidate can be accepted as either event; a timeline on the case overview; Rule 1019's reset deadlines count from the conversion date | L |

**Three environments.** *Local*: the new fixture loads through
`scripts/dev-aws-seed.sh`, and every row is testable on the existing local
stack — nothing in it needs staging. *Staging*: reseeded by
`.github/actions/seed-staging`. *Prod*: holds no real data yet, so it is
wiped and reseeded, not backfilled, and the destructive step is confirmed
with the maintainer before it runs.
