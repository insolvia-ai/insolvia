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

Everything below deploys to staging on merge to `main`; production is a manual
dispatch (`scripts/prod-deploy.sh`). One line each — the linked owner has the
detail:

| Surface / concern | What exists | Owner of the detail |
|---|---|---|
| Domain + DNS + TLS | `insolvia.ai` on Route53, shared `*.insolvia.ai` ACM cert | [`terraform.md`](reference/terraform.md) |
| Human email | Google Workspace inbound, SES outbound `no-reply@` | [`email.md`](reference/email.md) |
| Marketing site | React Router v7 SSR Lambda, `www.insolvia.ai` (+ noindexed staging), Lighthouse-gated in CI | [`apps/insolvia_marketing/CLAUDE.md`](../apps/insolvia_marketing/CLAUDE.md) |
| Web app | Expo / React Native SPA, `app.insolvia.ai`, S3 + CloudFront | [ADR 0004](adr/0004-react-native-replaces-flutter.md) · [`apps/insolvia_app/CLAUDE.md`](../apps/insolvia_app/CLAUDE.md) |
| Design system | **One cross-platform package** (`@insolvia-ai/design-system`) serving both surfaces — platform-split leaves, no third-party UI dependency | [ADR 0006](adr/0006-owned-cross-platform-design-system.md) |
| Tokens | One `tokens.json` → generated `theme.css` (web) + `tokens.ts` (native), drift-gated in CI | [`packages/insolvia_tokens/README.md`](../packages/insolvia_tokens/README.md) |
| API | Python/Flask on Lambda, `api.insolvia.ai` — health + waitlist; the client-stays-dumb trust boundary | [ADR 0001](adr/0001-client-stays-dumb-trust-boundary.md) · [`services/api/CLAUDE.md`](../services/api/CLAUDE.md) |
| API client | Hand-written TypeScript, contract-pinned by tests, wired into the app | [`packages/insolvia_api_client/CLAUDE.md`](../packages/insolvia_api_client/CLAUDE.md) |
| Mailer | SES-backed transactional service with unsubscribe path | [`services/mailer/CLAUDE.md`](../services/mailer/CLAUDE.md) |
| Auth (seam only) | Cognito pools + hosted UI per env; `/auth/callback` registered; **the app does not sign in yet** | `infra/modules/auth/main.tf` |
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

## Product milestones (to be planned in their own sessions)

| Milestone | Business plan | One-line scope |
|---|---|---|
| `Product · Auth` | — | Wire the app to the existing Cognito seam: sign-in, session, the first authenticated API endpoint (JWT verification lands server-side with it). |
| `Product · Intake & AI extraction` | M2 / P1 | Claude extracting credit reports and pay stubs; intake behind auth. **Shape depends on Milestone 0's findings** — particularly push vs. poll sync. |
| `Product · Forms & petition engine` | M3 / P2 | Deterministic, versioned forms; Chapter 7 packet; AI review agent. |
| `Product · Means test` | P3 | Rule-based, with the IRS/Census refresh pipeline from the regulatory register. |

**Worth flagging now:** `business/regulatory-source-register.html` describes a
maintenance calendar (§522 dollar amounts every 3 years, Census median income
2–4×/yr, IRS standards periodically). Those are *scheduled data pipelines with
effective-date fields*, not one-time loads. Not planned yet, but they must not
be a surprise when the forms engine lands.

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
6. **Design-system leaf-pair cost.** Two renderings of one design still exist,
   but as `.web`/`.native` leaves of one component in one package — drift is a
   single-PR diff, not cross-package skew. What remains is the per-component
   authoring cost; [ADR 0006](adr/0006-owned-cross-platform-design-system.md)
   records it honestly, with the `styled()`-helper revisit trigger.
