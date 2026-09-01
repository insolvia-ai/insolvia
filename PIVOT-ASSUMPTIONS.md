# MCP-pivot assumptions — review artifact for this PR

Every judgment call made while turning "we build an MCP, not a MyCase
integration" into the ADR, the plan rewrite, and the issue changes. Each one
is reversible; reject any and I'll unwind it. **This file is for the PR review
and should not outlive it** — a kept decision belongs in ADR 0013 or the plan;
delete the file at merge.

## Assumptions baked into the documents

1. **Agent writes land as candidate records, confirmed by a human.** I
   extended the confirm-before-entry invariant to MCP clients: a harness can
   never write confirmed case data directly. This is the strongest assumption
   I made — it shapes ADR 0013, #260, and #263. Alternative you might have
   meant: the attorney drives the harness, so harness writes *are*
   attorney-confirmed and could land directly. I chose the conservative
   reading because provenance ("worse than no value" — case-data-model.md)
   and GLBA posture both favour it.
2. **The pivot got a decision number, D11**, and an ADR (0013). The repo's own
   rules say a decision someone could re-litigate gets an ADR; this is the
   biggest strategy change since D9.
3. **Scope of "the harness does the reading": PMS data only.** I left the AI
   extraction milestone (8.7–8.9, credit reports and pay stubs) untouched —
   those documents aren't PMS data. ADR 0013 notes extraction *may* shrink if
   harnesses prove good at document reading, but decides nothing. If you
   intended extraction to move to the harness too, that milestone needs the
   same treatment the spike got.
4. **Forms & petition engine stays the current milestone.** The MCP milestone
   is sequenced beside it, not ahead of it: forms are the value the MCP
   exposes, and 12.2–12.5 want 9.9's entities (creditors, assets, income) to
   exist. Only 12.1 (design) and 12.7 (marketing is publicly wrong today) are
   flagged as worth doing early.
5. **Auth: OAuth against the existing Cognito pool; D10 unchanged.** An MCP
   session resolves to a `sub` with firm permissions from our store — no new
   principal class, no authorization in tokens. Details (DCR, scopes, token
   lifetime) deferred to #261, not decided.
6. **Service placement is deliberately undecided** (`services/mcp` vs. inside
   `services/api`) — left to #260 rather than assumed here.
7. **`sync_state` deleted from the data-model spec; `external_refs` kept** as
   an origin/provenance pointer. Verified nothing in code implements either —
   they were spec-only.
8. **The MyCase relationship was demoted, not discarded**: closed as an
   integration prerequisite (0.7), carried forward as a channel question
   (12.6/12.8).

## Assumptions about the GitHub reshaping

9. **Closed, never deleted**: 0.0–0.8 (#29, #1–#8) closed with comments
   pointing at ADR 0013 and the successor milestone; the old milestone
   "Foundation · MyCase spike" is closed. Reopenable in one click if you
   disagree with any individual closure.
10. **New milestone "Product · Case-management MCP" (milestone 10), issue
    prefix 12.x** (#260–#267), continuing the existing numbering convention
    (…10.x means test, 11.x firms).
11. **Marketing repositioning (#266) and the business-plan rewrite (#267) are
    issues, not changes in this PR.** Both are public/strategic artifacts —
    the site copy and the business plan are founder-signoff territory, so I
    filed the work instead of doing it. The site currently promises a direct
    MyCase integration; that's flagged as urgent in the plan.
12. **Left alone on purpose**: ADR 0009 / `insolvia_core.firms` /
    `access.py` / `cases.py` / the team screen — their MyCase mentions are
    borrowed *vocabulary* ("nothing here integrates with MyCase", their own
    words) and survive the pivot. Issue #90 (design partner) and the
    unmilestoned CI batch are also untouched.
13. **SES production access (#211) unaffected** — it was never MyCase-coupled.
