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
| D1 | Domain is **insolvia.ai**, not `.com` | Repo, CLAUDE.md, shared ACM wildcard, and the app's environment config are all already built on it. `.com` would mean re-authoring the shared env for no gain. |
| D2 | Subdomain map — see the table below; every environment gets its own host, staging included — **marketing now included too** | Staging needs a full parallel stack, not just an app. Flat `staging-*` naming (not `*.staging`) is load-bearing — see D2 below, which also records why marketing's original "no staging" carve-out was reversed. |
| D3 | Marketing site is **React Router v7** | Flutter web cannot be server-rendered or crawled. See D3 below. **Still stands under D9** — marketing does not move, and D9 records the measured reason. |
| D4 | The **design system becomes dual-target**, over one shared token source | Originally a Flutter package + a React package (a consequence of D3). **Revised by D9:** the Flutter package is deleted; the two targets are now the React design system and the app's own React Native one, still over one `tokens.json`. **Revised again:** both targets now live in one cross-platform package, `packages/insolvia_design_system`. See D4 below. |
| D5 | **The API is required for MVP**, not deferred | The desktop app is a fat client on an attorney's machine. It cannot hold AWS credentials. Per `docs/regulatory-source-register.html`, we handle SSNs and full financials under GLBA Safeguards — the trust boundary has to live server-side. |
| D6 | Backend stack is **Python + Flask + Mangum on Lambda** | Flask 3.1.2, Mangum 0.17, gunicorn — the established house pattern. |
| D7 | Human email and product email are **separate milestones, and now separate providers** | Human mailboxes were urgent and have no app dependency; the mailer service depends on the API. Bundling them would block the urgent thing behind the slow thing. Originally SES→Gmail forwarding; superseded by **Google Workspace for inbound, SES for outbound `no-reply@`** — only one apex MX set exists, so this is exclusive, not additive. See [`EMAIL_SETUP.md`](EMAIL_SETUP.md). |
| D8 | ~~**Web is the promoted path. Desktop is built but not promoted**~~ | **Superseded by D9.** Web is still the promoted path; desktop is no longer built. See D8 below — its reasoning is preserved, because D9 is a different answer to the same question. |
| D9 | **Flutter and Dart are replaced by React Native on Expo**, free tier only, bare primitives, no component library; desktop deleted, mobile held open by `expo prebuild` | Supersedes D8. See D9 below and [ADR 0004](adr/0004-react-native-replaces-flutter.md), which carries the six-round spike measurements. |

### D2 — the subdomain map

| Surface | Production | Staging |
|---|---|---|
| Marketing | `www.insolvia.ai` (apex 301s here) | `staging-www.insolvia.ai` |
| Web app | `app.insolvia.ai` | `staging-app.insolvia.ai` |
| API | `api.insolvia.ai` | `staging-api.insolvia.ai` |
| Mailer | `mailer-api.insolvia.ai` | `staging-mailer-api.insolvia.ai` |

**Marketing originally had no staging environment. That is reversed (M6/6.8).**
The original reasoning — static content, a PR preview build catching what
staging would, one fewer CloudFront distribution and SSR Lambda — rested on a
PR preview build that was never actually built, so in practice the site had no
pre-production environment at all. Milestone 6 is what forced the issue: the
SES production-access request is reviewed against a live privacy policy and a
working unsubscribe path, and neither is something to first exercise on the
host that AWS is reviewing. Prod is separately parked offline
(`site_enabled = false`), which would have left nowhere at all to see them.

Two things make a second public copy safe rather than a liability:

- **It cannot be indexed.** `app/lib/seo.ts` allowlists exactly
  `www.insolvia.ai`; every other host ships `noindex` and a `Disallow: /`
  robots.txt (issue #48). `marketing-staging.yml`'s smoke check asserts both,
  because a staging copy that started ranking would compete with production
  for its own keywords.
- **It owns no apex.** A zone has exactly one apex and prod owns it, so
  `modules/marketing_site` takes `apex_domain = null` on staging and skips the
  alias, the A/AAAA records, and the 301 branch. Two environments both
  claiming `insolvia.ai` would collide on the CloudFront alias.

`app` and `api` keep their staging environments for the original reason — they
have state, auth, and migrations worth rehearsing against.

**Flat `staging-app` beats nested `app.staging` — and this is not cosmetic.**
An ACM wildcard covers exactly one label: `*.insolvia.ai` matches
`staging-app.insolvia.ai` but **does not** match `app.staging.insolvia.ai`.
Nested naming would force a second wildcard cert (`*.staging.insolvia.ai`), a
second DNS validation cycle, and more moving parts in the shared env. The flat
scheme means the cert already authored in `infra/envs/shared/main.tf` covers
every host in the table above with zero changes.

**This renamed the original staging host.** The old flat `staging` host was
hardcoded in three places that had to change together, or staging would deploy
to a host with no certificate route:

- `infra/envs/staging/variables.tf` — the `subdomain` default
- `infra/envs/staging/terraform.tfvars.example`
- the app's environment config — the `host` getter (then
  `lib/config/environment.dart`; now `src/config/environment.ts`, per D9)

