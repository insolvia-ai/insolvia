# Insolvia docs

Engineering reference, runbooks, decisions, and the business plan. Repo-wide
conventions live in the root [`../CLAUDE.md`](../CLAUDE.md); the rules for
adding to this directory are in [`CLAUDE.md`](CLAUDE.md).

Documents are filed by **how they are read**: durable explanation, executable
procedure, decision, or business artifact.

## [`plan.md`](plan.md) — the living plan

What shipped, the decisions in force, and the open work. The one document here
that is rewritten as work lands.

## [`reference/`](reference/) — how the system works

| Doc | What |
|---|---|
| [`architecture.md`](reference/architecture.md) | Monorepo shape, toolchain (incl. the Expo free-tier constraint), environment model, hosting topology, CI/CD, PR-gate design + the required-check contract. |
| [`case-data-model.md`](reference/case-data-model.md) | The case schema: entities mapped to B101/B106/B107, per-field provenance and the confirm-before-entry invariant, derived values, the MyCase sync seam. |
| [`terraform.md`](reference/terraform.md) | Infra state model, modules, naming, deploy order, the ci-trust self-deny. |
| [`package-publishing.md`](reference/package-publishing.md) | How the design system publishes (source, no build) and how its two consumers install it — the app from the workspace, marketing by published version. |
| [`email.md`](reference/email.md) | `insolvia.ai` mail: address map, DNS records + owners, Google Workspace inbound + SES outbound. |

## [`runbooks/`](runbooks/) — procedures you execute

| Runbook | What | State |
|---|---|---|
| [`aws-bootstrap.md`](runbooks/aws-bootstrap.md) | One-time AWS/GitHub bootstrap, incl. the ci-trust anchor. | Executed; kept for a fresh account |
| [`app-deploy-verification.md`](runbooks/app-deploy-verification.md) | Six checks proving a host serves the app, and the right build. | Both envs verified; re-run per deploy |
| [`ses-production-access.md`](runbooks/ses-production-access.md) | The SES sandbox exit: checklist, request text, human console steps. | **Open** — actionable now |
| [`staging-e2e-setup.md`](runbooks/staging-e2e-setup.md) | One-time setup for the post-deploy auth E2E: the staging test user, then its Actions environment secrets. | **Open** — actionable now |

## [`adr/`](adr/) — decisions and their reasoning

Durable decisions that stay expensive to re-litigate. See
[`adr/README.md`](adr/README.md) for the register and the numbering rule.
Shorter-lived planning decisions live as `D<n>` entries in [`plan.md`](plan.md).

## [`business/`](business/) — company artifacts

| Doc | What |
|---|---|
| [`business-plan.html`](business/business-plan.html) | What we're building and why. |
| [`regulatory-source-register.html`](business/regulatory-source-register.html) | Authoritative regulatory source per feature, with its refresh cadence. |

## Not in `docs/`

- **Area rules** — how to change a specific app/package/service — live in that
  area's own `CLAUDE.md` and `README.md`.
- **Which script to run when** — the `insolvia-scripts` skill and
  [`../scripts/README.md`](../scripts/README.md).
- **AWS auth, deploys, deploy-role permissions, branch protection,
  design-system PRs** — the `insolvia-*` skills. Docs here name them rather
  than restating them.
