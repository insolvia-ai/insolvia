# Claude Instructions – docs/

These are Insolvia's docs. The root [`../CLAUDE.md`](../CLAUDE.md) is the source
of truth for conventions — read it first.

- [`business-plan.html`](business-plan.html) — what we're building and why (Best Case competitor); the fully-sourced company business plan.
- [`business-product-strategy.html`](business-product-strategy.html) — consolidated **business & product strategy**: market, moat, financial model, positioning, product roadmap, MVP strategy, and strategic risks — the business/product context isolated from technical execution.
- [`competitor-monetization.html`](competitor-monetization.html) — sourced deep-dive on **how competitors make money** (pricing models, headline figures, the payments layer) across Best Case, Glade AI, NextChapter, CINcompass, Jubilee, and Caseway; plus findings that touch our own pricing. Competitor identities/positioning stay in `business-plan.html` §2.
- [`MVP_PLAN.md`](MVP_PLAN.md) — now a thin **index**: the decision log (D1–D8) and a foundation milestone map. The per-issue technical detail lives in the GitHub milestones/issues; the business/product context moved to `business-product-strategy.html`.
- [`AWS_SETUP.md`](AWS_SETUP.md) — one-time AWS/GitHub bootstrap runbook.
- [`EMAIL_SETUP.md`](EMAIL_SETUP.md) — `insolvia.ai` mail: address map, the DNS records and who owns them, Google Workspace inbound + SES outbound.
- [`TERRAFORM_ARCHITECTURE.md`](TERRAFORM_ARCHITECTURE.md) — infra state model.
- [`ARCHITECTURE.md`](ARCHITECTURE.md) — monorepo, env model, hosting topology.

**AWS auth rule (repeated because it matters):** never hard-code or commit AWS
credentials. Local = your CLI profile/SSO; CI = the assumed OIDC role. If a tool
lacks credentials, stop and ask.

Docs are Markdown, `SCREAMING_SNAKE_CASE.md` for runbooks. Keep them current when
you change the thing they describe.