Nothing is deployed yet, so this is a free rename today and an annoying one
later. It's issue 1.15.

### D3 — the marketing site is React, not Flutter

> **Annotation (D9, 2026-07-29).** This decision stands, and D9 widened rather
> than reversed it: the *app* has now moved to React Native too, so the split
> D3 created is no longer Flutter-vs-React but two React stacks — Expo/Metro
> for the app, React Router/Vite for the site. Marketing deliberately did
> **not** move onto the app's stack. It could: bare React Native for Web passes
> every gate. It would cost 2.3× the script weight (125 KB → 293 KB gzip) and
> most of the LCP headroom, on the one page whose entire job is SEO. The
> measurements are in [ADR 0004](adr/0004-react-native-replaces-flutter.md).
> Everything below about CanvasKit is now history — it explains why the site
> was never Flutter, not why it is React today.

**Flutter web cannot be server-rendered.** It compiles to CanvasKit/Skwasm and
paints into a `<canvas>`. There is no DOM tree to serialize, so there is no
`renderToString` equivalent for a Lambda to call. The `--web-renderer html`
mode that emitted real DOM was deprecated and removed in Flutter 3.29, so that
escape hatch is gone too. Separately, CanvasKit ships ~1.5–2 MB before first
paint — a direct Core Web Vitals penalty on the one page where bounce rate is
the entire game.

So the proven approach:
`react-router.config.ts` with `ssr: true`, React Router v7 in framework mode,
deployed as a Docker Lambda behind CloudFront with hashed client assets on S3.
That pattern works precisely because React produces HTML strings on the server.

**On `ssr: true` vs `prerender`:** for ~8 static marketing pages, static
prerendering to S3 would technically be sufficient and would cut the Lambda
entirely. But RR7 flips between the two with essentially one config line, and
server-side form handling for the waitlist works out of the box. **Recommendation:
start at `ssr: true`** and drop to prerender later if the Lambda proves to be dead
weight. This is a cheap, reversible call — not worth agonising over.

One trap to budget for: RR7's single-fetch actions are CSRF-guarded on `Origin`,
and behind CloudFront → API Gateway the Lambda sees the API Gateway host rather
than the public one, so POST actions get rejected until the public hosts are
listed in `allowedActionOrigins` in `react-router.config.ts`.

### D4 — the design system serves both targets

> **Revised again by the cross-platform cutover, 2026-07.** The two-consumer
> table below is the state D9 left, kept for the record. Since then both
> render targets moved into **one** package,
> `packages/insolvia_design_system` (a shared props module plus a `.web` and
> a `.native` leaf per component; `insolvia_design_system_react` is deleted).
> The current story is [`PACKAGE_PUBLISHING.md`](PACKAGE_PUBLISHING.md).

> **Revised by D9, 2026-07-29.** The *shape* of this decision survives intact —
> one neutral token source, generated into per-stack artifacts, never one stack
> owning the other's brand. What changed is the second target. There is no
> Flutter package any more (`packages/insolvia_design_system/` is deleted, and
> with it the Dart generator and the git-tag publish flow). The two consumers
> of `tokens.json` are now:
>
> | Target | Consumer | Generated artifact |
> |---|---|---|
> | Marketing site | `packages/insolvia_design_system_react/` (published) | Tailwind v4 `@theme` block, `theme.css` |
> | App | `apps/insolvia_app` — its own components, not a package | typed `tokens.ts`, read through `src/theme.ts` |
>
> Both ship from `packages/insolvia_tokens` as `@insolvia-ai/tokens`. The
> generator is now TypeScript rather than Dart and emits both. The app's
> design system is *not* published: it has exactly one consumer in this repo,
> so the registry boundary that earns its keep for the React package would be
> pure overhead here. **The dual-implementation cost below is unchanged** — two
> renderings of one design, kept in sync by discipline — and so is the
> containment: the React set stays capped at the marketing components, and the
> app's set stays small because ADR 0004 chose to own it rather than import a
> library.

The proven model: Base UI
headless primitives + Tailwind v4, tokens declared as a `@theme` block of CSS
custom properties in `src/styles/theme.css`, built with tsup to ESM/CJS/`.d.ts`,
documented in Storybook, tested with Vitest, published to GitHub Packages.
Treating a shared design system as "a deliberate exception to the *services
share no code* rule" is exactly the posture Insolvia's design system already has.

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
not a port of all forty Base UI wrappers. A small
surface is a small drift problem. **This scope limit should be written into
CLAUDE.md**, or it will quietly expand.

---

### D8 — desktop is kept in the back pocket — **superseded by D9**

> **Superseded by D9, 2026-07-29 — read this section anyway.** D9 did not
> decide that desktop was a bad idea; it decided that the *mechanism* below
> stopped being cheap. Everything here is the reasoning D9 had to answer, and
> the "what to watch" paragraph is the risk that actually materialised, from
> the other direction.

We push customers to the web app. Both desktop targets are still built on
Flutter and installable from an untrusted source, but they are **not promoted**
and no code-signing certificates are bought yet. Desktop is the answer if
attorneys refuse to leave the desktop habit — held ready, not led with.

