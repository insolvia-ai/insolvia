# ADR 0013 — An MCP server replaces direct practice-management integration

- **Status:** Accepted
- **Date:** 2026-09-01
- **Relates to:** retires the MyCase spike (Foundation · Milestone 0, issues
  0.0–0.8); amends the sync seam in
  [`case-data-model.md`](../reference/case-data-model.md); leaves
  [ADR 0001](0001-client-stays-dumb-trust-boundary.md)'s trust boundary and
  [ADR 0009](0009-a-case-belongs-to-a-firm.md)'s tenancy model untouched — and
  leans on both.

## Decision

**Insolvia does not integrate with any practice-management system directly.
Instead, Insolvia exposes its bankruptcy case-management capability as an MCP
server** (Model Context Protocol, remote), so that an AI harness the attorney
already runs — Claude Desktop, ChatGPT, or any other MCP client — is the thing
that moves data between their practice-management system and Insolvia.

Concretely:

- The part of the original plan that read from and wrote to MyCase — auth
  against their API, polling or webhooks, field mapping, sync state — is now
  **the harness's job**, done through whatever connector the PMS vendor or the
  harness ecosystem provides. We build none of it.
- What we build instead is **one MCP surface over our own domain**: cases,
  debtors, creditors, documents, intake, petition status — the same capability
  the app exposes to humans, exposed to agents, behind the same server-side
  trust boundary and the same firm/permission model.
- MyCase stops being an architectural dependency and becomes one PMS among
  many that a harness can read. The product positioning follows: Insolvia is
  the bankruptcy case-prep engine any AI-equipped firm can connect, not a
  MyCase add-on.

## Context

The original wedge (business plan §1/§7) was "MyCase-native, no double entry",
resting on Milestone 0 — a spike to establish whether MyCase's API had the
write coverage the promise needed. That spike carried the plan's #1 risk
(write-thin APIs are common) and #2 risk (the access channel was a personal
relationship). Both risks existed because *we* were the integration.

Since that plan was written, the surrounding reality changed: firms' AI
harnesses can hold connections to line-of-business systems themselves, and MCP
is the emerging standard those harnesses share. An attorney whose harness can
already read their PMS does not need us to hold MyCase credentials — they need
the *bankruptcy* capability to be reachable by the same harness. That inverts
the integration: instead of Insolvia integrating with N practice-management
systems, N harnesses integrate with one Insolvia MCP server.

The no-double-entry promise survives, re-routed: data still flows from the
practice into the petition without retyping — the harness carries it, and our
confirm-before-entry invariant (the provenance model in
[`case-data-model.md`](../reference/case-data-model.md)) governs what an agent
may write exactly as it governs what extraction may write: **agent-written
data lands as candidate records and becomes case data only on human
confirmation.** The trust boundary of ADR 0001 is unchanged — the MCP server
is a server-side surface over the same stores, and no client (human app or AI
harness) ever touches the data stores directly.

## Consequences

- **Milestone 0 (MyCase spike) is closed unexecuted.** Write coverage, rate
  limits, webhooks, App Bar listing, the credentialed round-trips — all were
  questions about being the integration, and we no longer are. The issues are
  closed with pointers here, not deleted.
- **A new milestone owns the MCP server**: surface design, auth for MCP
  clients, the service itself, verification against real harnesses, and
  distribution (MCP directories/connector listings replace the App Bar as the
  discovery channel).
- **The sync seam narrows to an origin pointer.** `sync_state`
  (push/pull/hash bookkeeping) is deleted from the data-model spec — nothing
  built it, and there is no sync engine to need it. `external_refs` stays:
  a record a harness sourced from a PMS should say so, as provenance.
- **Auth follows the standards seam we already have.** MCP remote servers
  authenticate clients with OAuth; our Cognito pool is an OAuth provider, and
  ADR 0009 already keeps authorization out of tokens and in our store, so an
  MCP session is a `sub` with firm permissions like any other caller. The
  details (dynamic client registration, scopes, token lifetime for agents) are
  the new milestone's first design issue, not this ADR.
- **The forms & petition engine milestone is untouched.** Forms fill from
  confirmed case data; the pivot only changes one of the ways data arrives.
  Likewise means test and AI extraction — though extraction's scope may shrink
  if harnesses prove good at reading documents, that is a call for when 8.7 is
  taken up, not now.
- **Positioning debt.** The marketing site, waitlist copy and business plan
  (§1, §7, §10, §11) still say "MyCase-native". They are wrong until rewritten;
  tracked as their own issues rather than smuggled into this decision.
- **What we give up:** the warm MyCase relationship no longer shortcuts an
  integration (it may still open doors as a channel); "native to your
  practice" becomes conditional on the firm actually running an AI harness —
  which narrows the early market to AI-adopting firms and adds a new risk
  (harness capability and attorney adoption) in place of the write-coverage
  risk it retires.

## What would reopen this

A design-partner firm that will not run an AI harness but would buy a direct
integration; or MCP support in the major harnesses stalling or fragmenting so
that "any harness" stops being one build. Either is a market fact, cheap to
observe, and this ADR should be superseded — not silently contradicted — if
one arrives.
