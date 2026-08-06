# Insolvia — MVP Plan

Working document, kept **current**: it describes what is live, the decisions
in force, and the work ahead. Completed milestones are summarized in one table
— their full histories live in git, the PRs, and the ADRs, not here. Desktop
was dropped entirely (D9); this document no longer plans for it.

Status: **foundation shipped; product work ahead** · Last pruned 2026-08-01

---

## How this maps to the business plan

`docs/business/business-plan.html` §11 defines M0–M3 as *company* milestones:

| Business-plan milestone | Status |
|---|---|
| **M0 · Foundation repo** — design system, app shell, CI/CD, infra authored | ✅ Shipped |
| **M1 · Live staging + MyCase API spike** | Staging **live** (all four surfaces); MyCase spike **open** — see Milestone 0 below |
| **M2 · MyCase-native intake + AI extraction** | Next up (§ Product milestones) |
| **M3 · Compliant Chapter 7 packet** | After M2 (§ Product milestones) |

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

(D8 — "desktop built but not promoted" — is gone with desktop itself; ADR 0004
preserves its reasoning and why the answer changed.)

---

## Milestone 0 · MyCase API spike — the one open foundation milestone

**Not urgent, fully unblocked, and cheap** — a founder's business partner works
at MyCase, and the API is publicly documented:

> https://mycaseapi.stoplight.io/docs/mycase-api-documentation/k5xpc4jyhkom7-getting-started

Run docs-first: most questions (write coverage, data model, rate limits,
webhook vs. polling) are answerable before holding a credential. The
relationship covers access and fast answers; it does **not** cover what the API
can technically do — whether write endpoints exist for bankruptcy-intake fields
is a property of the API surface, and the no-double-entry promise dies without
write.

**Outcome:** the integration's technical shape documented well enough to design
the intake milestone against it.

| # | Issue | Notes |
|---|---|---|
| 0.0 | **Documentation spike** — answer everything answerable without credentials | Do first; free and unblocked. Turns 0.2–0.5 into confirmation rather than discovery. |
| 0.1 | Obtain MyCase API credentials | Advanced tier (~$89/mo), business-plan §3. Partner relationship — not a blocker. |
| 0.2 | Authenticate; one **read** round-trip | Proves credentials, scopes, rate limits. |
| 0.3 | One **write** round-trip | The riskier half — read-generous/write-thin APIs are common. |
| 0.4 | Map MyCase's data model → bankruptcy intake fields | Gaps here shape the intake milestone. |
| 0.5 | Document rate limits, pagination, webhook/sync options | Push vs. poll vs. on-demand is an architectural fork for intake. |
| 0.6 | Investigate App Bar listing requirements | The discovery channel — learn its bar and timeline now. |
| 0.7 | **Confirm the commercial relationship formally** | Business-plan §10: a personal relationship moves with the person. Formal partnership + a second channel (direct/NACBA) before this is leaned on in a raise. |
| 0.8 | Write the go/no-go, incl. what changes if MyCase says no | Far better discovered here than in the intake milestone. |

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
dependency order. MyCase sync is deliberately **out** of all four — it waits on
Milestone 0; the intake data model keeps a sync seam open (issue 8.1).

| Milestone | Business plan | Issues | One-line scope |
|---|---|---|---|
| [`Product · Auth`](https://github.com/insolvia-ai/insolvia/milestone/4) | — | 7.1–7.6 | Wire the app to the existing Cognito seam: sign-in, session, the first authenticated API endpoint (JWT verification lands server-side with it). |
| [`Product · Intake & AI extraction`](https://github.com/insolvia-ai/insolvia/milestone/5) | M2 / P1 | 8.1–8.10 | Standalone intake behind auth; Claude extracting credit reports and pay stubs, human-confirmed before case entry. Includes the intake form widgets (risk 4) and the design-partner web-first test (risk 3). |
| [`Product · Forms & petition engine`](https://github.com/insolvia-ai/insolvia/milestone/6) | M3 / P2 | 9.1–9.8 | Deterministic, versioned forms; Chapter 7 packet; AI review agent. Opens with the effective-date model the register demands from day one. |
| [`Product · Means test`](https://github.com/insolvia-ai/insolvia/milestone/7) | P3 | 10.1–10.4 | Rule-based §707(b), with the effective-dated IRS/Census refresh pipeline from the regulatory register. |
| [`Product · Firms & access control`](https://github.com/insolvia-ai/insolvia/milestone/8) | M2 / P1 | 11.1–11.7 | A case belongs to a **firm**, not to whoever opened it — firm users, roles, per-case linking, per-feature permissions ([ADR 0009](adr/0009-a-case-belongs-to-a-firm.md)). Two items stay open on purpose: provisioning a firm is still a hand-run script (risk 6), and the pool's case sensitivity is a decision rather than a task (risk 7). |

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

1. **MyCase write coverage (0.3–0.4).** Access is de-risked; whether the API
   exposes write endpoints for intake fields is not. Cheap to answer, shapes
   the intake milestone.
2. **Channel formality (0.7).** The warm channel rests on a personal
   relationship. Business-plan §10 calls for a formal partnership plus a
   second channel before it is leaned on in a raise.
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
