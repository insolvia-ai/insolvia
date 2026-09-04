# Insolvia

Modern bankruptcy case-preparation & e-filing for consumer bankruptcy law firms —
a competitor to Best Case by Stretto. The wedge is *seamlessness*: living inside
the firm's existing workflow instead of being one more disconnected petition
silo. One **TypeScript** codebase: the **web** app we promote, built with
**React Native on Expo**, with mobile held open by `expo prebuild` and nothing
committed until we want it.

> **Agents:** read [`CLAUDE.md`](CLAUDE.md) first — it is the source of truth for
> conventions in this monorepo.

## Layout

Runnable apps in `apps/`, shared libraries in `packages/`, backend services in
`services/`, infrastructure in `infra/`. The full annotated map is in
[`CLAUDE.md`](CLAUDE.md).

| Path | What |
|---|---|
| [`apps/insolvia_app/`](apps/insolvia_app/) | The Insolvia app — React Native on Expo, web today (themed hello-world). |
| [`apps/insolvia_admin/`](apps/insolvia_admin/) | Internal staff portal — firm provisioning, Vite SPA, Google Workspace sign-in. |
| [`apps/insolvia_marketing/`](apps/insolvia_marketing/) | Marketing site for `www.insolvia.ai` — React Router v7, SSR. |
| [`packages/`](packages/) | Shared libraries: the API client (TypeScript) and `insolvia_core`, the Python services' shared domain. |
| [`services/`](services/) | Backend services (Python on Lambda): `api`, `admin`, `mailer`, `mcp` (the remote MCP server). |
| [`infra/`](infra/) | AWS infrastructure (Terraform): `ci-trust`, `shared`, `staging`, `prod`. |
| [`docs/`](docs/) | [Business plan](docs/business/business-plan.html) + engineering runbooks. |

## Getting started

The `scripts/` directory is the toolchain — prefer it over hand-running `npm` or
`npx expo`. One-time system setup (Homebrew installs Terraform, AWS CLI, Node,
Python), idempotent and re-runnable:

```bash
./scripts/dev-setup.sh          # add --check to report without installing
```

Then set up and run whichever thing you're working on — each app/package/service
has its own `scripts/dev-setup.sh` and `scripts/dev-up.sh`:

```bash
apps/insolvia_app/scripts/dev-setup.sh   &&  apps/insolvia_app/scripts/dev-up.sh
apps/insolvia_marketing/scripts/dev-setup.sh  &&  apps/insolvia_marketing/scripts/dev-up.sh
services/api/scripts/dev-setup.sh  &&  services/api/scripts/dev-up.sh
```

The full catalogue — including per-machine dev AWS resources and deploys — is in
[`scripts/README.md`](scripts/README.md).

**A prerequisite the scripts can't install for you:** [Docker
Desktop](https://www.docker.com/products/docker-desktop/) — the backend services
run in compose. Nothing here needs Xcode: there are no native builds in the repo.

### There is no desktop or mobile build

The app targets **web**, and that is the only artifact CI produces. There are no
macOS or Windows builds (see decision D9 in
[`docs/plan.md`](docs/plan.md)) and nothing committed under `ios/` or
`android/` — `npx expo prebuild` generates those from `app.json` if and when we
want them.

We use **Expo's free tier only**: no EAS Build, Submit, Update or Hosting, and no
Expo account. A CI guard enforces it. Deploys go to our own AWS, through
Terraform, like everything else.

## Deployment

Deploys run through GitHub Actions (AWS via OIDC) — see
[`docs/runbooks/aws-bootstrap.md`](docs/runbooks/aws-bootstrap.md) and
[`docs/reference/terraform.md`](docs/reference/terraform.md). Shared
infra is applied and the `*.insolvia.ai` ACM cert is `ISSUED`, so pushes to
`main` deploy for real.

- **staging** → `staging-app.insolvia.ai` (auto, on merge to `main`)
- **production** → `app.insolvia.ai` (manual, gated)
