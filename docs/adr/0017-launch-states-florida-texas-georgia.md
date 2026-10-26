# ADR 0017 — Launch set: Florida, Texas, Georgia (provisional)

- **Status:** Accepted 2026-10-25 (#471) — **provisional**, in the sense
  defined under [Provisional, and the revisit trigger](#provisional-and-the-revisit-trigger):
  the set changes only by a dated amendment below, when the trigger fires.
  Proposed 2026-09-01 as a Schedule C dataset choice; accepted with its scope
  widened to the launch set and its exemption half split off.
- **Date:** 2026-09-01 (proposed) · 2026-10-25 (accepted)
- **Relates to:** issues #95, #471; the
  [launch set and exemption coverage](../../GLOSSARY.md) terms; ADR 0024
  (filing, verified per court on the training databases); the exemption entity
  and the statutory-constants-are-configuration rule in
  [`case-data-model.md`](../reference/case-data-model.md); instantiates the
  release model in [`effective-dating.md`](../reference/effective-dating.md)
  and [ADR 0014](0014-the-repository-is-the-regulatory-release-registry.md)
  for its first series.

## Decision

**The launch set is Florida, Texas and Georgia** — all ten of their
bankruptcy courts. It names the courts whose filing drivers are verified on
their training databases (ADR 0024 PR 10) and where Insolvia says it files
first. It is a **promise, never a gate**: nothing in the model refuses a case,
a court or a chapter because it lies outside the set.

1. **The model holds for any district.** A case in any state opens and is
   prepared normally. A step refuses only when the data or the verification it
   needs is missing — Schedule C without a verified exemption series for that
   state, or filing in a court whose driver has not been verified — and it
   says so rather than guessing.
2. **The public claim is made court by court.** Only courts verified on their
   training database are named as places Insolvia files. Every other court in
   a launch state is *prepare and hand off, filing coming* — the fallback in
   #484 — until it verifies. A clerk refusing a developer registration, or a
   court with no training database, does not remove a state from the set.
3. **Exemption coverage is a separate decision.** The states whose exemption
   figures are kept checked against statute is decided apart from the launch
   set and may be wider (#486). At acceptance it is the federal §522(d) scheme
   plus FL, TX and GA: four series in the regulatory release registry —
   `exemptions/federal`, `exemptions/fl`, `exemptions/tx`, `exemptions/ga`,
   under `services/api/src/insolvia_api/regulatory/` — with
   `services/api/src/insolvia_api/core/exemptions.py` as the loader, payload
   schema, and resolution surface. Every figure carries a statute citation, a
   verification tier, and the sources it was checked against.
4. **S.D. Tex. as the suggested first court orders the work; it does not
   change the set.** Texas is the election state, so the `exemptions/tx` and
   `exemptions/federal` figures are verified first. Which court PR 10 verifies
   first is #478's decision.

### Provisional, and the revisit trigger

*Provisional* is a property of the set, not a status:

- The set changes only by a **dated amendment** to this ADR (see
  [Amendments](#amendments)), never by a superseding ADR, and only when the
  trigger below fires.
- A state that leaves the set keeps its data. Its series stay in the registry,
  staged as *not launched*.
- **Nothing waits for the trigger.** Work that depends on the launch set
  proceeds against the set as accepted.

**Trigger: a design-partner firm signs** — a written agreement to prepare and
file real cases with Insolvia; interest, a demo or a pilot conversation is not
one. If the firm practises outside the set, its state is **added**, not swapped
for a member, and its courts go to the front of the verification queue. If it
practises inside, at most the first court changes.

## Context

The register calls 50-state exemptions the hidden iceberg — no central feed,
every state amends independently — and says to decide early which
states/districts launch supports. A wrong exemption figure lands on a signed
federal filing and can cost a client their house, so breadth trades directly
against verification depth. Three forces picked this set:

1. **Consumer filing volume.** The business plan's own source (AO CY2025:
   549,577 non-business filings) makes Florida, Texas, and Georgia three of
   the largest consumer-bankruptcy markets; Georgia is also perennially among
   the highest per-capita filing states. High volume is where a design partner
   plausibly practises — `docs/business/` names no state, so volume is the
   best available proxy.
2. **Regime coverage.** The set exercises every branch the exemption analyzer
   and Schedule C assembly must handle, so adding state four is data entry,
   not new code paths:
   - **Florida** — opt-out (Fla. Stat. §222.20), *unlimited-value* homestead
     with acreage limits, and a conditional wildcard that exists only when the
     homestead is not claimed.
   - **Texas** — **not** opted out: the debtor elects federal §522(d) or the
     state scheme (106C line 1 has two live answers), unlimited-value
     homestead, and an *aggregate* personal-property cap that individual
     categories draw down.
   - **Georgia** — opt-out with fixed per-category amounts, a wildcard fed by
     unused homestead, and — since HB 1024 (eff. 2026-07-01) — a homestead
     figure that just changed, which makes it the live test of the
     effective-date model: the GA series begins at 2026-07-01, so resolution
     for an earlier filing date *refuses* (no fallback past the beginning)
     instead of serving figures the snapshot does not describe.
3. **California is deliberately deferred**, despite being the largest market:
   it is opt-out yet offers two mutually exclusive *state* systems (CCP
   §704 vs §703.140(b)), each with its own adjustment cycle. That is a
   scheme-shape the current model does not yet represent; take it as the first
   staged state once the launch set has proven the dataset shape.

**Why the launch set and exemption coverage were split (2026-10-25).** The
proposal waited on the design-partner firm's state, and there is no firm yet;
recruiting one is not on the engineering path. By acceptance two facts had
changed the shape of the question:

- **Filing breadth is bounded by court approvals, one court at a time.**
  Verifying a driver needs that court's clerk to approve a training-database
  registration (#470, #484); research effort cannot speed that up.
- **Exemption breadth is bounded by research effort**, which a compiled
  secondary source used as an index makes cheap — every shipped figure is
  still checked against statute.

Tying both to one list would have held exemptions to the pace of clerks. The
same audit (#468) found the set acting as a **gate**: the court registry and
the exemptions loader refused any other state, so a firm elsewhere could not
open a case at all — a one-district corner in a model meant to hold for many.

## Consequences

- **Build row — remove the launch-set gate** (sequenced with the spec order;
  found by #468). The court registry knows every district's identity, whether
  or not its opening facts are authored, so case creation no longer refuses a
  court outside FL/GA/TX (`insolvia_core` `courts.py`'s state table and
  `cases.py`'s unknown-court refusal). The exemptions loader refuses only for a
  missing series, not for a state outside `LAUNCH_STATES`
  (`exemptions.py`), and the IRS local-standards payload, today scoped to the
  launch states, covers every state (`ust_data.py`). What remains refuses at
  the step that needs the data — Schedule C, the district checklist, filing —
  with a message saying so.
- **Claims follow verification.** The website, the business plan (#479) and
  sales name courts, not states, and only verified ones.
- The payload commits to a shape (scheme → cited, tiered entries; opt-out
  flag; wildcard-carryover links; federal caps like §522(p)/(q) kept separate
  from claimable exemptions) that Schedule C assembly and the analyzer build
  on; effective dating and supersession are release-level, per
  `effective-dating.md`, never edits to a merged release.
- Federal figures are pinned to the April 1, 2025 triennial adjustment
  (90 FR 8941); the next lands **April 1, 2028** and arrives as a new release
  of `exemptions/federal` — a scheduled maintenance event, per the register's
  calendar. Georgia's homestead starts annual inflation indexing 2031-07-01.
- Cases domiciled in a state without a verified exemption series cannot have
  Schedule C assembled; the product says so rather than guessing. Which
  state's law governs is §522(b)(3)(A)'s 730-day domicile rule, not the
  debtor's current state (#486).
- When the trigger fires, a new state is dataset entry under the same shape,
  research and clerk registrations for its courts, and an amendment below.

## Amendments

*None yet.* Each entry is dated and names the trigger that fired and the
states added.
