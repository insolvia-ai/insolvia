# Insolvia — MVP Plan

Working document, kept **current**: it describes what is live, the decisions
in force, and the work ahead. Completed milestones are summarized in one table
— their full histories live in git, the PRs, and the ADRs, not here. Desktop
was dropped entirely (D9); this document no longer plans for it.

Status: **foundation shipped; intake shipped; forms & petition engine is the
current milestone** · Last pruned 2026-09-01

**Pivot 2026-09-01 (D11 /
[ADR 0013](adr/0013-mcp-server-replaces-direct-pms-integration.md)):** no
direct practice-management integration, ever — Insolvia exposes a remote **MCP
server** and the attorney's AI harness (Claude Desktop, ChatGPT, any MCP
client) moves data between their PMS and us. The MyCase spike (old
Milestone 0) is retired; its successor milestone is below.

---

## How this maps to the business plan

`docs/business/business-plan.html` §11 defines M0–M3 as *company* milestones:

| Business-plan milestone | Status |
|---|---|
| **M0 · Foundation repo** — design system, app shell, CI/CD, infra authored | ✅ Shipped |
| **M1 · Live staging + MyCase API spike** | Staging **live** (all four surfaces); the spike was **retired unexecuted** by the MCP pivot ([ADR 0013](adr/0013-mcp-server-replaces-direct-pms-integration.md)) — its successor is the [Case-management MCP milestone](https://github.com/insolvia-ai/insolvia/milestone/10) below |
| **M2 · MyCase-native intake + AI extraction** | Intake **shipped** (standalone, PMS-independent); AI extraction **deferred to its own milestone** 2026-08-11 (§ Product milestones) |
| **M3 · Compliant Chapter 7 packet** | **Current work** — taken up ahead of extraction (§ Product milestones) |

The business plan's §1/§7/§10/§11 still describe the pre-pivot MyCase wedge;
reconciling it is [#267](https://github.com/insolvia-ai/insolvia/issues/267)
(12.8). Until then, where they disagree, ADR 0013 and this plan win.

---

## What is live (the shipped foundation)

Everything below deploys to staging on merge to `main`; production ships when
the release run's `promote` gate is approved in the GitHub UI. One line each —
the linked owner has the detail:

| Surface / concern | What exists | Owner of the detail |
|---|---|---|
| Domain + DNS + TLS | `insolvia.ai` on Route53, shared `*.insolvia.ai` ACM cert | [`terraform.md`](reference/terraform.md) |
| Human email | Google Workspace inbound, SES outbound `no-reply@` | [`email.md`](reference/email.md) |
| Marketing site | React Router v7 SSR Lambda, `www.insolvia.ai` (+ noindexed staging), Lighthouse-gated in CI | [`apps/insolvia_marketing/CLAUDE.md`](../apps/insolvia_marketing/CLAUDE.md) |
| Web app | Expo / React Native SPA, `app.insolvia.ai`, S3 + CloudFront | [ADR 0004](adr/0004-react-native-replaces-flutter.md) · [`apps/insolvia_app/CLAUDE.md`](../apps/insolvia_app/CLAUDE.md) |
| Design system | **One cross-platform package** (`@insolvia-ai/design-system`) serving both surfaces — platform-split leaves, no third-party UI dependency. Lives in [`insolvia-ai/design-system`](https://github.com/insolvia-ai/design-system); consumed here by published version | [ADR 0006](adr/0006-owned-cross-platform-design-system.md) · [ADR 0010](adr/0010-design-system-moves-to-its-own-repository.md) |
| Tokens | One `tokens.json` → generated `theme.css` (web) + `tokens.ts` (native), drift-gated in CI — in the design-system repo. What is generated *here* is Cognito's sign-in branding, from the installed `@insolvia-ai/tokens` | [`reference/package-publishing.md`](reference/package-publishing.md) |
| API | Python/Flask on Lambda, `api.insolvia.ai` — health + waitlist; the client-stays-dumb trust boundary | [ADR 0001](adr/0001-client-stays-dumb-trust-boundary.md) · [`services/api/CLAUDE.md`](../services/api/CLAUDE.md) |
| API client | Hand-written TypeScript, contract-pinned by tests, wired into the app | [`packages/insolvia_api_client/CLAUDE.md`](../packages/insolvia_api_client/CLAUDE.md) |
| Mailer | SES-backed transactional service with unsubscribe path | [`services/mailer/CLAUDE.md`](../services/mailer/CLAUDE.md) |
| Auth | Cognito pools + hosted UI per env; the app signs in with authorization-code + PKCE, keeps access/ID tokens in memory and the refresh token in `localStorage`, and guards its routes | [ADR 0007](adr/0007-hosted-ui-pkce-refresh-token-in-local-storage.md) · `infra/modules/auth/main.tf` |
| CI/CD | Per-area PR gates (9 required checks), staging on merge, prod by dispatch, promote-not-rebuild | [`architecture.md`](reference/architecture.md) |

The subdomain map (D2) as deployed — flat `staging-*` naming, because one ACM
wildcard covers exactly one label:

| Surface | Production | Staging |
|---|---|---|
| Marketing | `www.insolvia.ai` (apex 301s here) | `staging-www.insolvia.ai` |
| Web app | `app.insolvia.ai` | `staging-app.insolvia.ai` |
| API | `api.insolvia.ai` | `staging-api.insolvia.ai` |
| Mailer | `mailer-api.insolvia.ai` | `staging-mailer-api.insolvia.ai` |

---

## Decisions in force

The one-line versions; each pointer owns the reasoning. Superseded decisions
(the Flutter design system, desktop targets, D8) are recorded in the ADRs they
died in — this plan no longer carries their bodies.

| # | Decision | Where the reasoning lives |
|---|---|---|
| D1 | Domain is **insolvia.ai** | settled; everything is built on it |
| D2 | Flat `staging-*` subdomains, every surface staged (marketing included) | table above; ACM wildcard covers one label |
| D3 | Marketing is **React Router v7 SSR** — Flutter web could not be crawled, and the replacement stack must stay dramatically lighter than any app runtime | `apps/insolvia_marketing/lighthouserc.json` (the enforced budget) |
| D4 | **One owned cross-platform design system**, platform-split leaves, over one token source | [ADR 0006](adr/0006-owned-cross-platform-design-system.md) |
| D5 | **The API is required for MVP** — GLBA-scope data (SSNs, financials) keeps the trust boundary server-side | [ADR 0001](adr/0001-client-stays-dumb-trust-boundary.md) |
| D6 | Backend is **Python + Flask + Mangum on Lambda** | house pattern, shipped |
| D7 | Human email and product email are **separate providers** (Workspace in, SES out) | [`email.md`](reference/email.md) |
| D10 | **A case belongs to a FIRM, not to a user.** Firm, role, admin flag, per-case linking and per-feature permissions live in our own store rather than in Cognito claims — an access token carries a `sub` and nothing else authorization-bearing | [ADR 0009](adr/0009-a-case-belongs-to-a-firm.md) |
| D9 | **React Native on Expo replaced Flutter**; Expo free tier only, CI-enforced; **desktop deleted**, mobile held open by `expo prebuild` | [ADR 0004](adr/0004-react-native-replaces-flutter.md) |
| D11 | **No direct PMS integration — Insolvia is an MCP server.** The attorney's AI harness moves data between their practice-management system and us; agent writes land as candidates under confirm-before-entry | [ADR 0013](adr/0013-mcp-server-replaces-direct-pms-integration.md) |

(D8 — "desktop built but not promoted" — is gone with desktop itself; ADR 0004
preserves its reasoning and why the answer changed.)

---

## Milestone · Case-management MCP — the integration surface (replaces the MyCase spike)

The pivot's constructive half
([ADR 0013](adr/0013-mcp-server-replaces-direct-pms-integration.md) /
[milestone 10](https://github.com/insolvia-ai/insolvia/milestone/10)): one
remote **MCP server** over our own domain, so any harness a firm runs can read
cases and push data *into* them — instead of Insolvia integrating with N
practice-management systems, N harnesses integrate with one Insolvia. The old
Milestone 0 (issues 0.0–0.8) is closed unexecuted; its still-live questions
carried over (App Bar → 12.6, the MyCase relationship as *channel* → 12.6/12.8).

The invariant that shapes everything here: **agent writes land as candidate
records** with provenance, confirmed by a human before they become case data —
the same seam extraction review (8.9) needs
([`case-data-model.md`](reference/case-data-model.md)).

| # | Issue | Notes |
|---|---|---|
| 12.1 | [#260](https://github.com/insolvia-ai/insolvia/issues/260) — Design the MCP surface: tools + candidate-write flow | Do first; also decides `services/mcp` vs. inside `services/api`. |
| 12.2 | [#261](https://github.com/insolvia-ai/insolvia/issues/261) — MCP auth: OAuth against the existing Cognito pool | A session is a `sub` with firm permissions (D10); nothing authorization-bearing in tokens. |
| 12.3 | [#262](https://github.com/insolvia-ai/insolvia/issues/262) — The service itself, all three environments | Lambda, `insolvia_core`, normal CI/deploy pattern. |
| 12.4 | [#263](https://github.com/insolvia-ai/insolvia/issues/263) — First end-to-end: harness reads a case, writes a candidate creditor, human confirms | The round-trip that proves the pivot. |
| 12.5 | [#264](https://github.com/insolvia-ai/insolvia/issues/264) — Verify against Claude Desktop + ChatGPT + an inspector | "Any MCP client" meets reality; per-harness gaps feed back into 12.1. |
| 12.6 | [#265](https://github.com/insolvia-ai/insolvia/issues/265) — Distribution: MCP/connector directories | The discovery channel that replaces the App Bar (old 0.6). |
| 12.7 | [#266](https://github.com/insolvia-ai/insolvia/issues/266) — Marketing repositioning off "MyCase-native" | Public story; founder signs off the copy. |
| 12.8 | [#267](https://github.com/insolvia-ai/insolvia/issues/267) — Business plan §1/§7/§10/§11 rewrite | Founder-owned; tracked so the staleness is visible. |

Sequencing against the forms milestone: forms stay **current** — they are the
value the MCP exposes. 12.1 (design, cheap) and 12.7 (the public story is
wrong today) are worth doing early; the build (12.2–12.5) benefits from 9.9
landing first, since creditors/assets/income are most of what a harness would
push.

## Outstanding foundation item · SES production access

Deliberately deferred until the site and bounce handling were live — **they now
are**, so this is actionable: submit the request per
[`ses-production-access.md`](runbooks/ses-production-access.md). Until granted, we
receive at `@insolvia.ai` but cannot reply from it. Set a date rather than
leaving it open-ended.

---

## Product milestones (planned 2026-08-01 — tracked in GitHub)

Each is a GitHub milestone with its issues filed; the issue bodies carry the
scope and "done when", so this table stays one line each. Ordering is the
dependency order. PMS integration is deliberately **out** of all five — since
the pivot (D11) it is not sync at all but the
[Case-management MCP milestone](https://github.com/insolvia-ai/insolvia/milestone/10)
above; the intake data model's old sync seam narrowed to an origin pointer
([`case-data-model.md`](reference/case-data-model.md)).

| Milestone | Business plan | Issues | One-line scope |
|---|---|---|---|
| [`Product · Auth`](https://github.com/insolvia-ai/insolvia/milestone/4) | — | 7.1–7.6 | Wire the app to the existing Cognito seam: sign-in, session, the first authenticated API endpoint (JWT verification lands server-side with it). |
| [`Product · Intake`](https://github.com/insolvia-ai/insolvia/milestone/5) | M2 / P1 | 8.1–8.6, 8.10 | Standalone intake behind auth: case data model, encrypted store, case CRUD, the intake form widgets (risk 4), the questionnaire, document upload. Only the design-partner web-first test (risk 3) is still open. |
| **[`Product · Forms & petition engine`](https://github.com/insolvia-ai/insolvia/milestone/6)** — *current* | M3 / P2 | 9.1–9.9 | Deterministic, versioned forms; Chapter 7 packet; AI review agent. Opens with the effective-date model the register demands from day one. |
| [`Product · Means test`](https://github.com/insolvia-ai/insolvia/milestone/7) | P3 | 10.1–10.4 | Rule-based §707(b), with the effective-dated IRS/Census refresh pipeline from the regulatory register. |
| [`Product · Firms & access control`](https://github.com/insolvia-ai/insolvia/milestone/8) | M2 / P1 | 11.1–11.7 | A case belongs to a **firm**, not to whoever opened it — firm users, roles, per-case linking, per-feature permissions ([ADR 0009](adr/0009-a-case-belongs-to-a-firm.md)). Two items stay open on purpose: provisioning a firm is still a hand-run script (risk 6), and the pool's case sensitivity is a decision rather than a task (risk 7). |
| [`Product · AI extraction`](https://github.com/insolvia-ai/insolvia/milestone/9) | M2 / P1 | 8.7–8.9 | Claude reading credit reports and pay stubs into candidate records, human-confirmed before case entry. **Deferred 2026-08-11** — see below. |

### Why extraction is its own milestone now (2026-08-11)

It was 8.7–8.9 inside `Product · Intake & AI extraction`. Intake shipped, and
rather than finish that milestone's tail, extraction was re-scoped as a
milestone of its own and the forms engine became the current work. Three
consequences are worth writing down, because none is obvious from the milestone
list alone:

- **The forms engine never depended on it.** Forms fill deterministically from
  *confirmed* case data (business plan §4); extraction only changes who does the
  typing. 9.1–9.3 and 9.8 were unblocked the whole time.
- **What extraction *was* hiding: a case holds almost no case data.** The API
  implements `Case`, `Debtor`, `Document` and provenance — no creditors, claims,
  assets, income or SOFA entries, though
  [`case-data-model.md`](reference/case-data-model.md) specifies all of them.
  Extraction was the assumed way those records would arrive, so nothing had
  filed the plain manual path. It is filed now as 9.9, sequenced ahead of the
  creditor matrix (9.4) and packet assembly (9.6). It was owed regardless: a
  candidate needs a record shape to be confirmed *into*, and every creditor no
  document mentions is typed by hand.
- **The Anthropic decisions moved with the work, not with the milestone.** 8.7
  was carrying where the model call runs (inside `services/api` vs its own
  service — latency against the Lambda's limits) and the no-training /
  zero-retention configuration. The AI review agent (9.7) is now the first
  Claude surface to land, so it inherits both and 8.7 adopts what it decides.

Issue numbers stayed 8.7–8.9 in the new milestone: they are cross-referenced
from 9.4, 9.7 and the ADRs, and renumbering buys nothing.

**Worth flagging now:** `business/regulatory-source-register.html` describes a
maintenance calendar (§522 dollar amounts every 3 years, Census median income
2–4×/yr, IRS standards periodically). Those are *scheduled data pipelines with
effective-date fields*, not one-time loads. Now planned: the shared
effective-date model opens the forms milestone (9.1), the Dec-1 forms cycle has
a runbook issue (9.8), and the UST refresh pipeline anchors the means-test
milestone (10.1).

---

## Risks worth watching

In order of likely bite:

1. **Harness capability and adoption (12.4–12.5).** The pivot retired the
   MyCase write-coverage risk and bought this one: "no double entry" now
   depends on the attorney's harness actually being able to read their PMS
   and drive our MCP tools well — and on firms running a harness at all,
   which narrows the early market to AI-adopting firms. Cheap to test the
   technical half (12.4/12.5); the adoption half joins the web-first bet in
   front of the design partner (8.10).
2. **The channel changed shape (12.6–12.8).** The warm MyCase relationship no
   longer shortcuts an integration; distribution now runs through connector
   directories whose review bars and timelines we have not measured, plus the
   relationship as a door-opener. Business-plan §10's channel section is stale
   until 12.8 lands.
3. **Web-first is a bet on attorney behaviour — and D9 raised the stake.** The
   market is described as desktop-loyal, and counter-evidence now costs a port
   rather than a marketing decision. **Test it explicitly with the
   design-partner firm, early** — this is the risk whose cost of late
   discovery went up rather than down.
4. **We own complex-widget accessibility.** `Modal`, `Select`, `Combobox` and
   date pickers have no third-party implementation here, in a product that
   needs all four. Guarded by the axe assertion in `app-pr.yml`; intended
   relief is `@react-native-aria/*` when it lands; ADR 0004 and 0006 record
   the trade and its revisit trigger (component count).
5. **SES production access left open-ended.** See the outstanding item above —
   the deferral was correct and its preconditions are now met.
6. **Nothing in the pipeline provisions a firm.** Self-signup is disabled on
   the pool by design, so a firm and its first administrator are created by us
   — and the only thing that exists today is a hand-run script. Onboarding a
   design partner is therefore a manual step with no audit trail and no
   second pair of eyes. The bounded version of the risk: a firm that loses its
   last administrator cannot appoint one (the API refuses the edit that would
   cause it, so the reachable path is us provisioning wrongly, not a user).
   Relief is a small provisioning surface, and the trigger is the second firm.
7. **Design-system leaf-pair cost.** Two renderings of one design still exist,
   but as `.web`/`.native` leaves of one component in one package — drift is a
   single-PR diff, not cross-package skew. What remains is the per-component
   authoring cost; [ADR 0006](adr/0006-owned-cross-platform-design-system.md)
   records it honestly, with the `styled()`-helper revisit trigger.
