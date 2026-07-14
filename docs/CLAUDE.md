# Claude Instructions – docs/

These are Insolvia's docs. The root [`../CLAUDE.md`](../CLAUDE.md) is the source
of truth for conventions — read it first.

- [`business-plan.md`](business-plan.md) — what we're building and why (Best Case competitor).
- [`AWS_SETUP.md`](AWS_SETUP.md) — one-time AWS/GitHub bootstrap runbook.
- [`TERRAFORM_ARCHITECTURE.md`](TERRAFORM_ARCHITECTURE.md) — infra state model.
- [`ARCHITECTURE.md`](ARCHITECTURE.md) — monorepo, env model, hosting topology.

**AWS auth rule (repeated because it matters):** never hard-code or commit AWS
credentials. Local = your CLI profile/SSO; CI = the assumed OIDC role. If a tool
lacks credentials, stop and ask.

Docs are Markdown, `SCREAMING_SNAKE_CASE.md` for runbooks. Keep them current when
you change the thing they describe.
