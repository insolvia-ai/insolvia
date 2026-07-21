# Insolvia — MVP Foundation Plan

Working document. Edit freely; GitHub milestones and issues get created from
this once it's agreed. Everything below is scoped to the **foundation** —
getting the domain, email, the design system's second target, three public
surfaces, and the API trust boundary standing up. Product features (MyCase,
forms engine, means test) are stubbed at the end and belong in later sessions.

Status: **draft, awaiting review** · Author: planning session 2026-07-21 (rev 2)

---

## How this maps to the business plan

`docs/business-plan.html` §11 defines M0–M3. Those are *company* milestones.
Everything in this document sits inside business-plan **M0 → M1** — it is the
engineering work that turns "foundation repo authored" into "live staging,
ready for the MyCase spike."

| Business-plan milestone | This document |
|---|---|
| **M0 · Foundation repo** — design system, app shell, CI/CD, infra authored | ✅ Shipped (PRs #1–#3) |
| **M1 · Live staging + MyCase API spike** | Milestones 1–6 below, then the MyCase spike |
| **M2 · MyCase-native intake + AI extraction** | Stub only (§ Beyond foundation) |
| **M3 · Compliant Chapter 7 packet** | Stub only (§ Beyond foundation) |

Milestones are named `Area · Thing` to match the repo's existing workflow
naming convention.

---

## Decisions taken

| # | Decision | Rationale |
|---|---|---|
| D1 | Domain is **insolvia.ai**, not `.com` | Repo, CLAUDE.md, shared ACM wildcard, and `environment.dart` are all already built on it. `.com` would mean re-authoring the shared env for no gain. |
| D2 | Subdomain map — see the table below; every environment gets its own host, staging included | Staging needs a full parallel stack, not just an app. Flat `staging-*` naming (not `*.staging`) is load-bearing — see D2 below. |
| D3 | Marketing site is **React Router v7**, mirroring `andreas-services/website` | Flutter web cannot be server-rendered or crawled. See D3 below. |
| D4 | The **design system becomes dual-target**: Flutter package + React package, over one shared token source | Consequence of D3. If we're adding a framework, the design system serves both rather than fragmenting the brand. See D4 below. |
| D5 | **The API is required for MVP**, not deferred | The desktop app is a fat client on an attorney's machine. It cannot hold AWS credentials. Per `docs/regulatory-source-register.html`, we handle SSNs and full financials under GLBA Safeguards — the trust boundary has to live server-side. |
| D6 | Backend stack is **Python + Flask + Mangum on Lambda** | Confirmed against `andreas-services/mailer/requirements.txt` (Flask 3.1.2, Mangum 0.17, gunicorn) — the established house pattern, used by mailer, storybook, and website. |
| D7 | Human email and product email are **separate milestones** | Inbound forwarding to Gmail is cheap, urgent, and has no app dependency. The mailer service depends on the API. Bundling them would block the urgent thing behind the slow thing. |
| D8 | **Web is the promoted path. Desktop is built but not promoted** — unsigned, no certificate procurement yet | See D8 below. |

### D2 — the subdomain map

| Surface | Production | Staging |
|---|---|---|
| Marketing | `www.insolvia.ai` (apex 301s here) | — (PR previews only, by decision) |
| Web app | `app.insolvia.ai` | `staging-app.insolvia.ai` |
| API | `api.insolvia.ai` | `staging-api.insolvia.ai` |

Marketing has no staging environment: it is static content, its PR preview build
catches what staging would, and skipping it saves a CloudFront distribution and
an SSR Lambda. `app` and `api` keep theirs — they have state, auth, and
migrations worth rehearsing against.

**Flat `staging-app` beats nested `app.staging` — and this is not cosmetic.**
An ACM wildcard covers exactly one label: `*.insolvia.ai` matches
`staging-app.insolvia.ai` but **does not** match `app.staging.insolvia.ai`.
Nested naming would force a second wildcard cert (`*.staging.insolvia.ai`), a
second DNS validation cycle, and more moving parts in the shared env. The flat
scheme means the cert already authored in `infra/envs/shared/main.tf` covers
every host in the table above with zero changes.

**This renames the existing staging host.** `staging.insolvia.ai` is currently
hardcoded in three places that must change together, or staging deploys to a
host with no certificate route:

- `infra/envs/staging/variables.tf` — the `subdomain` default
- `infra/envs/staging/terraform.tfvars.example`
- `apps/insolvia_app/lib/src/config/environment.dart` — the `host` getter

Nothing is deployed yet, so this is a free rename today and an annoying one
later. It's issue 1.15.

### D3 — the marketing site is React, not Flutter

**Flutter web cannot be server-rendered.** It compiles to CanvasKit/Skwasm and
paints into a `<canvas>`. There is no DOM tree to serialize, so there is no
`renderToString` equivalent for a Lambda to call. The `--web-renderer html`
mode that emitted real DOM was deprecated and removed in Flutter 3.29, so that
escape hatch is gone too. Separately, CanvasKit ships ~1.5–2 MB before first
paint — a direct Core Web Vitals penalty on the one page where bounce rate is
the entire game.

So we mirror what `andreas-services/website` already proves:
`react-router.config.ts` with `ssr: true`, React Router v7 in framework mode,
deployed as a Docker Lambda behind CloudFront with hashed client assets on S3.
That pattern works precisely because React produces HTML strings on the server.

**On `ssr: true` vs `prerender`:** for ~8 static marketing pages, static
prerendering to S3 would technically be sufficient and would cut the Lambda
entirely. But RR7 flips between the two with essentially one config line, the
SSR deploy pipeline is directly copyable from `website/`, and server-side form
handling for the waitlist works out of the box. **Recommendation: start at
`ssr: true`** to match the reference implementation, and drop to prerender later
if the Lambda proves to be dead weight. This is a cheap, reversible call — not
worth agonising over.

One thing to carry across: `website/frontend/react-router.config.ts` documents a
real trap — RR7's single-fetch actions are CSRF-guarded on `Origin`, and behind
CloudFront → API Gateway the Lambda sees the API Gateway host rather than the
public one, so POST actions get rejected until the public hosts are listed in
`allowedActionOrigins`. Budget for hitting this.

### D4 — the design system serves both targets

`andreas-services/design-system` (`@ansavva/design-system`) is the model: Base UI
headless primitives + Tailwind v4, tokens declared as a `@theme` block of CSS
custom properties in `src/styles/theme.css`, built with tsup to ESM/CJS/`.d.ts`,
documented in Storybook, tested with Vitest, published to GitHub Packages. Its
own CLAUDE.md calls it "a deliberate exception to the *services share no code*
rule" — which is exactly the posture Insolvia's design system already has.

The important design point in that package: apps brand themselves by overriding
**semantic** tokens (`--color-primary`, `--color-accent`, `--color-ink`) rather
than raw palette names. That indirection is what makes one token set drivable
from two stacks.

**The seam: one token source, two renderings.** Today Insolvia's tokens are
hand-written Dart constants (`InsolviaPalette`, `InsolviaSpacing`,
`InsolviaRadii`, `InsolviaTypography`). The proposal is to move the source of
truth to a neutral `tokens.json` and generate *both* the Dart files and the
Tailwind `@theme` CSS from it, with both generated artifacts committed and a CI
check that fails if regeneration produces a diff.

Neutral-source rather than Dart-primary is deliberate: neither stack should own
the other's brand, and a JSON source keeps descriptions attached to tokens so
the generated Dart keeps its doc comments.

**The honest cost — and it is real.** Components *cannot* be shared. A Flutter
`AppButton` and a React `<Button>` are two implementations of one design, kept
in sync by discipline. Visual drift between parallel component libraries is the
classic failure mode of dual-platform design systems, and nothing in the
tooling prevents it.

**What contains that risk:** the React library exists *only* to serve the
marketing site. `app.insolvia.ai` and the desktop app are Flutter and stay
Flutter. So the React set should stay deliberately tiny — roughly six components
(Button, Card, NavBar, Footer, Accordion for FAQ, Input/Field for the waitlist),
not a port of all forty Base UI wrappers from `andreas-services`. A small
surface is a small drift problem. **This scope limit should be written into
CLAUDE.md**, or it will quietly expand.

---

### D8 — desktop is kept in the back pocket

We push customers to the web app. Both desktop targets are still built on
Flutter and installable from an untrusted source, but they are **not promoted**
and no code-signing certificates are bought yet. Desktop is the answer if
attorneys refuse to leave the desktop habit — held ready, not led with.

This matches `business-plan.html` §1, which already frames the wedge as
*seamlessness*, with a native desktop option for offline keyboard-driven
drafting rather than as the pitch itself.

**⚠️ It does not match the root `CLAUDE.md`**, whose opening paragraph says the
wedge *is* "meeting desktop-loyal attorneys where they are." A future session
reading only CLAUDE.md would over-invest in desktop. **CLAUDE.md should be
updated to match** — that's issue 4.12.

**What this decision buys:**
- Windows and Apple certificates come off the day-one procurement list, removing
  the longest lead time in the plan.
- Milestone 4 shrinks to the web app plus two unpromoted desktop builds.
- The Flutter bet is preserved intact — the desktop targets stay green in CI, so
  reversing this is a marketing decision plus certificate procurement, not an
  engineering rebuild. That optionality is the whole point of the Flutter choice
  and it costs almost nothing to keep.

**What to watch:** unpromoted targets rot quietly. If nobody runs the desktop
builds, they break and nobody notices until the moment we need them — which will
be the moment a prospect demands desktop. Issue 4.8 keeps both in CI for exactly
this reason; it should not be dropped as dead weight.

---

## Sequencing — what blocks what

Two long-lead items gate almost everything, and both are *waiting*, not
*working*. Start them on day one.

```
DAY-ONE PROCUREMENT (waiting, not working — start together)
  [.ai registration]──┐
  [SES prod access]───┤       ┌─▶ M4 web app (+ unpromoted desktop) ─┐
  [MyCase Advanced]─┐ │       │                                      ├──▶ M6 mailer
                    │ ├─▶ DNS live ─▶ ACM issued ───────────────────┼─▶ M5 api ─┘
                    │ └─▶ M1 email ───────────────────────────────┘
                    │
                    └─▶ M0 MyCase spike ──▶ go/no-go on the whole thesis

  M2 design system (React) ──▶ M3 www
```

Procurement status — two items remain:

| Item | Lead time | Blocks | Status |
|---|---|---|---|
| `.ai` registration | — | everything with a hostname | ✅ **Done** — Gandi, NS delegated to Route53 |
| MyCase Advanced tier (~$89/mo) | days | M0 | ⏳ **Only outstanding blocker — start today** |
| SES production access | ~24h+ | replying as `@insolvia.ai` | 🔜 **Deliberately deferred to issue 6.8** — submitted last, with the site and bounce handling already in place |

**Deferred by D8:** Windows code-signing certificate and Apple Developer
account. These were the two longest lead times in the plan; deprioritising
desktop distribution removes both. Note that reversing D8 reintroduces a
multi-week Windows validation window — promoting desktop is a decision with
weeks of lead time, not a switch.

**M0 and M2 need none of it.** The MyCase spike (once credentialed) and the
React design system are both fully parallel to the DNS wait — that's where the
engineering time goes while the queues clear.

- **`.ai` registration is not self-service in AWS.** Route53 Domains does not
  sell `.ai`; it must be registered at a third-party registrar (101domain,
  Porkbun, Namecheap) and the NS records delegated to the Route53 zone.
  `infra/envs/shared/main.tf` already carries a comment acknowledging this is
  blocked. `.ai` is priced per *two* years and is not cheap — budget for it.
- **SES starts in sandbox.** A fresh AWS account can only send to verified
  addresses, capped at 200/day. Production access is a support request with a
  ~24h turnaround that *can be rejected* and need resubmitting. It blocks both
  Milestone 1 and Milestone 6. File it immediately — it costs nothing to have it
  approved and unused.
- **Milestone 2 is not blocked by DNS.** The design system can be built while
  the domain and SES requests are in flight. Good parallel work for the wait.

---

## Milestone 0 · MyCase API spike

**Why this is first.** The business plan's entire go-to-market rests on one
untested assumption: that the MyCase API can carry the integration we're
promising. §3 calls distribution "the plan's keystone"; §7 lists native
integration as the wedge Best Case "structurally cannot copy"; §10 names
platform dependency as a *High* risk. If the API can't read and write what we
need, the wedge evaporates — and that is worth knowing before five milestones of
foundation get built around it, not after.

It is also cheap and fully parallel: a throwaway script against someone else's
API, with no dependency on our DNS, our infra, or our design system. It fills
the dead time while `.ai` registration and SES production access are pending.

**Outcome:** a written go/no-go on the integration thesis, backed by a real
authenticated round-trip.

| # | Issue | Notes |
|---|---|---|
| 0.1 | Obtain MyCase API access | Requires the **Advanced tier (~$89/mo)** per business-plan §3. Procurement lead time — start immediately. |
| 0.2 | Authenticate and complete one read round-trip | Pull a real case/contact record. Proves credentials, scopes, and rate limits. |
| 0.3 | Complete one **write** round-trip | The riskier half. Many practice-management APIs are generous on read and thin on write; the no-double-entry promise dies without write. |
| 0.4 | Map MyCase's data model → bankruptcy intake fields | Where does a debtor's income, creditors, and asset data actually live? Gaps here shape the whole intake milestone. |
| 0.5 | Document rate limits, pagination, webhook/sync options | Determines whether sync is push, poll, or on-demand — an architectural fork for the intake milestone. |
| 0.6 | Investigate App Bar listing requirements | Review process, technical bar, timeline. This is the discovery channel; find out now what it demands. |
| 0.7 | Confirm the commercial relationship | §10 mitigation calls for a formal partnership rather than warm intros, plus a second channel (direct/NACBA). The plan says this should be confirmed before the channel is leaned on in a raise. |
| 0.8 | **Write the go/no-go**, incl. what changes if MyCase says no | If the answer is no, the plan needs restructuring around a different wedge — far better discovered here. |

---

## Milestone 1 · Domain & Email

**Why first:** you called this the most important thing right now, and it's
genuinely independent — it needs no app, no API, no marketing site. It also
unblocks the ACM cert every other surface depends on.

**Outcome:** `insolvia.ai` resolves, mail to `hello@insolvia.ai` lands in your
Gmail, and you can reply *as* that address.

**Reference implementation:** `andreas-services/humbugg/infra/modules/support_forwarding/`
(SES receipt rule set → S3 → Lambda → re-send via SES with original sender as
`Reply-To`), the Lambda source at `humbugg/support-forwarding/src/support_forwarder/handler.py`,
and the Gmail runbook at `humbugg/docs/support-forwarding.md:98`. This is a
port, not a design exercise.

| # | Issue | Notes |
|---|---|---|
| ~~1.1~~ | ~~Register `insolvia.ai`~~ — ✅ **DONE** | Gandi, created 2026-07-21, expires 2028-07-21 (two-year `.ai` term). |
| ~~1.4~~ | ~~Delegate registrar NS → Route53~~ — ✅ **DONE** | Gandi delegates to zone `Z01038711J6IZ68FD6ZDW`. |
| 1.0 | **Create the Terraform state bucket** — `insolvia-terraform-state` | Bootstrap step 1 of `docs/AWS_SETUP.md`, **not yet done**. `terraform init` cannot run without it — every `backend.tf` in the repo points at this bucket. This is now the first action in the whole plan. |
| 1.1b | **⚠️ `terraform import` the existing hosted zone before the first apply** | **Critical — see the trap below.** `terraform import aws_route53_zone.main Z01038711J6IZ68FD6ZDW`. Skipping this silently breaks DNS *and* hangs the certificate. |
| 1.2 | Verify the destination Gmail address as an SES identity | Sandbox requires it. Enough to make inbound forwarding fully testable — see the sandbox note below. **SES production access is deliberately deferred to issue 6.8.** |
| 1.3 | Apply `infra/envs/shared` — wildcard ACM, OIDC provider, deploy role | Confirmed **not applied**: no state bucket, no OIDC provider, no `github-actions-insolvia` role, no ACM certificate. Only the zone exists. Blocked by 1.0 + 1.1b. |
| 1.3b | Confirm the wildcard ACM cert reaches `ISSUED` | `infra/envs/staging/main.tf` looks the cert up with `statuses = ["ISSUED"]`, so every downstream env fails at plan time with a misleading "no matching certificate" error until this is true. |
| 1.3c | Wire `AWS_ROLE_ARN` secret + `DEPLOY_ENABLED` variable | Step 4 of `AWS_SETUP.md`, not yet done. |
| 1.3d | Update `docs/AWS_SETUP.md` | Its status banner still says the domain is the current blocker. It isn't. Add the state-bucket and zone-import steps while you're there. |
| 1.5 | New `infra/modules/email`: SES domain identity, DKIM, custom MAIL FROM | Port from `humbugg/infra/modules/email/`. |
| 1.6 | SPF, DMARC, and MX records for `insolvia.ai` | MX → `inbound-smtp.us-east-1.amazonaws.com`. Start DMARC at `p=none`, tighten later. |
| 1.7 | Port `support_forwarding` → `infra/modules/inbound_forwarding` | Rule set, S3 inbound bucket w/ lifecycle expiry, Lambda, IAM. |
| 1.8 | Port forwarder Lambda source → `services/inbound_forwarder/` | Rename the `X-Humbugg-Forwarded` loop marker → `X-Insolvia-Forwarded`. Keep the safety gates (spam/virus verdict, loop detection, allowed-recipient check) and the rebuild-the-message-from-scratch approach — do not relay untrusted headers. |
| 1.9 | SSM SecureString for the private forward-to destination | Value injected once via `TF_VAR_`, then `ignore_changes`. Never committed. |
| 1.10 | DLQ + CloudWatch alarm on the forwarder | Misconfiguration must surface on an alarm, not silently drop a client's mail. |
| 1.11 | Address map — **confirmed** | `hello@` (general), `support@` (product), `no-reply@` (transactional sender), `security@` (disclosure). All forward to Gmail for now; `no-reply@` is send-only and should be excluded from the forwarder's allowed-recipient list. |
| 1.12 | SES SMTP credentials + Gmail "Send mail as" runbook | Write as `docs/email-setup.md`. This is the "reply from the address" half. Port `humbugg/docs/support-forwarding.md`. |
| 1.13 | Un-gate the deploy workflows | CLAUDE.md notes deploys are gated OFF pending DNS. This is the flip. |
| 1.14 | Document the Google Workspace migration path | When you move to Workspace, MX flips to Google and inbound forwarding must be retired in the same change or mail bounces. Write it down now while the context is fresh. |
| 1.15 | Rename `staging.insolvia.ai` → `staging-app.insolvia.ai` | Three files, one commit: `infra/envs/staging/variables.tf`, `terraform.tfvars.example`, `environment.dart`. Free now, annoying once anything is deployed. See D2. |

### Working in the SES sandbox — what does and doesn't function

**Decision:** we stay in the SES sandbox for now and request production access
**last** (issue 6.8), once there's a live site, working bounce/complaint
handling, and a privacy policy for AWS to review. Submitting cold invites a
rejection, and rejections make resubmission harder.

What that means in practice:

| Capability | In sandbox |
|---|---|
| Inbound receipt rules (SES → S3 → Lambda) | ✅ Unaffected — sandbox is an *outbound* restriction |
| Forwarding inbound mail to your Gmail | ✅ Works, once that Gmail address is a verified SES identity (issue 1.2) |
| Sending to any other address | ❌ Blocked unless that exact address is verified |
| **Replying as `hello@insolvia.ai` via SES SMTP** | ❌ **Blocked** — the recipient isn't verified |
| Sending volume | 200/day, 1 msg/sec |

**The catch to plan around:** M1's outcome is "mail lands in Gmail **and** you can
reply as that address." Sandbox delivers the first half only. Inbound works and
is fully testable; the reply-from-our-domain half stays dark until 6.8 lands.
Until then, replies go from your own Gmail address.

That's an acceptable trade while there's no inbound volume — but it means
**issue 6.8 should not slip indefinitely**, or the mailbox stays half-built.

### ⚠️ The duplicate-hosted-zone trap — read before running `terraform apply`

**Verified state of the Insolvia AWS account (521762924626), 2026-07-21:**

| Resource | State |
|---|---|
| Hosted zone `insolvia.ai` | ✅ Exists — `Z01038711J6IZ68FD6ZDW`, **2 records (NS + SOA only)** |
| Gandi NS delegation | ✅ Points at that zone |
| S3 bucket `insolvia-terraform-state` | ❌ **Does not exist** |
| GitHub OIDC provider | ❌ Absent |
| IAM role `github-actions-insolvia` | ❌ Absent |
| ACM certificate | ❌ None in us-east-1 |

So the zone was created **outside Terraform** — there is no state bucket, so no
state file can exist. `infra/envs/shared` has never been applied.

**Why that's dangerous.** `infra/envs/shared/main.tf` declares
`resource "aws_route53_zone" "main"`. With empty state, `terraform apply` will
**create a second hosted zone for `insolvia.ai`** — Route53 permits duplicates
and simply assigns a different nameserver set. The consequences are quiet and
confusing:

1. Gandi still delegates to the *original* zone, so the new Terraform-managed
   zone is authoritative for nothing.
2. The ACM DNS-validation records get written into the new, unreferenced zone,
   so validation never completes. `aws_acm_certificate_validation` hangs until
   it times out.
3. The failure surfaces as a certificate timeout — with nothing pointing at the
   actual cause, which is two zones.
4. You pay for both.

**The fix — import, don't recreate:**

```bash
aws s3api create-bucket --bucket insolvia-terraform-state --region us-east-1   # issue 1.0
cd infra/envs/shared
terraform init
terraform import aws_route53_zone.main Z01038711J6IZ68FD6ZDW                   # issue 1.1b
terraform plan   # MUST show no zone creation, and must not destroy the zone
terraform apply
```

Importing keeps Gandi's existing delegation valid, so no registrar change is
needed. The alternative — delete the manual zone and let Terraform create a
fresh one — also works but means re-delegating at Gandi, and is only harmless
because nothing is live yet.

**Do not skip the `terraform plan` check.** A plan that proposes creating a
`aws_route53_zone` means the import didn't take, and applying it is the failure
above.

---

## Milestone 2 · Design system — React target

**Outcome:** one token source of truth driving both a Flutter package and a
React package, so the marketing site is on-brand by construction rather than by
eyeballing.

**Reference implementation:** `andreas-services/design-system` — read its
`CLAUDE.md` first; the component pattern, the `cn()` merge helper, the
`@source` directive consumers need, and the tsup build config all transfer
directly.

| # | Issue | Notes |
|---|---|---|
| 2.1 | Extract tokens to a neutral `packages/insolvia_tokens/tokens.json` | Carries descriptions so generated Dart keeps its doc comments. |
| 2.2 | Generator: `tokens.json` → Dart token files **+** Tailwind v4 `@theme` CSS | Both outputs committed; CI check fails on drift. A small script beats pulling in Style Dictionary for this token count. |
| 2.3 | Map the Insolvia palette onto semantic tokens | `ink`/`brass`/`paper` → `--color-primary`/`--color-accent`/`--color-bg`, etc. Follow the semantic-indirection pattern from `@ansavva/design-system`, including `[data-theme='dark']`. |
| 2.4 | Scaffold `packages/insolvia_design_system_react/` as `@insolvia/design-system` | tsup → ESM + CJS + `.d.ts`, `theme.css` copied verbatim to `dist/`. Tailwind v4 + Base UI + `cn()`. Excluded from the pub workspace. |
| 2.5 | Build **only** the marketing components | Button, Card, NavBar, Footer, Accordion, Input/Field. Explicitly *not* a port of all 40 wrappers. |
| 2.6 | Storybook + Vitest/Testing Library | Mirrors the Flutter package's "every exported component has a widget test" rule. |
| 2.7 | Publish to GitHub Packages under the `@insolvia` scope | `.npmrc` with `${NODE_AUTH_TOKEN}`; CI uses `secrets.GITHUB_TOKEN`. Note the `website/` trick of bundling the design system into the SSR build via `ssr.noExternal` so the runtime Lambda needs no registry token. |
| 2.8 | Workflow `design-system-react-pr.yml` | Alongside the existing `design-system-pr.yml`. |
| 2.9 | Write the parity discipline into CLAUDE.md | The scope limit in D4, plus: tokens are never hand-edited in either generated file. |

---

## Milestone 3 · Marketing site (`www.insolvia.ai`)

**Outcome:** a fast, crawlable, on-brand marketing site at `www.insolvia.ai`,
with the apex redirecting to it.

**Depends on Milestone 2.**

| # | Issue | Notes |
|---|---|---|
| 3.1 | Scaffold `apps/insolvia_marketing/` — React Router v7 framework mode | Copy the shape of `andreas-services/website/frontend/`. Own `package.json`; excluded from the pub workspace. |
| 3.2 | Wire the design system + Tailwind entrypoint | `@import "tailwindcss"` → `@import "@insolvia/design-system/theme.css"` → `@source` the dist. Missing the `@source` line is the classic "why are my styles gone" bug. |
| 3.3 | Content pass — positioning, JTBD, competitive framing | Source from `business-plan.html` §6 (jobs-to-be-done) and §7 (positioning). Do not invent new claims; the plan's figures are sourced and shouldn't drift. |
| 3.4 | SEO baseline | Per-route `<title>`/meta/OG, `sitemap.xml`, `robots.txt`, JSON-LD `Organization`. Explicitly allow GPTBot/ClaudeBot/PerplexityBot — inbound increasingly arrives through them. |
| 3.5 | Infra: `www` + apex hosting | CloudFront + S3 assets + SSR Lambda, following `website/infra/modules/{hosting,compute}`. Apex → `www` 301. |
| 3.6 | Set `allowedActionOrigins` for `www` + apex | The CSRF trap documented in D3. Cheaper to do now than to debug later. |
| 3.7 | Workflows: `marketing-pr.yml`, `marketing-staging.yml`, `marketing-prod.yml` | Follow existing `app-*.yml` shape and the cache-control rules in CLAUDE.md. Staging serves `staging-www.insolvia.ai`. |
| 3.10 | `noindex` on every non-prod host | `staging-www`, `staging-app`, `staging-api`. A crawlable staging copy of the marketing site competes with prod for its own keywords — a genuinely damaging and easily-missed SEO own-goal. |
| 3.8 | Lighthouse / Core Web Vitals budget in CI | The whole reason we're not using Flutter here — enforce it or the reasoning rots. |
| 3.9 | Waitlist / contact capture | **Soft-depends on Milestone 5.** Ship storing to DynamoDB directly from the SSR action first (this is what `website/` does — no SES, intake straight to DynamoDB), rather than blocking on the API. |

---

## Milestone 4 · App shell (`app.insolvia.ai` + desktop)

**Outcome:** infrastructure proven end-to-end for both delivery targets, with a
deliberately minimal home page. Per the brief: *"we can just put up a really
simple home page for right now — we will build out the app in tickets
separately."*

| # | Issue | Notes |
|---|---|---|
| 4.1 | Verify `staging-app.insolvia.ai` deploys and serves | The `app-staging.yml` workflow exists; this is its first real run. Depends on the rename in 1.15. |
| 4.2 | Prod hosting for `app.insolvia.ai` + `app-prod.yml` verification | `workflow_dispatch`-gated behind the `insolvia-production` environment, per CLAUDE.md. |
| 4.3 | CloudFront SPA routing: 403/404 → `/index.html` | go_router deep links 404 without this. |
| 4.4 | Minimal signed-in shell home page | Uses `AppScaffold` + `BrandWordmark` from the Flutter design system. Intentionally thin. |
| 4.5 | Add the `windows/` Flutter target | Only `web/` and `macos/` are checked in today. |
| 4.6 | Produce unsigned `setup.exe` (Windows) and `.dmg` (macOS) artifacts | Distribution is a download link — no store, no approval. Not linked from `www` per D8. |
| 4.7 | CI: add `windows-latest` + `macos-latest` build jobs | Flutter desktop must be built on its target OS — no cross-compilation. Both runners bill at a multiplier over Linux minutes. |
| 4.8 | **Keep both desktop targets green in CI** | Load-bearing under D8: an unpromoted target rots silently and breaks exactly when a prospect finally demands desktop. This is what preserves the option. |
| 4.9 | Desktop auto-update — **deferred**, but write down the decision | Not needed while distribution is hand-held and few. Revisit before *any* firm depends on a desktop build day-to-day; retrofitting an updater is far worse than building one. macOS (Sparkle-style) and Windows (MSIX/installer) paths differ. |
| 4.10 | Artifact hosting for the unsigned builds | S3 + CloudFront, unlinked. Enough to hand someone a URL. |
| 4.11 | Write the install walkthrough for unsigned builds | Needed on macOS especially — see below. Screenshots of the Gatekeeper flow, not prose. |
| 4.12 | **Update root `CLAUDE.md` to match D8** | Its opening still frames desktop as *the* wedge. Left alone, future sessions will over-invest in it. |

**Deferred out of this milestone by D8:** Apple Developer account, Windows OV/EV
certificate, notarization, and the public download link. See below for what that
costs and what it will take to reverse.

### Desktop signing — deferred, and what "unsigned" actually costs

Signing is **deferred** under D8: we build both desktop targets and distribute
them unsigned, to people we're talking to directly. No certificate procurement
now. But the two platforms are not equally forgiving, and the difference should
be understood before anyone hands a build to a firm.

**Windows — a click-through.** A downloaded `setup.exe` carries Mark of the Web,
so SmartScreen shows a full-screen *"Windows protected your PC"* dialog whose
only visible button is **Don't run**. Getting past it means "More info" → "Run
anyway". Ugly, but a single sentence of guidance on a call.

**macOS — not a click-through.** Gatekeeper genuinely refuses to launch an
unsigned or unnotarized app; the DMG's quarantine attribute is enforced, not
warned about. The user must attempt to open it, be blocked, then go to
**System Settings → Privacy & Security → Open Anyway** and confirm. Apple has
been tightening this: the old right-click → Open shortcut no longer works on
recent macOS versions. In bad cases the app reports itself as "damaged," and the
fix is `xattr -d com.apple.quarantine` in Terminal — which is not something to
ask a bankruptcy attorney to run.

**Practical consequence:** unsigned macOS distribution is viable only with
hand-holding and a written walkthrough. It is not a self-serve path in any form.
Plan a short install guide (issue 4.11) rather than assuming a DMG is
self-explanatory.

**When we do promote desktop, the procurement facts are:**

- Since June 2023, Windows OV code-signing keys must live in certified hardware
  (token or cloud HSM) — you cannot drop a `.pfx` into a GitHub secret.
  *Azure Trusted Signing* is ~$10/mo and CI-friendly but has generally applied a
  three-year business-history bar, so assume a newly-formed Insolvia is
  ineligible until checked. A traditional OV cert runs ~$200–600/yr plus token
  or HSM, with validation taking one to several weeks. EV costs more but grants
  SmartScreen reputation immediately, which matters on IT-managed firm machines.
- macOS requires an Apple Developer account ($99/yr) plus notarization.

None of that is on the critical path today — but the Windows validation window
is long enough that promoting desktop is a *decision with weeks of lead time*,
not a switch to flip. Worth remembering when the moment comes.

---

## Milestone 5 · API (`api.insolvia.ai`)

**Outcome:** the trust boundary exists. No client — web or desktop — ever holds
AWS credentials or talks to an AWS service directly.

**Reference implementation:** `andreas-services/mailer/src/mailer/` for the
layered layout (`core/` contracts with no framework deps → `api/` Flask
blueprints → `adapters/` AWS + in-memory + Mailpit → `entrypoints/` Lambda
handlers and the dev server), including its architecture test that enforces the
dependency direction. That test is worth porting on day one — it's what stops
the layering rotting.

| # | Issue | Notes |
|---|---|---|
| 5.1 | Scaffold `services/api/` — Flask + Mangum, mirroring mailer's layout | Port the architecture test with it. |
| 5.2 | Infra: API Gateway HTTP API + Lambda (Docker/ECR) + CloudFront + custom domain | Note the CLAUDE.md rule: `lifecycle { ignore_changes = [image_uri, environment] }`, and build-and-push the image *before* Terraform applies, or fresh-account deploys deadlock. |
| 5.3 | Stand up **both** `staging-api` and `api` environments | Per CLAUDE.md, each env is its own `infra/envs/<env>/` directory with its own state key — never Terraform workspaces. Separate ECR tags, Cognito pools, and DynamoDB tables per env; staging must never read prod data. |
| 5.4 | Point the app's env config at the right API host per build | `environment.dart` gains an `apiBaseUrl` alongside `host`, resolved from `INSOLVIA_ENV`. A staging desktop build hitting prod is the failure mode to design out. |
| 5.5 | Auth: Cognito user pool + app clients | Two flows: web PKCE, and **desktop loopback-redirect PKCE** — these differ, and the desktop one is the awkward one. Separate pools per environment. |
| 5.6 | Dart API client package `packages/insolvia_api_client/` | Shared by web and desktop builds; generated from an OpenAPI spec if practical. |
| 5.7 | Write down the trust boundary as an ADR | The "client stays dumb" rule needs documenting, or it erodes the first time something is easier to do client-side. |
| 5.8 | CORS allowlist per environment | `api` accepts `app.insolvia.ai`; `staging-api` accepts `staging-app.insolvia.ai` + localhost. Desktop sends no browser `Origin` — don't let a permissive desktop path widen the web policy. |
| 5.9 | Structured JSON logging, `/health`, CloudWatch alarms | |
| 5.10 | Config + secrets via SSM, namespaced per env | `/insolvia/<env>/...`. |
| 5.11 | Workflows: `api-pr.yml`, `api-staging.yml`, `api-prod.yml` | `staging` on push to `main`; `prod` `workflow_dispatch` behind the `insolvia-production` environment, per CLAUDE.md. |
| 5.12 | Local dev via `docker compose` | Mirror `mailer/docker-compose.yml`. |

---

## Milestone 6 · Transactional email (mailer service)

**Outcome:** the app can send product email — welcome, verification, password
reset — durably and with feedback handling.

**Reference implementation:** port `andreas-services/mailer/` wholesale. Its
flow: SigV4-signed `POST /v1/services/<service>/messages` → ingress Lambda
verifies the caller role → manifest to S3 + pointer to SQS → sender Lambda
validates content, suppression, kill switch → SES → normalized feedback events
back to a status queue.

That design is more than an MVP strictly needs. Recommendation: **port it whole
anyway.** Suppression handling and bounce/complaint feedback are not optional
once you're sending to real attorneys — SES will throttle or suspend an account
with a bad complaint rate, and retrofitting suppression afterwards is painful.

| # | Issue | Notes |
|---|---|---|
| 6.1 | Port `mailer/` → `services/mailer/` | Strip humbugg-specific configuration sets. |
| 6.2 | Insolvia service registry + IAM role mapping | Replace humbugg/scout entries. |
| 6.3 | Port the mailer infra module | SQS, S3 manifests, sender + feedback Lambdas, suppression, kill switch. |
| 6.4 | API → mailer integration over SigV4 | |
| 6.5 | Mailpit local dev loop | Already in the upstream `docker-compose.yml`. |
| 6.6 | Initial templates: welcome, email verification, password reset | |
| 6.7 | Bounce/complaint monitoring + alarms | Protects SES reputation — and is one of the things AWS looks for in 6.8. |
| 6.8 | **Request SES production access — the last thing we do** | Deliberately deferred so the request is made with everything AWS reviews already in place: a live `www`, working bounce/complaint handling (6.7), suppression (6.3), an unsubscribe path, and a published privacy policy. Until this lands we cannot reply as `@insolvia.ai` (see the sandbox note in M1) — so don't let it slip forever. |

---

## Beyond foundation — stubs only

To be fleshed out in their own sessions, not now.

| Milestone | Business plan | One-line scope |
|---|---|---|
| `Product · Intake & AI extraction` | M2 / P1 | Claude extracting credit reports and pay stubs; intake behind auth. **Shape depends on Milestone 0's findings** — particularly whether MyCase sync is push, poll, or on-demand. |
| `Product · Forms & petition engine` | M3 / P2 | Deterministic, versioned forms; Chapter 7 packet; AI review agent. |
| `Product · Means test` | P3 | Rule-based, with the IRS/Census refresh pipeline from the regulatory register. |

**Worth flagging now:** `regulatory-source-register.html` describes a
maintenance calendar (§522 dollar amounts every 3 years, Census median income
2–4×/yr, IRS standards periodically). Those are *scheduled data pipelines with
effective-date fields*, not one-time loads, and they need infrastructure of
their own. Not foundation work, but they shouldn't be a surprise when the forms
engine lands.

---

## Resolved questions

All open questions from rev 2 are answered and folded into the plan above.

| Question | Answer | Where it landed |
|---|---|---|
| Pull the MyCase spike forward? | **Yes** | New Milestone 0, running parallel to the DNS/SES wait |
| Windows at MVP, or macOS only? | **Both** | Milestone 4 issues 4.6–4.8 + the code-signing warning |
| Address map | **Confirmed** | Issue 1.11 |
| npm scope `@insolvia`? | **Yes** | Issue 2.7 |
| Staging for marketing? | **No** | D2 table; PR previews only |

## Remaining risks worth watching

Not questions — just the things most likely to bite, in order:

1. **The duplicate-hosted-zone trap (M1).** Highest-probability concrete failure
   in the plan, because the natural next action — `terraform apply` on shared —
   triggers it. See the boxed section in Milestone 1. Import first.
2. **MyCase write access (M0).** The single largest unknown. Read access is
   likely; write is what the no-double-entry promise depends on.
3. **Desktop bit-rot (D8).** Unpromoted targets break silently. If the Windows
   and macOS builds fall out of CI, they'll be broken at the exact moment a
   prospect demands desktop — destroying the optionality this decision was
   meant to preserve. Issue 4.8 is the guard; don't let it get trimmed.
4. **Web-first is a bet on attorney behaviour.** The business plan describes
   this market as desktop-loyal. Pushing web is right, but it's an assumption
   worth testing explicitly with the design-partner firm rather than
   discovering late — and it's cheap to test, because the desktop build exists.
5. **SES production access deferred too long (6.8).** Deferring it is correct —
   the request is stronger with a live site and real bounce handling. The risk is
   the opposite one: while it's outstanding we can receive mail at
   `@insolvia.ai` but cannot reply from it, so the mailbox is half-built. Set a
   date rather than leaving it open-ended.
6. **Design-system parity drift.** Contained by the six-component scope limit in
   D4 — which only holds if issue 2.9 actually writes it into CLAUDE.md.

---

## GitHub

Access confirmed 2026-07-21 — `gh` has the `project` scope, and the **MVP**
project exists (`PVT_kwDOEi5yWs4BeBkB`). The repo currently has **no milestones
and no issues**, so creation is a clean slate.

Awaiting go-ahead to create the seven milestones and their issues, and add them
to the MVP board.
