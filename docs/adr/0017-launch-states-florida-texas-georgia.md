# ADR 0017 — Launch states: Florida, Texas, Georgia (provisional)

- **Status:** Proposed — **a provisional business call, made to unblock the
  dataset.** The forcing anchor the
  [regulatory source register](../business/regulatory-source-register.html)
  names — the design-partner firm's state — is not yet known; when it is, it
  joins (or replaces a member of) this set and this ADR is amended. Overturning
  the choice costs one state's dataset, not a redesign.
- **Date:** 2026-09-01
- **Relates to:** issue #95; the exemption entity and the
  statutory-constants-are-configuration rule in
  [`case-data-model.md`](../reference/case-data-model.md); instantiates the
  release model in [`effective-dating.md`](../reference/effective-dating.md)
  and [ADR 0014](0014-the-repository-is-the-regulatory-release-registry.md)
  for its first series.

## Decision

**Schedule C launches supporting the federal §522(d) scheme plus three states:
Florida, Texas, and Georgia.** The data lives as four series in the regulatory
release registry — `exemptions/federal`, `exemptions/fl`, `exemptions/tx`,
`exemptions/ga`, under `services/api/src/insolvia_api/regulatory/` — with
`services/api/src/insolvia_api/core/exemptions.py` as the loader, payload
schema, and resolution surface. Every figure carries a statute citation, a
verification tier, and the sources it was checked against. All other states
are staged, not promised.

## Context

The register calls 50-state exemptions the hidden iceberg — no central feed,
every state amends independently — and says to decide early which
states/districts launch supports. A wrong exemption figure lands on a signed
federal filing and can cost a client their house, so breadth trades directly
against verification depth. Three forces picked this set:

1. **Consumer filing volume.** The business plan's own source (AO CY2025:
   549,577 non-business filings) makes Florida, Texas, and Georgia three of
   the largest consumer-bankruptcy markets; Georgia is also perennially among
   the highest per-capita filing states. High volume is where a MyCase-channel
   design partner plausibly practices — `docs/business/` names no state, so
   volume is the best available proxy.
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

## Consequences

- The payload commits to a shape (scheme → cited, tiered entries; opt-out
  flag; wildcard-carryover links; federal caps like §522(p)/(q) kept separate
  from claimable exemptions) that Schedule C assembly and the analyzer build
  on; effective dating and supersession are release-level, per
  `effective-dating.md`, never edits to a merged release.
- Federal figures are pinned to the April 1, 2025 triennial adjustment
  (90 FR 8941); the next lands **April 1, 2028** and arrives as a new release
  of `exemptions/federal` — a scheduled maintenance event, per the register's
  calendar. Georgia's homestead starts annual inflation indexing 2031-07-01.
- Cases domiciled outside FL/TX/GA cannot have Schedule C assembled at launch;
  the product must say so rather than guess.
- When the design-partner state is known and differs, add it as dataset entry
  work under the same shape and amend this ADR.
