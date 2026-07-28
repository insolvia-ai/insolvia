# Insolvia — agent guide

Cross-platform bankruptcy case-prep & e-filing: one Dart/Flutter codebase for the
web + desktop app, a React site for `www.insolvia.ai`. Monorepo on AWS, one
shared design system.

**Web is the promoted path; desktop is built but not promoted** — kept green in
CI as an option, not led with. Don't over-invest in it: decision D8 in
[`docs/MVP_PLAN.md`](docs/MVP_PLAN.md) owns this and says what reversing costs.

**This file is a catalog.** It says where things live and what to open before
touching them — read the linked file when your task needs it. Detail lives there,
not here.

**Always (before you look anything up):**
- This repo is **public** — never commit secrets, credentials, real mailbox
  addresses, or customer/case data.
- Never commit to `main` — work on a branch (`claude/<name>-<id>`) and open a PR.
  *(A `PreToolUse` hook enforces this.)*

## The map

```
apps/       insolvia_app (Flutter desktop+web) · insolvia_marketing (React SSR)
packages/   insolvia_tokens · insolvia_design_system · insolvia_design_system_react · insolvia_api_client
services/   api · mailer            (Python on Lambda)
infra/      Terraform: ci-trust · shared · staging · prod
```

Every app / package / service and `infra/` has its **own `CLAUDE.md`** (that
area's rules — it auto-loads when you work there; read it before editing) and a
`README.md` (for humans). One owner per fact — link, never restate.

## Catalog — need this? read that

| When you're… | Open |
|---|---|
| working in any app/package/service/infra | that directory's `CLAUDE.md` |
| running or building anything | `insolvia-scripts` skill → [`scripts/README.md`](scripts/README.md) |
| deploying / shipping / applying to staging or prod | `insolvia-deploy` skill — **deploys run in CI, never from your CLI** |
| hitting AWS auth / credential errors | `insolvia-aws-auth` skill |
| changing the CI deploy role's IAM | `insolvia-deploy-role-permissions` skill |
| adding a new package/app/service | `insolvia-new-package` skill |
| **changing either design-system package** | `insolvia-design-system-pr` skill — **its own PR + a version bump** |
| publishing a package / bumping versions | [`docs/PACKAGE_PUBLISHING.md`](docs/PACKAGE_PUBLISHING.md) |
| touching env model, hosting, or PR-gate design | [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) |
| touching Terraform state / naming / deploy order | [`docs/TERRAFORM_ARCHITECTURE.md`](docs/TERRAFORM_ARCHITECTURE.md) |
| doing one-time AWS/GitHub bootstrap | [`docs/AWS_SETUP.md`](docs/AWS_SETUP.md) |
| working on mail / SES | [`docs/EMAIL_SETUP.md`](docs/EMAIL_SETUP.md) · [`docs/SES_PRODUCTION_ACCESS.md`](docs/SES_PRODUCTION_ACCESS.md) |
| looking for any other runbook | [`docs/README.md`](docs/README.md) |