This matches `business-plan.html` §1, which already frames the wedge as
*seamlessness*, with a native desktop option for offline keyboard-driven
drafting rather than as the pitch itself.

**Fixed in 4.12.** The root `CLAUDE.md` and `README.md` both used to open on
"meeting desktop-loyal attorneys where they are," which would have led a future
session reading only those files to over-invest in desktop. Both now open on
seamlessness and point here.

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

### D9 — Flutter and Dart are replaced by React Native on Expo

**Supersedes D8.** The full decision, the alternatives, and the six-round
styling spike that settled the UI layer are
[ADR 0004](adr/0004-react-native-replaces-flutter.md) — an ADR because this one
outlives the plan. What belongs here is the plan-level consequence: which
earlier decisions moved, and what the desktop trade actually was.

`apps/insolvia_app` is now an Expo app on **SDK 57**, pinned exact, in place of
the pinned Flutter `3.44.6`. No Dart remains anywhere in the repo — the Flutter
design system, the Dart API client half, the Dart token generator, the pub
workspace and Melos are all deleted. Five constraints ride along, and ADR 0004
argues each:

| | |
|---|---|
| Toolchain | Expo SDK 57 · Metro for the app, Vite for marketing · Expo Router, `web.output: "single"` |
| Billing | **Expo free tier only** — no EAS Build/Submit/Update/Hosting, no Expo account in CI. Enforced by a guard step in `app-pr.yml`, not by good intentions |
| UI | **No component library.** Bare React Native primitives + `StyleSheet.create` + a generated typed token module. No Tailwind in the app |
| Desktop | **Deleted, not deferred** — no macOS/Windows targets, no desktop CI jobs, no `artifact_hosting`, no desktop Cognito client |
| Mobile | **Latent.** Nothing under `ios/`/`android/` is committed; `expo prebuild` generates both on demand |

**What D8 was really buying, and where it went.** D8's central claim was that
optionality was nearly free: *"the desktop targets stay green in CI, so
reversing this is a marketing decision plus certificate procurement, not an
engineering rebuild."* That was true, and it was true **because of Flutter** —
one toolchain compiled macOS, Windows and web from one source, so holding the
option open cost a CI job.

React Native does not have those economics. Desktop means
`react-native-macos` / `react-native-windows`: separate forks, their own
release cadence, their own SDK-version skew. Keeping them "green in CI" would
not be maintenance, it would be a second port under continuous repair — the
opposite of what D8 was paying for.

So the option is kept a different way. **Under Expo the cheap held-open target
is mobile, and `expo prebuild` holds it open with nothing committed and no CI
job at all** — no `ios/`, no `android/`, no build minutes. Optionality now comes
from *having chosen React Native*, not from running desktop builds. That is the
whole substitution, and it is worth stating plainly: **we traded a cheap
desktop option for a cheaper mobile one**, on the judgement that a bankruptcy
attorney's second device is a phone rather than a second desktop OS.

**What this costs, honestly.** If a firm demands macOS or Windows tomorrow, the
answer is a port, not a certificate order. D8's estimate — weeks of Windows OV
validation plus notarization — is now the *second* half of the bill. The
sections below on signing and procurement stay in this document precisely
because that half has not changed and will still apply.

**Consequential edits elsewhere:**

- **D8 → superseded** (above); **D4 → revised** (two targets, neither of them
  Flutter); **D3 → annotated** (it stands; marketing does not move).
- **[ADR 0002](adr/0002-desktop-auto-update-deferred.md) → superseded by ADR
  0004.** Its subject was an updater for a build that no longer exists. Its
  revisit trigger and cost model are kept, as the checklist for any desktop
  return.
- **[ADR 0003](adr/0003-flutter-app-layout.md) → superseded by
  [ADR 0005](adr/0005-expo-app-layout.md)**, which makes the same "adopt the
  framework's own published layout" call for Expo.
- **Milestones 2 and 4 are partly historical** — see the notes on each.
- The environment variable is renamed `INSOLVIA_ENV` → **`EXPO_PUBLIC_INSOLVIA_ENV`**.
  Expo inlines only `EXPO_PUBLIC_*`, so this was forced.

---

## Sequencing — what blocks what

Two long-lead items gate almost everything, and both are *waiting*, not
*working*. Start them on day one.

