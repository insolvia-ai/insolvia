# Insolvia docs

Engineering runbooks and the business plan. Conventions and rules live in the
root [`../CLAUDE.md`](../CLAUDE.md) — read it first; these docs are the depth
behind its *Where things live* table.

| Doc | What |
|---|---|
| [`ARCHITECTURE.md`](ARCHITECTURE.md) | Monorepo shape, environment model, hosting topology, CI/CD, PR-gate design + required-check contract. |
| [`TERRAFORM_ARCHITECTURE.md`](TERRAFORM_ARCHITECTURE.md) | Infra state model, modules, naming, deploy order. |
| [`AWS_SETUP.md`](AWS_SETUP.md) | One-time AWS/GitHub bootstrap runbook, incl. the ci-trust anchor. |
| [`PACKAGE_PUBLISHING.md`](PACKAGE_PUBLISHING.md) | How both design systems publish and how consumers install them. |
| [`EMAIL_SETUP.md`](EMAIL_SETUP.md) | `insolvia.ai` mail: address map, DNS records + owners, Google Workspace inbound + SES outbound. |
| [`SES_PRODUCTION_ACCESS.md`](SES_PRODUCTION_ACCESS.md) | The SES sandbox exit: checklist, request text, and the human AWS-console steps. |
| [`MVP_PLAN.md`](MVP_PLAN.md) | Milestone/issue plan — the source for the GitHub MVP project board. |
| [`adr/`](adr/) | Architecture Decision Records — durable decisions with their rationale. |
| [`business-plan.html`](business-plan.html) | What we're building and why. |
| [`regulatory-source-register.html`](regulatory-source-register.html) | Regulatory source register. |

## Not in docs/

- **Area rules** (how to change a specific app/package/service, its conventions
  and gotchas) live in that area's own `CLAUDE.md` and `README.md`, not here.
- **Repo tooling** — which script to run when — is the `insolvia-scripts` skill
  and [`../scripts/README.md`](../scripts/README.md).
- **AWS auth / deploy-role permissions** are the `insolvia-aws-auth` and
  `insolvia-deploy-role-permissions` skills.

Docs are Markdown, `SCREAMING_SNAKE_CASE.md` for runbooks. Keep a doc current when
you change the thing it describes — and don't restate a fact that already has an
owner elsewhere; link to it.
