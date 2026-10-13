# Decision status review — 2026-10-13

A point-in-time audit of what is **decided**, **superseded/amended**,
**half-built** and **still open** across [`plan.md`](../plan.md), the 26 ADRs in
[`adr/`](../adr/README.md), the [business plan](../business/business-plan.html),
and the open issues and PRs on `insolvia-ai/insolvia`. Each claim cites the file
(and line) or the issue/commit that owns it; "code" claims were checked against
the tree at `21ae6dd` (main, 2026-10-13).

**This is a snapshot, not an owner of any fact.** Where it finds drift, the
fix belongs in the owner it names (usually `plan.md` or an ADR), after which
this file has done its job.

**Outcome (same day).** The doc inconsistencies in § 6 were fixed in their
owners (`plan.md`, `adr/README.md`, ADRs 0001/0024/0026, `architecture.md`)
in the PR that added this file. Issues #353, #361, #368, #88, #38 and #41
were closed as done, #36 and #43 as not planned; #211 and #226 moved to
insolvia-services (insolvia-ai/insolvia-services#8, #9). Adding `Cognito
branding`, `Admin service` and `Admin portal` to `protect-main` is a
maintainer step. Still open by design: ADR 0017's status, ADR 0023's binding
owner, the business plan (#267).

Method: read `docs/CLAUDE.md`, `plan.md` in full, every ADR's header,
amendments and build breakdown, the business plan's text, all 24 open issues
(`gh issue list --state open`), open PRs (none), the `protect-main` ruleset
(read-only `gh api`), the Release workflow's run history, and `git log` since
2026-09-25.

---

## 1. Summary

- **Production has been promoted.** Release run 38397914222 (2026-10-13,
  `ef87efc`) passed `promote` and deployed every prod leg (infra, api, admin,
  mcp, marketing, app + smoke, filing); runs on 2026-10-12 (`a209b0b`,
  `5570970`) also succeeded. `plan.md` still says promote "has not yet been
  approved" and lists it as Next #1 (plan.md:8–10, 229–230).
- **ADR 0022 (a client is not a case) is fully built** — PRs 1–8 merged
  (#409, #411, #412, #416, #417, #418, #423, #425), plus the merge-race fix
  (#446). Its tracking issue #353 is still open.
- **ADR 0023 (client portal) is fully built** — PRs 1–5 merged (#405, #413,
  #427, #429, #430); #362–#364 closed. Tracking issue #361 still open.
  Real use is gated on SES production access, ZDR and the prod custom auth
  domain (plan.md:240–241).
- **ADR 0024 (automated e-filing) is built through PR 9; PR 11 is decided.**
  Merged: PR 2 #404, PR 3 #432, PR 4 vault #433, PR 5 authorization #434,
  PR 6 per-filing approval #435, PR 7 worker + fake CM/ECF #437, PR 8
  filed-state capture #438, PR 9 Case Upload #440; PR 11 (fees) decided
  2026-10-09 (#441). **Missing: PR 10 — no real court driver exists**
  (`services/filing/src/insolvia_filing/core/drivers/` holds only `base.py`,
  `fake.py`, `screens.py`), so no district can file automatically yet.
- **Auth and shared infra moved to insolvia-services** (ADR 0025, ADR 0026 +
  same-day amendment); the cutover ran (#450, #452, #459, #460). This repo
  reads auth via `infra/modules/auth_contract` (SSM `/insolvia/<env>/auth/*`).
- **The MCP pivot (ADR 0013/0016) is built but unproven end to end**: the
  service exists (`services/mcp`), but the harness round-trip #263 and
  multi-harness verification #264 are open, as are distribution #265 and the
  repositioning #266/#267.
- **ADR 0017 (launch states FL/TX/GA) is still "Proposed"** although its four
  exemption series ship (`services/api/src/insolvia_api/regulatory/exemptions/{federal,fl,ga,tx}`)
  and ADR 0024's ten launch districts depend on it. Its trigger (the
  design-partner firm's state) depends on #90, which is not started.
- **ADR 0007's required CSP still does not exist**: `infra/modules/web_hosting/`
  has no `response_headers_policy` (#114, open since 2026-08-01) while the
  refresh token sits in `localStorage`.
- **ADR 0019's ZDR item is still open** and now gates three things: real-firm
  extraction, client uploads (`services/api/src/insolvia_api/api/routes/portal_documents.py:20`)
  and the portal's first real invite.
- **The brand drift gate is not a required check.** The ruleset requires 10
  checks (API client, API service, App, Core package, Filing worker, Form
  specs, MCP service, Marketing site, Terraform validate staging/prod) — not
  `Cognito branding` (the ADR 0020 drift gate) and not `Admin service` /
  `Admin portal`. `docs/reference/architecture.md:322–333` lists a different
  seven.
- **The business plan (v0.3, 2026-07-15) still describes the pre-pivot
  product** — MyCase-native, desktop included, Chapter 11, IEPD-first
  e-filing, day-one Best Case import. Reconciliation is #267, open.
- **About a third of the open issues are stale or done**: #353, #361, #368
  (decided/built), #88 (pay-stub extraction exists), #38, #41, #36 (done or
  obsoleted by `release.yml` and ADR 0025), #43 (rejected in a workflow
  comment), and #211/#226 now belong to insolvia-services.

---

## 2. Decided

"Implemented?" is checked against code, not docs.

| Decision | Source | Status | Implemented? |
|---|---|---|---|
| D1 Domain is insolvia.ai | plan.md:101 | Decided | Yes — live surfaces, plan.md:84–89 |
| D2 Flat `staging-*` subdomains | plan.md:102 | Decided | Yes — plan.md:84–89 |
| D3 Marketing on React Router v7 SSR | plan.md:103 | Decided | Yes — `apps/insolvia_marketing`, `marketing-*.yml` |
| D4 / ADR 0006 One owned cross-platform design system | plan.md:104; ADR 0006 | Accepted; consumption superseded by 0010 | Yes — consumed from `insolvia-ai/design-system` |
| D5 / ADR 0001 Client stays dumb; API is the trust boundary | plan.md:105; ADR 0001 | Accepted, amended by 0011 and 0024 | Yes |
| D6 Python + Flask + Mangum on Lambda | plan.md:106 | Decided | Yes — `services/api`, `admin`, `mcp` |
| D7 Separate human/product email providers | plan.md:107 | Decided; owned by insolvia-services | Yes (SES still sandboxed — #211) |
| D9 / ADR 0004 React Native on Expo; free tier; no desktop | plan.md:109; ADR 0004 | Accepted | Yes — `apps/insolvia_app`; EAS guard in `app-pr.yml` |
| D10 / ADR 0009 A case belongs to a firm; authz in our store | plan.md:108; ADR 0009 | Accepted | Yes — `packages/insolvia_core/src/insolvia_core/firms.py`, `access.py` |
| D11 / ADR 0013 No direct PMS integration; MCP server | plan.md:110; ADR 0013 | Accepted | Partial — service built, round-trip unproven (#263, #264) |
| ADR 0005 Expo project structure | ADR 0005 | Accepted | Yes |
| ADR 0007 Hosted-UI PKCE, refresh token in localStorage, **CSP required** | ADR 0007 | Accepted (amended 2026-09-16; amended by 0009) | Partial — sign-in yes; **CSP missing** (#114) |
| ADR 0008 Testing shape per area | ADR 0008 | Accepted | Yes |
| ADR 0010 Design system in its own repo | ADR 0010 | Accepted | Yes — no `packages/insolvia_design_system*` here |
| ADR 0011 Staff are a separate principal; Google OIDC; `services/admin` | ADR 0011 | Accepted (amended 2026-08-11: GIS) | Yes — `services/admin/src/insolvia_admin/api/routes/firms.py` (`POST /v1/firms`), `apps/insolvia_admin/src/screens/provision.tsx` |
| ADR 0012 Shared Python domain package | ADR 0012 | Accepted | Yes — `packages/insolvia_core` |
| ADR 0014 Repo is the regulatory release registry | ADR 0014 | Accepted | Yes — `services/api/src/insolvia_api/regulatory/`, `packages/insolvia_core/src/insolvia_core/regulatory/courts/us-bankruptcy/` |
| ADR 0015 Async pipelines beside the lambdalith; SAM rejected | ADR 0015 | Accepted | Yes — `infra/modules/job_pipeline` |
| ADR 0016 MCP server is its own service | ADR 0016 | Accepted | Yes — `services/mcp`, `infra/modules/mcp_service` |
| ADR 0018 SQS + worker Lambda over Step Functions | ADR 0018 | Accepted | Yes — `infra/modules/job_pipeline`, `filing_queue` |
| ADR 0019 Claude in the worker, Anthropic API direct; ZDR launch-gated | ADR 0019 (amended 09-04, 09-05, 09-23) | Accepted | Partial — review + extraction (`core/petition_review.py`, `core/extraction.py`); **ZDR not in place** |
| ADR 0020 Brand is a consumer-owned override from `brand/colors.json` | ADR 0020 | Accepted, amended by 0026 | Yes — `tool/render-brand-theme.ts`, `branding-pr.yml`; but the gate is not required (§6) |
| ADR 0021 Four test tiers; seed fixtures from one bucket | ADR 0021 | Accepted | Yes — `e2e/tests/{flows,smoke}`, `seeds/` |
| ADR 0022 A client is not a case | ADR 0022 | Accepted 2026-09-26 (+ 2026-10-01 funnel note) | Yes — PRs 1–8 merged; `insolvia_core/firm_clients.py`, `client_merge.py` |
| ADR 0023 Client is a third principal class | ADR 0023 | Accepted 2026-09-26 (+ 2026-10-06 `client_answered` note) | Yes (PRs 1–5); gated for real use (§4) |
| ADR 0024 Insolvia files under the attorney's CM/ECF login | ADR 0024 | Accepted 2026-09-26 (reversed from draft) | Partial — PRs 1–9 built, PR 11 decided, **PR 10 not started** |
| ADR 0024 PR 11: fee paid by attorney at hand-back; no card data | ADR 0024 build table row 11; #441 | Decided 2026-10-09 | Decision only; per-district check is PR 10 |
| ADR 0025 Shared infra + mailer move to insolvia-services | ADR 0025 | Accepted 2026-10-11, amended by 0026 | Yes — #450; no `services/mailer`, no `infra/envs/shared` here |
| ADR 0026 Auth is a shared service; SSM contract; one shared dev pool | ADR 0026 | Accepted 2026-10-12, amended same day | Yes — #452, #459, #460; `infra/modules/auth_contract` |
| Lifecycle: prospect funnel on the client, a case starts retained | ADR 0022 § Build breakdown, 2026-10-01 note; plan.md:231–239 | Decided (maintainer) | Yes — #425 |
| Prod data disposable pre-launch: reseed, don't migrate | plan.md:330–332; ADR 0022 status | Decided (maintainer, 2026-09-26) | Yes — `services/admin/.../pre_client_purge.py` |
| Import spike parked; no importer scheduled | plan.md:182; `business/switching-cost-and-import.html` | Decided (maintainer) | N/A — deliberately not built |

---

## 3. Superseded / reversed / amended

| What | Replaced or changed by | Sources |
|---|---|---|
| ADR 0002 Desktop auto-update deferred | **Superseded** by ADR 0004 (no desktop at all) | 0002 Status; 0004 Relates-to |
| ADR 0003 Flutter app layout | **Superseded** by ADR 0005 | 0003 Status; 0005 Relates-to |
| D8 "desktop built but not promoted" | **Superseded** by D9 / ADR 0004 | plan.md:112–113; 0002 Status |
| Flutter (the whole stack) | **Replaced** by React Native on Expo | ADR 0004; plan.md:109 |
| ADR 0004 § *Marketing stays put* (two design systems) | **Revised** by ADR 0006 | 0004 Status; 0006 Relates-to |
| ADR 0006 consumption (workspace member + symlink) | **Superseded** by ADR 0010 (published packages) | 0006 Status; 0010 Relates-to |
| ADR 0007 "server-side authz reads only verified token claims" | **Amended** by ADR 0009 (authz in our store) | 0007 body ("Amended by ADR 0009"); 0009 `Amends:` |
| ADR 0007 reload behaviour | **Amended** 2026-09-16 (the reload is optimistic) | 0007 body, line 37 |
| ADR 0011 point 3 (portal PKCE redirect) | **Amended** 2026-08-11 → Google Identity Services | 0011 Status |
| ADR 0001 single application principal | **Amended** by ADR 0011 (a second principal reaches the firm table) and ADR 0024 (a third party's credential held server-side) | 0011 Relates-to; 0001:42–51 |
| ADR 0015 orchestration primitive (deferred) | **Resolved** by ADR 0018 (SQS + worker) | 0015 Decision; 0018 Relates-to |
| ADR 0019 key storage | **Amended** 2026-09-05 (originally SSM SecureString) | 0019:28 |
| ADR 0019 scope | **Amended** 2026-09-04 (extraction sends document bytes; ZDR gates real-firm documents) and 2026-09-23 (tax id stored, 13.12) | 0019:154, 0019:177 |
| ADR 0020 "four generated surfaces incl. Cognito" | **Amended** by ADR 0026's amendment: three surfaces; Cognito's sign-in style moves to insolvia-services | 0020 Status; 0026 § Amendment (line 217) |
| ADR 0023 accepted-answer provenance source `client` | **Changed** 2026-10-06 to `client_answered` (0022 had taken `client`) | ADR 0023 § Build breakdown note; plan.md:248–252 |
| ADR 0024 first draft: prepare-and-hand-off | **Reversed** 2026-09-26 by the maintainer → Insolvia files itself | ADR 0024 Status/Date and Decision |
| ADR 0025 list of what moves | **Amended** by ADR 0026 (auth moves too; fourth cross-repo seam) | 0025 Status; 0026 `Amends:` |
| ADR 0026 rule 5 ("the brand stays with the consumer") and its first "stays here" row | **Superseded** by its own 2026-10-12 Amendment | 0026 Status; 0026:81, 0026:200 |
| Per-machine dev Cognito pools | **Replaced** by one shared dev pool (`dev-auth.insolvia.ai`) | ADR 0026 Status |
| Business plan's MyCase spike (M1) and MyCase-native intake (M2) | **Retired** by the MCP pivot | plan.md:35–40, 51–57; ADR 0013 |
| Extraction inside the Intake milestone | **Split** into its own milestone 2026-08-11 | plan.md:184–211 |
| Staff Cognito pool for admin | **Built and reverted** → direct Google OIDC | ADR 0011 Decision |

---

## 4. Half-built

| Item | What exists | What is missing | Evidence | Issues |
|---|---|---|---|---|
| **ADR 0024 automated e-filing** | Registry, matrix-by-court, filing set + checklist, vault (dedicated KMS key), signed authorization, per-filing approval, worker + fake CM/ECF with fence and kill switch, filed-state capture, Case Upload `debtor.txt` | **PR 10**: no per-court driver, no training-database verification, so every real court hands back. PR 12 (XML) is conditional. Per ADR 0024, no production filing until PRs 4–8 are live in prod **and** one district is verified | `services/filing/src/insolvia_filing/core/drivers/` (fake only); `infra/modules/filing_credentials`, `filing_queue`, `filing_worker`; commits #404, #432–#435, #437, #438, #440 | #368 (open but decided), #369 |
| **MCP integration (ADR 0013/0016)** | `services/mcp`; harness client IDs come through `infra/modules/auth_contract/outputs.tf:42–47`; client records over MCP (#418) | A real harness round-trip (12.4); verification on Claude Desktop, ChatGPT and an inspector (12.5); directory listings (12.6) | plan.md:138–142 | #263, #264, #265 |
| **ADR 0007 CSP** | Sign-in, memory tokens, refresh token in `localStorage` | The CloudFront response-headers policy / CSP that ADR 0007 calls load-bearing | `infra/modules/web_hosting/main.tf` (no `response_headers_policy`) | #114 |
| **ADR 0019 ZDR** | No-training standing; tax-id scrub; client uploads never auto-extracted | Zero-data-retention terms (a business follow-up), which gate real-firm extraction and the portal's real use | `services/api/src/insolvia_api/core/extraction.py:15–30`; `api/routes/portal_documents.py:20` | none open |
| **ADR 0023 portal → real debtors** | All five PRs | SES production access, ZDR, prod custom auth domain (`auth.insolvia.ai` still "future" — `infra/modules/auth_contract/outputs.tf:27–28`, `infra/envs/prod/main.tf:283`) | plan.md:240–241 | #211 (now insolvia-services' concern) |
| **ADR 0023 binding owner** | Binding keyed to the **case** (`PK CASE#…`, `case_id`) | ADR 0023 § Binding says that once 0022 lands "the binding moves from the case to the client record"; 0022 has landed and the binding has not moved, and nothing tracks it | `packages/insolvia_core/src/insolvia_core/clients.py:1–40, 104`; ADR 0023 Decision §3 | — |
| **Forms milestone done-when (9.6)** | Packet gate, assembly, storage and pins, app screen (#300/#301) | The human walkthrough: a staging case judged filing-shaped by the design partner | #96 comment 2026-09-04 | #96, #90 |
| **Brand drift gate (ADR 0020)** | `branding-pr.yml` (job `Cognito branding`) checks the app/marketing/admin outputs | Not in `protect-main`'s required checks, so drift can merge | `scripts/update-ruleset.sh show`; `branding-pr.yml:1–12` | — |
| **Email brand (#228)** | `services/api/src/insolvia_api/core/mail.py:47–53`: seven hand-typed hex colours | Generation from `brand/colors.json`. The issue's design names `tool/reconcile-cognito-branding.ts`, which no longer exists (`tool/` has `render-brand-theme.ts`) | — | #228 |
| **Plan.md Next #6 small follow-ups** | Fixed: `iat` leeway (#443, #449), name formatting (#442) | Still open, not tracked by any issue: B113 §3.1's blank current-installment column; the seed drift report (reverses ADR 0021's "existing rows are left alone" — the maintainer's call); a scratch portal client for integration tests | plan.md:311–320 | none |
| **TS lint strictness (#33)** | Flat ESLint configs in `apps/insolvia_{app,admin,marketing}` | `strict-type-checked` anywhere; the issue's design-system half left with ADR 0010 | `apps/*/eslint.config.*` | #33 |
| **CI supply chain (#42)** | Actions pinned to tags (`actions/checkout@v5` …) | SHA pins; no `.github/dependabot.yml` | `.github/workflows/*.yml` | #42 |

---

## 5. Still open

### Undecided questions / Proposed ADRs

- **ADR 0017 is Proposed**: launch states FL/TX/GA are provisional until the
  design-partner firm's state is known (0017 Status), even though the data and
  ADR 0024's district scope depend on it.
- **Pool case sensitivity** — "a decision rather than a task" (plan.md:176,
  risk 7). No ADR, no open issue.
- **Seed drift report / may the loader repair fixtures?** Reverses ADR 0021 —
  the maintainer's call (plan.md:314–317).
- **Web-first vs desktop-loyal market** — risk 3 (plan.md:352–356); only the
  design partner can answer it (#90).
- **ADR 0023 reopen question**: does managed login offer a per-app-client
  first factor (email OTP for debtors)? Unverified (ADR 0023 § Risks).
- **Runtime vs build-time app environment** — #43 proposes runtime config;
  `.github/workflows/app-prod.yml:24–27` says it is "deliberately not taken".
  Needs a close-as-wontfix or a decision record.
- **XML case opening** (ADR 0024 PR 12) — only if PR 10 shows a district
  exposes it.

### Open issues by theme (24 open, 0 open PRs)

- **MCP / pivot**: insolvia-ai/insolvia#263 (12.4 round-trip),
  insolvia-ai/insolvia#264 (12.5 harness verification),
  insolvia-ai/insolvia#265 (12.6 distribution),
  insolvia-ai/insolvia#266 (12.7 marketing repositioning),
  insolvia-ai/insolvia#267 (12.8 business plan rewrite).
- **Filing and after**: insolvia-ai/insolvia#369 (17.2 court-notice intake —
  unblocked by #438's `caseNumberKey`; not started),
  insolvia-ai/insolvia#368 (17.1 — done-when "ADR written and decision
  recorded" is met; close).
- **Product tracking issues whose work is done**: insolvia-ai/insolvia#353
  (14.1 — ADR 0022 built), insolvia-ai/insolvia#361 (15.1 — ADR 0023 built),
  insolvia-ai/insolvia#88 (8.8 pay stubs — `EXTRACTABLE_DOCUMENT_KINDS =
  ("credit_report", "pay_stub")`, `core/extraction.py:104, 548`).
- **Human / business gates**: insolvia-ai/insolvia#90 (design partner, also
  owns ADR 0017's trigger and risk 3), insolvia-ai/insolvia#96 (9.6
  walkthrough), insolvia-ai/insolvia#211 (SES production access — the SES
  identity now lives in insolvia-services).
- **Security**: insolvia-ai/insolvia#114 (CSP), insolvia-ai/insolvia#226
  (Identity Center — `account-access` moved to insolvia-services under ADR 0025).
- **Brand**: insolvia-ai/insolvia#228 (email colours from the brand file).
- **CI / tooling (2026-07 backlog, mostly stale)**: insolvia-ai/insolvia#33
  (strict TS lint), insolvia-ai/insolvia#36 (shared → staging ordering —
  `shared` moved out and `release.yml` chains legs, #407),
  insolvia-ai/insolvia#38 (required checks — done: 10 required, and
  `scripts/update-ruleset.sh` manages them), insolvia-ai/insolvia#39
  (`merge_group` — no workflow has it), insolvia-ai/insolvia#40 (staging E2E —
  largely delivered by ADR 0021's `e2e/tests/flows`/`smoke`),
  insolvia-ai/insolvia#41 (one approval per release — done: `release.yml`'s
  single `promote` job, prod legs' `approve` skipped),
  insolvia-ai/insolvia#42 (SHA pins + Dependabot), insolvia-ai/insolvia#43
  (runtime config; Flutter-era text), insolvia-ai/insolvia#44 (parked epic).

### Plan-level open items not tied to an issue

- Risk 1/2 (harness adoption, channel) — plan.md:340–351.
- Risk 4 (complex-widget accessibility) — plan.md:357–361; the relief named,
  `@react-native-aria/*`, has no issue.

---

## 6. Inconsistencies

Each names a suggested owner of truth (the one to fix the others against).

| # | Disagreement | Citations | Owner of truth |
|---|---|---|---|
| 1 | Plan says prod promote has never run; it ran 2026-10-12 and 2026-10-13 | plan.md:8–10, 229–230 vs Release runs 38397914222, 38276718020 | Release run history → update plan.md |
| 2 | Plan's ADR 0024 "Next" lists the vault, authorization and approval as to do; they merged (#433–#435), and PR 9 (#440) goes unmentioned | plan.md:277, 289–295 vs git log | git → plan.md; the ADR 0024 build table should mark PRs 2–9 done (only PR 1 says "Done", adr/0024:462–463) |
| 3 | Plan contradicts itself on the portal: "Left: PR 5 (#364)" vs "ADR 0023 is built (PRs 1–5)" | plan.md:180 vs plan.md:240 | plan.md:240 (#430 merged, #364 closed) |
| 4 | Plan says provisioning a firm is "a hand-run script" (risk 6); the admin service and portal have provisioned firms since #229 (2026-08-10) | plan.md:176, 364–371 vs `services/admin/.../routes/firms.py` `POST /v1/firms`, `apps/insolvia_admin/src/screens/provision.tsx` | Code → rewrite risk 6 (remaining gap: audit and a second pair of eyes, not the absence of a surface) |
| 5 | Plan says #96/#97 "stay open"; #97 closed 2026-09-04 | plan.md:174 vs #97 | GitHub |
| 6 | Plan's live table is stale: API "health + waitlist"; tokens row says "generated here is Cognito's sign-in branding"; auth "pools + hosted UI per env"; CI "7 required checks" | plan.md:74, 75, 78, 79 vs ADR 0020/0026 amendments, ruleset (10 checks) | ADRs 0020/0026 + ruleset → plan.md |
| 7 | `architecture.md` lists seven required checks, including `Cognito branding`; the ruleset requires ten, without `Cognito branding` and without Admin. So ADR 0020's "gated by the existing check" gate is not enforced | docs/reference/architecture.md:322–333; ADR 0020 Decision; `branding-pr.yml:1–12` vs `scripts/update-ruleset.sh show` | Decide the intended set (likely add `Cognito branding`, `Admin service`, `Admin portal` via `scripts/update-ruleset.sh`); then architecture.md follows the ruleset |
| 8 | Plan's Next #6 lists the `iat` leeway and the two name formats as open; both are fixed | plan.md:312–314 vs #442, #443, #449 | git → plan.md |
| 9 | ADR index (adr/README.md) errors: the 0010 row is labelled "0009"; 0006's cell reads "superseded by [0009](0010-…)"; rows out of order (0009 before 0008, 0021 before 0020); 0023's summary still says writes carry "provenance source `client`"; 0001's row omits the 0011 amendment; 0007 and 0019 are shown as plain "Accepted" despite in-body amendments | adr/README.md:15, 17–19, 29–30, 32, 10 vs each ADR's Status/body | Each ADR's own Status line → README |
| 10 | ADR 0001's Status says "Accepted" with no mention of the 0011 amendment, which 0011 declares | adr/0001:3 vs adr/0011 Relates-to (line 19) | ADR 0011 → add a note to 0001 |
| 11 | ADR 0001 cites `docs/regulatory-source-register.html`, which now lives in `docs/business/` | adr/0001:18 | Fix the path |
| 12 | ADR 0026's Relates-to still says ADR 0020 is "why the branding stays here", yet its own Amendment moved the branding | adr/0026:15 vs adr/0026:217ff | The Amendment → edit Relates-to |
| 13 | ADR 0023 says the binding moves to the client record when 0022 lands; 0022 landed, the binding is still case-keyed, and nothing tracks the move | adr/0023 Decision §3 vs `insolvia_core/clients.py` | Maintainer: amend 0023 (keep case-keyed) or file the move |
| 14 | Naming collision: `insolvia_core/clients.py` is the **portal binding** while `firm_clients.py` is ADR 0022's **client** | `packages/insolvia_core/src/insolvia_core/clients.py:1` vs `firm_clients.py` | Code-level; rename at the next touch |
| 15 | ADR 0017 is "Proposed" but implemented and depended on (ADR 0024's ten launch districts) | adr/0017:3 vs `regulatory/exemptions/{fl,ga,tx}`; adr/0024:13 | Maintainer: accept as provisional, or keep Proposed and say it ships |
| 16 | Business plan vs pivot: "AI-assisted, MyCase-native", MyCase spike and MyCase intake as M1/M2 | business-plan.html:7, 275, 303, 519–520 vs ADR 0013; plan.md:55–57 | ADR 0013 (plan.md already says so); #267 |
| 17 | Business plan keeps desktop ("A native desktop option remains", "+ Desktop", "$129/seat/mo, desktop included") | business-plan.html:292, 336, 426 vs ADR 0004 / D9 | ADR 0004 |
| 18 | Business plan scopes Chapters 7, 11 and 13; nothing in plan.md or the ADRs plans Chapter 11 | business-plan.html:289, 446 vs plan.md:170–182 | Founder decision → #267 |
| 19 | Business plan: "build to the IEPD/CM/ECF spec early" and "PACER pull" for notices; ADR 0024 drives the court's screens (XML is post-launch PR 12), and #369 ingests notices from email | business-plan.html:481, 508 vs ADR 0024 Decision and PR 12; #369 | ADR 0024 / #369 |
| 20 | Business plan: "Claude parses Best Case exports on day one"; the import spike found Best Case publishes no export, and an importer is parked | business-plan.html:466 vs `business/switching-cost-and-import.html:247`; plan.md:182 | The spike + maintainer's parking decision |
| 21 | Business plan lists "zero-retention terms" as an existing mitigation; ADR 0019 holds ZDR as an open, launch-gated follow-up | business-plan.html:545 vs ADR 0019 Decision | ADR 0019 |
| 22 | Several open issues describe a world that no longer exists: #43 (Flutter `--dart-define`, `docs/ARCHITECTURE.md`), #38/#41/#36 (pre-`release.yml`), #228 (`reconcile-cognito-branding.ts`), #33 (design-system package), #211/#226 (resources now in insolvia-services) | the issues vs `.github/workflows/release.yml`, `app-prod.yml:24–27`, `tool/`, ADR 0010/0025 | Triage: close done or obsolete ones; transfer #211/#226 to insolvia-services |
| 23 | Done tracking issues still open: #353, #361, #368, #88 | issues vs merged PRs (§5) | GitHub → close |