```
DAY-ONE PROCUREMENT (waiting, not working — start together)
  [.ai registration]──┐
  [SES prod access]───┤       ┌─▶ M4 web app (desktop dropped — D9) ─┐
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
| MyCase API access | low | M0 | ✅ **De-risked** — founder's business partner works at MyCase |
| SES production access | ~24h+ | replying as `@insolvia.ai` | 🔜 **Deliberately deferred to issue 6.8** — submitted last, with the site and bounce handling already in place |

**There is no external queue left.** Every remaining blocker is engineering we
control, which makes the AWS bootstrap block in Milestone 1 (issues 1.0 → 1.3b)
unambiguously the place to start — nothing else can proceed until the wildcard
certificate is issued.

**Off the procurement list, originally by D8 and now permanently by D9:**
Windows code-signing certificate and Apple Developer account. These were the two
longest lead times in the plan. Under D8 they were *deferred* — the desktop
builds existed and only distribution was held back — so reversing it meant the
multi-week Windows validation window and nothing else. **Under D9 they are not
on the list at all**, because there is no desktop build to sign: a desktop
return is a port to `react-native-macos` / `react-native-windows` *first*, and
only then the certificates. Same weeks of lead time, now behind engineering
rather than in front of it.

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

**Access is not a risk — a founder's business partner works at MyCase.** An
earlier revision of this plan treated MyCase access as the largest open
assumption and put this milestone on the critical path. That was wrong, and the
milestone is **not urgent**.

What the relationship does and doesn't cover is still worth separating:

- **Covered:** obtaining credentials, App Bar navigation, rate limits, sync
  mechanics, and fast answers generally. Issues 0.5–0.7 likely close in a
  conversation rather than an investigation.
- **Not covered:** what the API can technically *do*. Whether write endpoints
  exist for the fields bankruptcy intake needs is a property of the API surface,
  not of who you know. An insider gets the answer in an afternoon instead of a
  fortnight — but the answer could still be "that endpoint doesn't exist."

So 0.3 (write round-trip) and 0.4 (data-model mapping) keep their value: they
feed directly into the intake milestone's design, and 0.5's push-vs-poll answer
is an architectural fork. They're now cheap and fast rather than slow and
uncertain.

**One thing the relationship makes *more* important, not less.** Business-plan
§10 names platform dependency a *High* risk, and its own closing note says the
MyCase relationship "should be confirmed (formal partnership / App Bar listing
vs. warm introductions) before the channel is leaned on in a raise." A personal
relationship is precisely the kind the plan warns about depending on — it moves
with the person. That's issue 0.7, and it's the one item here worth doing
properly rather than informally.

**The API is publicly documented**, which changes how this milestone runs:

> https://mycaseapi.stoplight.io/docs/mycase-api-documentation/k5xpc4jyhkom7-getting-started

Most of the open questions — whether write endpoints exist, what the data model
looks like, rate limits, pagination, webhook vs. polling — are answerable from
the documentation **before we hold a single credential**. So the milestone runs
docs-first (issue 0.0), and the credentialed work becomes confirmation of what
we already expect rather than discovery.

That makes this milestone both cheap and completely unblocked: it can start
whenever, in parallel with anything, with no dependency on AWS, DNS, or MyCase
procurement.

**Outcome:** the integration's technical shape documented well enough for the
intake milestone to be designed against it.

| # | Issue | Notes |
|---|---|---|
| 0.0 | **Documentation spike — answer everything answerable without credentials** | Read the [public API docs](https://mycaseapi.stoplight.io/docs/mycase-api-documentation/k5xpc4jyhkom7-getting-started). Covers auth model, available endpoints, write coverage, data model, rate limits, pagination, webhooks. **Do this first** — it's free, unblocked, and turns 0.2–0.5 into confirmation rather than discovery. |
| 0.1 | Obtain MyCase API credentials | Requires the **Advanced tier (~$89/mo)** per business-plan §3. Not a blocker — partner relationship. |
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

**Design:** an SES receipt rule set → S3 → Lambda → re-send via SES with the
original sender as `Reply-To`, plus a Gmail runbook for wiring up the send-as
alias.

| # | Issue | Notes |
|---|---|---|
| ~~1.1~~ | ~~Register `insolvia.ai`~~ — ✅ **DONE** | Gandi, created 2026-07-21, expires 2028-07-21 (two-year `.ai` term). |
| ~~1.4~~ | ~~Delegate registrar NS → Route53~~ — ✅ **DONE** | Gandi delegates to zone `Z01038711J6IZ68FD6ZDW`. |
| 1.0 | **Create the Terraform state bucket** — `insolvia-terraform-state` | Bootstrap step 1 of `docs/AWS_SETUP.md`, **not yet done**. `terraform init` cannot run without it — every `backend.tf` in the repo points at this bucket. This is now the first action in the whole plan. |
| 1.1b | **⚠️ `terraform import` the existing hosted zone before the first apply** | **Critical — see the trap below.** `terraform import aws_route53_zone.main Z01038711J6IZ68FD6ZDW`. Skipping this silently breaks DNS *and* hangs the certificate. |
| ~~1.2~~ | ~~Verify the destination Gmail address as an SES identity~~ — ❌ **SUPERSEDED** | Only existed to make SES→Gmail forwarding testable in the sandbox. Inbound is Google Workspace now; there is no forwarding destination to verify. **SES production access is still deferred to issue 6.8.** |
| 1.3 | Apply `infra/envs/shared` — wildcard ACM, OIDC provider, deploy role | Confirmed **not applied**: no state bucket, no OIDC provider, no `insolvia-github-actions` role, no ACM certificate. Only the zone exists. Blocked by 1.0 + 1.1b. |
| 1.3b | Confirm the wildcard ACM cert reaches `ISSUED` | `infra/envs/staging/main.tf` looks the cert up with `statuses = ["ISSUED"]`, so every downstream env fails at plan time with a misleading "no matching certificate" error until this is true. |
| ~~1.3c~~ | ~~Wire the `AWS_ROLE_ARN` secret~~ — ✅ **DONE** | Step 5 of `AWS_SETUP.md`; deploys authenticate through it. |
| 1.3d | Update `docs/AWS_SETUP.md` | Its status banner still says the domain is the current blocker. It isn't. Add the state-bucket and zone-import steps while you're there. |
| 1.5 | New `infra/modules/email`: SES domain identity, DKIM, custom MAIL FROM | |
| 1.6 | SPF, DMARC, and MX records for `insolvia.ai` | MX → `1 smtp.google.com` (Google Workspace). Apex SPF must include **both** `amazonses.com` and `_spf.google.com`. Start DMARC at `p=none`, tighten once Google DKIM is live. |
| ~~1.7~~ | ~~Port `support_forwarding` → `infra/modules/inbound_forwarding`~~ — ❌ **SUPERSEDED, then removed** | Built, then torn out when Google Workspace took over inbound. Only one apex MX set can exist, so SES receiving and Workspace are mutually exclusive. |
| ~~1.8~~ | ~~Port forwarder Lambda source → `services/inbound_forwarder/`~~ — ❌ **SUPERSEDED, then removed** | Same reason as 1.7. |
| ~~1.9~~ | ~~SSM SecureString for the private forward-to destination~~ — ❌ **SUPERSEDED, then removed** | No forwarding destination exists. Delete the `INBOUND_FORWARD_TO` GitHub environment secret by hand. |
| ~~1.10~~ | ~~DLQ + CloudWatch alarm on the forwarder~~ — ❌ **SUPERSEDED, then removed** | Same reason as 1.7. |
| 1.11 | Address map — **confirmed** | `hello@` (general), `support@` (product), `security@` (disclosure) are real Google Workspace mailboxes. `no-reply@` is an SES send-only transactional sender with no inbox. |
| ~~1.12~~ | ~~SES SMTP credentials + Gmail "Send mail as" runbook~~ — ✅ **Written** | [`docs/EMAIL_SETUP.md`](EMAIL_SETUP.md). Creating the credentials and adding the Gmail alias remain human steps; replies stay broken until 6.8. |
| ~~1.13~~ | ~~Un-gate the deploy workflows~~ — ✅ **DONE** | 1.3 + 1.3b hold, deploys run for real, and the temporary deploy gate has been removed from the workflows entirely. |
| ~~1.14~~ | ~~Document the Google Workspace migration path~~ — ✅ **Written** | [`docs/EMAIL_SETUP.md` § Migrating to Google Workspace](EMAIL_SETUP.md#migrating-to-google-workspace) — ordered cutover checklist plus what to verify after. |
| 1.15 | Rename the staging host → `staging-app.insolvia.ai` | Three files, one commit: `infra/envs/staging/variables.tf`, `terraform.tfvars.example`, and the app's environment config. Free now, annoying once anything is deployed. See D2. |

### Working in the SES sandbox — what does and doesn't function

**Decision:** we stay in the SES sandbox for now and request production access
**last** (issue 6.8), once there's a live site, working bounce/complaint
handling, and a privacy policy for AWS to review. Submitting cold invites a
rejection, and rejections make resubmission harder.

What that means in practice:

| Capability | In sandbox |
|---|---|
| Receiving mail at `@insolvia.ai` | ✅ Unaffected — inbound is Google Workspace, not SES |
| Humans replying from their Workspace mailboxes | ✅ Unaffected — sends via Google, not SES |
| **The app sending transactional mail as `no-reply@`** | ❌ **Blocked** unless the recipient is itself a verified SES identity |
| Sending volume | 200/day, 1 msg/sec |

**The catch to plan around:** the sandbox no longer touches human mail at all —
Google Workspace handles inbound and human replies end to end. What stays dark
until 6.8 is the **application's** outbound: waitlist confirmations, password
resets, and anything else sent as `no-reply@insolvia.ai` can only reach verified
identities.

That's fine while there are no users — but it means **issue 6.8 gates the first
real product email**, so it should not slip past the point where the app needs
to mail a stranger.

### ⚠️ The duplicate-hosted-zone trap — read before running `terraform apply`

**Verified state of the Insolvia AWS account (521762924626), 2026-07-21:**

| Resource | State |
|---|---|
| Hosted zone `insolvia.ai` | ✅ Exists — `Z01038711J6IZ68FD6ZDW`, **2 records (NS + SOA only)** |
| Gandi NS delegation | ✅ Points at that zone |
| S3 bucket `insolvia-terraform-state` | ❌ **Does not exist** |
| GitHub OIDC provider | ❌ Absent |
| IAM role `insolvia-github-actions` | ❌ Absent |
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

> **Shipped, and partly rewritten by D9.** The milestone is done; the rows below
> are the record of what was built, not a live checklist. Two of them no longer
> describe the repo: the Dart half of 2.1/2.2 is gone (the generator is
> TypeScript and emits marketing's `theme.css` plus the app's typed `tokens.ts`),
> and `design-system-pr.yml` in 2.8 is deleted along with the Flutter package.
> The React package, its scope cap and its parity discipline are untouched.

**Outcome:** one token source of truth driving both a Flutter package and a
React package, so the marketing site is on-brand by construction rather than by
eyeballing.

The component pattern, the `cn()` merge helper, the
`@source` directive consumers need, and the tsup build config are all
well-trodden prior art.

| # | Issue | Notes |
|---|---|---|
| 2.1 | Extract tokens to a neutral `packages/insolvia_tokens/tokens.json` | Carries descriptions so generated Dart keeps its doc comments. |
| 2.2 | Generator: `tokens.json` → Dart token files **+** Tailwind v4 `@theme` CSS | Both outputs committed; CI check fails on drift. A small script beats pulling in Style Dictionary for this token count. |
| 2.3 | Map the Insolvia palette onto semantic tokens | `ink`/`brass`/`paper` → `--color-primary`/`--color-accent`/`--color-bg`, etc. Follow the semantic-indirection pattern, including `[data-theme='dark']`. |
| 2.4 | Scaffold `packages/insolvia_design_system_react/` as `@insolvia-ai/design-system` | tsup → ESM + CJS + `.d.ts`, `theme.css` copied verbatim to `dist/`. Tailwind v4 + Base UI + `cn()`. Excluded from the pub workspace. |
| 2.5 | Build **only** the marketing components | Button, Card, NavBar, Footer, Accordion, Input/Field. Explicitly *not* a port of all 40 wrappers. |
| 2.6 | Storybook + Vitest/Testing Library | Mirrors the Flutter package's "every exported component has a widget test" rule. |
| 2.7 | Publish to GitHub Packages under the `@insolvia-ai` scope | `.npmrc` with `${NODE_AUTH_TOKEN}`; CI uses `secrets.GITHUB_TOKEN`. Bundle the design system into the SSR build via `ssr.noExternal` so the runtime Lambda needs no registry token. |
| 2.8 | Workflow `design-system-react-pr.yml` | Alongside the existing `design-system-pr.yml`. |
| 2.9 | Write the parity discipline into CLAUDE.md | The scope limit in D4, plus: tokens are never hand-edited in either generated file. |

---

## Milestone 3 · Marketing site (`www.insolvia.ai`)

**Outcome:** a fast, crawlable, on-brand marketing site at `www.insolvia.ai`,
with the apex redirecting to it.

**Depends on Milestone 2.**

| # | Issue | Notes |
|---|---|---|
| 3.1 | Scaffold `apps/insolvia_marketing/` — React Router v7 framework mode | Own `package.json` and lockfile; deliberately **not** a root-workspace member (the reason is in the root `package.json` comments). |
| 3.2 | Wire the design system + Tailwind entrypoint | `@import "tailwindcss"` → `@import "@insolvia-ai/design-system/theme.css"` → `@source` the dist. Missing the `@source` line is the classic "why are my styles gone" bug. |
| 3.3 | Content pass — positioning, JTBD, competitive framing | Source from `business-plan.html` §6 (jobs-to-be-done) and §7 (positioning). Do not invent new claims; the plan's figures are sourced and shouldn't drift. |
| 3.4 | SEO baseline | Per-route `<title>`/meta/OG, `sitemap.xml`, `robots.txt`, JSON-LD `Organization`. Explicitly allow GPTBot/ClaudeBot/PerplexityBot — inbound increasingly arrives through them. |
| 3.5 | Infra: `www` + apex hosting | CloudFront + S3 assets + SSR Lambda (`infra/modules/marketing_site`). Apex → `www` 301. |
| 3.6 | Set `allowedActionOrigins` for `www` + apex | The CSRF trap documented in D3. Cheaper to do now than to debug later. |
| 3.7 | Workflows: `marketing-pr.yml`, `marketing-staging.yml`, `marketing-prod.yml` | Follow existing `app-*.yml` shape and the cache-control rules in CLAUDE.md. Staging serves `staging-www.insolvia.ai`. |
| 3.10 | `noindex` on every non-prod host | `staging-www`, `staging-app`, `staging-api`. A crawlable staging copy of the marketing site competes with prod for its own keywords — a genuinely damaging and easily-missed SEO own-goal. |
| 3.8 | Lighthouse / Core Web Vitals budget in CI | The whole reason this site has its own stack — first D3, and now D9's measurements, which are what keep it off the app's. Enforce it or the reasoning rots. |
| 3.9 | Waitlist / contact capture | **Soft-depends on Milestone 5.** Ship storing to DynamoDB directly from the SSR action first (no SES, intake straight to DynamoDB), rather than blocking on the API. |

---

## Milestone 4 · App shell (`app.insolvia.ai` + desktop)

> **Shipped as written, then half of it removed by D9.** Rows 4.5–4.11 are the
> desktop half: the `windows/` target, the `.dmg`/`setup.exe` artifacts, the two
> per-OS CI jobs, `artifact_hosting`, and the unsigned-install walkthrough. All
> of it was built and all of it is now deleted — D9 explains why holding that
> option open stopped being cheap. The rows stay because *"why did the repo once
> have a Windows build?"* is a question the git history answers badly and this
> answers well. 4.1–4.4 and 4.12 are live and still describe the web app; 4.4's
> components are now the app's own React Native ones rather than the Flutter
> design system's.

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
| 4.7 | CI: add `windows-latest` + `macos-latest` build jobs | Flutter desktop must be built on its target OS — no cross-compilation. **The 2×/10× minute multipliers do not apply here:** they bill against the included-minutes quota on *private* repos, and `insolvia-ai/insolvia` is public, where standard runners are free with unlimited minutes. The real costs are queueing against the per-OS concurrency limits and wall-clock time in the PR gate. Larger and GPU runners *are* billable on public repos — don't reach for them without a reason. |
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

**Still accurate under D9, and now the second half of a longer bill.** Every
procurement fact above survives the migration untouched — certificates and
notarization do not care what compiled the binary. What changed is that there
is no longer a binary: D9 deleted both desktop targets, so a desktop return
starts with a port to `react-native-macos` / `react-native-windows` and only
*then* reaches this paragraph.

---

## Milestone 5 · API (`api.insolvia.ai`)

**Outcome:** the trust boundary exists. No client — web or desktop — ever holds
AWS credentials or talks to an AWS service directly.

**The layered layout:** `core/` contracts with no framework deps → `api/` Flask
blueprints → `adapters/` AWS + in-memory + Mailpit → `entrypoints/` Lambda
handlers and the dev server, with an architecture test that enforces the
dependency direction. That test is worth writing on day one — it's what stops
the layering rotting.

| # | Issue | Notes |
|---|---|---|
| 5.1 | Scaffold `services/api/` — Flask + Mangum, mirroring mailer's layout | Port the architecture test with it. |
| 5.2 | Infra: API Gateway HTTP API + Lambda (Docker/ECR) + CloudFront + custom domain | Note the CLAUDE.md rule: `lifecycle { ignore_changes = [image_uri, environment] }`, and build-and-push the image *before* Terraform applies, or fresh-account deploys deadlock. |
| 5.3 | Stand up **both** `staging-api` and `api` environments | Per CLAUDE.md, each env is its own `infra/envs/<env>/` directory with its own state key — never Terraform workspaces. Separate ECR tags, Cognito pools, and DynamoDB tables per env; staging must never read prod data. |
| 5.4 | Point the app's env config at the right API host per build | `src/config/environment.ts` gains an `apiBaseUrl` alongside `host`, resolved from `EXPO_PUBLIC_INSOLVIA_ENV`. A staging build hitting prod is the failure mode to design out. *(Was `environment.dart` / `INSOLVIA_ENV` — see D9.)* |
| 5.5 | Auth: Cognito user pool + app clients | **One** flow now: web PKCE. D9 deleted the desktop client and its loopback-redirect flow — the awkward half of this row. Separate pools per environment. |
| 5.6 | API client package `packages/insolvia_api_client/` | TypeScript, an npm workspace member; generated from an OpenAPI spec if practical. *(Planned as Dart, "shared by web and desktop builds" — D9 made it one language and one target.)* |
| 5.7 | Write down the trust boundary as an ADR | The "client stays dumb" rule needs documenting, or it erodes the first time something is easier to do client-side. |
| 5.8 | CORS allowlist per environment | `api` accepts `app.insolvia.ai`; `staging-api` accepts `staging-app.insolvia.ai` + localhost. *(The original caution — "desktop sends no browser `Origin`, don't let a permissive desktop path widen the web policy" — is moot under D9, and would return with any native client.)* |
| 5.9 | Structured JSON logging, `/health`, CloudWatch alarms | |
| 5.10 | Config + secrets via SSM, namespaced per env | `/insolvia/<env>/...`. |
| 5.11 | Workflows: `api-pr.yml`, `api-staging.yml`, `api-prod.yml` | `staging` on push to `main`; `prod` `workflow_dispatch` behind the `insolvia-production` environment, per CLAUDE.md. |
| 5.12 | Local dev via `docker compose` | Mirror `mailer/docker-compose.yml`. |

---

## Milestone 6 · Transactional email (mailer service)

**Outcome:** the app can send product email — welcome, verification, password
reset — durably and with feedback handling.

**The mailer flow:** SigV4-signed `POST /v1/services/<service>/messages` →
ingress Lambda verifies the caller role → manifest to S3 + pointer to SQS →
sender Lambda validates content, suppression, kill switch → SES → normalized
feedback events back to a status queue.

That design is more than an MVP strictly needs. Recommendation: **build it whole
anyway.** Suppression handling and bounce/complaint feedback are not optional
once you're sending to real attorneys — SES will throttle or suspend an account
with a bad complaint rate, and retrofitting suppression afterwards is painful.

| # | Issue | Notes |
|---|---|---|
| ~~6.1~~ | ~~Build `services/mailer/`~~ — ✅ **Done** | Tenant-specific configuration sets stripped. |
| ~~6.2~~ | ~~Insolvia service registry + IAM role mapping~~ — ✅ **Done** | One registered caller: `insolvia_api`. |
| ~~6.3~~ | ~~Port the mailer infra module~~ — ✅ **Done** | SQS, S3 manifests, sender + feedback Lambdas, suppression, kill switch. |
| ~~6.4~~ | ~~API → mailer integration over SigV4~~ — ✅ **Done** | `services/api` `adapters/aws/mailer_client.py`. |
| ~~6.5~~ | ~~Mailpit local dev loop~~ — ✅ **Done** | `services/mailer/docker-compose.yml`. |
| ~~6.6~~ | ~~Initial templates: welcome, email verification, password reset~~ — ✅ **Done** | `services/api` `core/mail.py`. No route calls them yet — the auth flows that trigger them are a later milestone. |
| ~~6.7~~ | ~~Bounce/complaint monitoring + alarms~~ — ✅ **Done** | Alarms at 5% bounce / 0.1% complaint, both well under the AWS review thresholds. |
| 6.8 | **Request SES production access — the last thing we do** | **Everything in this repo is ready; submitting is a human AWS-console action.** Runbook, pre-submission checklist, and the exact request text: [`SES_PRODUCTION_ACCESS.md`](SES_PRODUCTION_ACCESS.md). Deliberately deferred so the request is made with everything AWS reviews already in place: a live `www`, bounce/complaint handling (6.7), suppression (6.3), a working unsubscribe path, and a published privacy policy — the last two were built for this issue. **One thing blocks submission and it is a decision, not work:** `www.insolvia.ai` is parked offline (`site_enabled = false` in `infra/envs/prod`), so the privacy policy AWS reviews does not currently load. Until this lands we cannot send to any address that is not a verified SES identity (see the sandbox note in M1) — so don't let it slip forever. |

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
| Windows at MVP, or macOS only? | **Both — then neither.** Answered "both" in rev 3, shipped, and removed by D9 | Milestone 4 issues 4.6–4.8 + the code-signing warning; D9 for why the answer changed |
| Address map | **Confirmed** | Issue 1.11 |
| npm scope `@insolvia`? | **Overturned — it's `@insolvia-ai`.** GitHub Packages requires the scope to equal the owning org's login (`insolvia-ai`) and rejects `@insolvia` with a misleading "installation does not exist" 403 | Issue 2.7; `docs/PACKAGE_PUBLISHING.md` |
| Staging for marketing? | **Answered "no" in rev 3; reversed in M6** — it is `staging-www.insolvia.ai` now | D2 table; the reversal and its reasoning live there |

## Remaining risks worth watching

Not questions — just the things most likely to bite, in order:

1. **The duplicate-hosted-zone trap (M1).** Highest-probability concrete failure
   in the plan, because the natural next action — `terraform apply` on shared —
   triggers it. See the boxed section in Milestone 1. Import first.
2. **MyCase write coverage (M0)** — *downgraded.* Access itself is not a risk
   (partner relationship). What remains is narrower: whether the API exposes
   write endpoints for the fields intake needs. Cheap to answer now, and it
   shapes the intake milestone rather than the whole thesis.
3. **Channel formality (0.7).** The warm channel currently rests on a personal
   relationship, which moves with the person. Business-plan §10 already flags
   this and recommends a formal partnership plus a second channel (direct/NACBA)
   before the channel is leaned on in a raise.
4. ~~**Desktop bit-rot (D8).**~~ **Closed by D9 — the risk was realised, in the
   cheapest available way.** D8's guard against unpromoted targets rotting was
   issue 4.8, keeping both green in CI. D9 removed the targets instead, so
   there is nothing left to rot. What replaces this risk is item 5.
5. **Web-first is a bet on attorney behaviour — and D9 raised the stake.** The
   business plan describes this market as desktop-loyal. Pushing web is still
   right, but the assumption is now more expensive to be wrong about: under D8
   the counter-evidence cost a marketing decision plus certificates, because the
   desktop build already existed. Under D9 it costs a port. **So test it
   explicitly with the design-partner firm, early** — this is the item on this
   list whose cost of late discovery went up rather than down.
6. **We now own complex-widget accessibility (D9).** No component library means
   `Modal`, `Select`, `Combobox` and date pickers are ours, in a product that
   needs all four. Guarded by the axe assertion in `app-pr.yml` from day one;
   the intended relief is `@react-native-aria/*` when it lands. ADR 0004 records
   both the library defects that made owning them attractive and the breadth
   cost that makes it uncomfortable at forty components.
7. **SES production access deferred too long (6.8).** Deferring it is correct —
   the request is stronger with a live site and real bounce handling. The risk is
   the opposite one: while it's outstanding we can receive mail at
   `@insolvia.ai` but cannot reply from it, so the mailbox is half-built. Set a
   date rather than leaving it open-ended.
8. **Design-system parity drift.** Contained by the six-component scope limit in
   D4 — which only holds if issue 2.9 actually writes it into CLAUDE.md. D9 did
   not change the shape of this risk: there are still two implementations of one
   design, and the second one is now the app's own React Native components
   rather than a Flutter package.

---

## GitHub

Access confirmed 2026-07-21 — `gh` has the `project` scope, and the **MVP**
project exists (`PVT_kwDOEi5yWs4BeBkB`). The repo currently has **no milestones
and no issues**, so creation is a clean slate.

Awaiting go-ahead to create the seven milestones and their issues, and add them
to the MVP board.
