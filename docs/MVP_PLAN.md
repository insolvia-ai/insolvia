# Insolvia — MVP Plan (index)

> **This document was reorganised on 2026-07-23.** The MVP plan produced two
> durable artifacts, and they now own the detail:
>
> - **Business & product context** — the market, moat, financial model,
>   positioning, product roadmap, MVP strategy, and strategic risks — lives in
>   [`business-product-strategy.html`](business-product-strategy.html) (strategy)
>   and [`business-plan.html`](business-plan.html) (fully-sourced figures).
> - **Technical execution** — the per-issue foundation work (infra, CI, email,
>   design system, marketing, API, mailer, desktop) — lives in the
>   [**GitHub milestones**](https://github.com/insolvia-ai/insolvia/milestones)
>   and [**issues**](https://github.com/insolvia-ai/insolvia/issues).
>
> What remains here is the **durable index**: the decision log (D1–D8, which
> other docs reference by anchor) and a milestone map pointing at the issues.
> The blow-by-blow engineering detail, AWS/Terraform bootstrap traps, and
> per-ticket notes that used to fill this file now live on the issues themselves.

Everything below is scoped to the **foundation** — getting the domain, email,
the design system's second target, the public surfaces, and the API trust
boundary standing up. Product features (MyCase intake, forms engine, means test)
are the milestones beyond foundation and are shaped by the strategy document.

---

## How this maps to the company milestones

[`business-plan.html`](business-plan.html) §11 defines the *company* milestones
M0–M3. The foundation engineering work sits inside business-plan **M0 → M1** —
it is what turns "foundation repo authored" into "live staging, ready for the
MyCase spike." See
[`business-product-strategy.html` §8 (MVP strategy)](business-product-strategy.html#mvp)
for the business reasoning behind the sequence.

| Company milestone | Foundation work |
|---|---|
| **M0 · Foundation repo** — design system, app shell, CI/CD, infra authored | ✅ Shipped |
| **M1 · Live staging + MyCase API spike** | Foundation milestones 1–6 below, then the MyCase spike (milestone 0) |
| **M2 · MyCase-native intake + AI extraction** | Beyond foundation |
| **M3 · Compliant Chapter 7 packet** | Beyond foundation |

---

## Decisions taken (durable record)

These are the decisions other docs reference by anchor (e.g. `CLAUDE.md` cites
**D4** and **D6**; the trust-boundary ADR cites **D6**; the marketing site cites
**D8**). Keep the anchors stable. The full rationale and any engineering detail
for each now lives in the linked strategy document or the relevant issue.

| # | Decision | In one line |
|---|---|---|
| **D1** | Domain is **insolvia.ai**, not `.com` | The repo, shared ACM wildcard, and `environment.dart` are already built on it. |
| **D2** | Every environment gets its own host; staging included, flat `staging-<surface>` naming | A single `*.insolvia.ai` ACM wildcard covers one label — flat `staging-app` matches it, nested `app.staging` would not. |
| **D3** | Marketing site is **React Router v7** (SSR), not Flutter | Flutter web compiles to `<canvas>` and cannot be server-rendered or crawled; the marketing page is where bounce rate and Core Web Vitals are the whole game. |
| **D4** | The **design system is dual-target**: a Flutter package + a React package over one shared token source | Consequence of D3. One brand, two renderings; components can't be shared, so the React surface is deliberately capped (see the parity discipline in `CLAUDE.md`). |
| **D5** | **The API is required for MVP**, not deferred | Clients (web and desktop) cannot hold AWS credentials; SSNs and full financials under GLBA Safeguards need a server-side trust boundary. See [`adr/0001-client-stays-dumb-trust-boundary.md`](adr/0001-client-stays-dumb-trust-boundary.md). |
| **D6** | Backend stack is **Python + Flask + Mangum on Lambda** | The established house pattern (mirrors `andreas-services/mailer`). |
| **D7** | Human email and product email are **separate milestones** | Inbound forwarding to Gmail is cheap, urgent, and app-independent; the mailer service depends on the API. Don't block the urgent thing behind the slow one. |
| **D8** | **Web is the promoted path; desktop is built but not promoted** — unsigned, no certificate procurement yet | Removes the longest procurement lead time while preserving the Flutter optionality. The business framing is in [`business-product-strategy.html` §8](business-product-strategy.html#mvp). |

---

## Foundation milestone map

High-level only — each item's tasks are tracked as
[GitHub issues](https://github.com/insolvia-ai/insolvia/issues), grouped under
the matching [milestone](https://github.com/insolvia-ai/insolvia/milestones).

| Milestone | Outcome | Where the detail lives |
|---|---|---|
| **0 · MyCase API spike** | The integration's technical shape documented well enough for the intake milestone to be designed against — including one authenticated read *and* write round-trip. This is the go/no-go on the channel thesis. | Issues `0.0`–`0.8` |
| **1 · Domain & Email** | `insolvia.ai` resolves, mail to `hello@insolvia.ai` lands in Gmail, and (once out of the SES sandbox) can be replied to as that address. | Issues `1.x`; runbook [`EMAIL_SETUP.md`](EMAIL_SETUP.md), [`AWS_SETUP.md`](AWS_SETUP.md) |
| **2 · Design system — React target** | One token source of truth driving both a Flutter package and a React package, so the marketing site is on-brand by construction. | Issues `2.x`; [`PACKAGE_PUBLISHING.md`](PACKAGE_PUBLISHING.md) |
| **3 · Marketing site** (`www.insolvia.ai`) | A fast, crawlable, on-brand marketing site with the apex redirecting to it. | Issues `3.x` |
| **4 · App shell** (`app.insolvia.ai` + desktop) | Infrastructure proven end-to-end for both delivery targets, with a deliberately minimal home page; desktop built but unpromoted (D8). | Issues `4.1`–`4.12` |
| **5 · API** (`api.insolvia.ai`) | The trust boundary exists — no client ever holds AWS credentials or talks to an AWS service directly (D5). | Issues `5.x`; [`adr/0001`](adr/0001-client-stays-dumb-trust-boundary.md) |
| **6 · Transactional email** (mailer service) | The app can send product email — welcome, verification, password reset — durably and with bounce/complaint handling. | Issues `6.1`–`6.8` |

**Beyond foundation** (their own sessions, shaped by the strategy doc's product
roadmap — [`business-product-strategy.html` §6](business-product-strategy.html#roadmap)):
MyCase-native intake & AI extraction (P1), the forms & petition engine (P2), and
the means test (P3, with the IRS/Census refresh pipeline from the
[regulatory source register](regulatory-source-register.html)).

---

## Engineering knowledge that must not be lost with the tickets

A few hard-won engineering facts were called out in the original plan. They are
preserved in the runbooks and issues, but flagged here so they aren't forgotten:

- **The duplicate-hosted-zone trap** — the `insolvia.ai` zone was created outside
  Terraform, so `terraform apply` on `infra/envs/shared` would create a *second*
  zone and silently hang ACM validation. **Import, don't recreate.** Full
  procedure in [`AWS_SETUP.md`](AWS_SETUP.md).
- **Desktop bit-rot (D8)** — unpromoted desktop targets rot silently and break at
  exactly the moment a prospect demands desktop. Keeping both targets green in CI
  is the entire cost of holding that option open; don't trim it (issue `4.8`).
- **SES production access is deferred to last** (issue `6.8`) — deliberately, so
  the request is made with a live site and working bounce handling in place. Until
  it lands we can *receive* mail at `@insolvia.ai` but not *reply* from it, so it
  shouldn't slip indefinitely.
