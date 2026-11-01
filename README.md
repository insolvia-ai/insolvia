# Insolvia

Modern bankruptcy case-preparation & e-filing for consumer bankruptcy law firms —
a competitor to Best Case by Stretto. The wedge is *seamlessness*: living inside
the firm's existing workflow instead of being one more disconnected petition
silo. The **web** app we promote is built with **React Native on Expo**, with
mobile held open by `expo prebuild` and nothing committed until we want it; the
backend is **.NET 10 on AWS Lambda with Aurora PostgreSQL**.

> **Agents:** read [`CLAUDE.md`](CLAUDE.md) first — it is the source of truth for
> conventions in this monorepo.

## Layout

Runnable apps in `apps/`, the API client in `packages/`, the backend in
`backend/`, infrastructure in `infra/`. The full annotated map is in
[`CLAUDE.md`](CLAUDE.md).

| Path | What |
|---|---|
| [`apps/insolvia_app/`](apps/insolvia_app/) | The Insolvia app — React Native on Expo, web today (themed hello-world). |
| [`apps/insolvia_admin/`](apps/insolvia_admin/) | Internal staff portal — firm provisioning, Vite SPA, Google Workspace sign-in. |
| [`apps/insolvia_marketing/`](apps/insolvia_marketing/) | Marketing site for `www.insolvia.ai` — React Router v7, SSR. |
| [`packages/`](packages/) | The API client (TypeScript) the app ships. |
| [`backend/`](backend/) | The backend (.NET 10, PostgreSQL, on Lambda): the API and its job worker, `admin`, `mcp` (the remote MCP server), `filing` (the filing worker — ADR 0024), and the migrator. |
| [`contract/`](contract/) | The backend's HTTP contract suite: offline on every PR, and signed in against staging (after each deploy) or a local stack. |
| [`infra/`](infra/) | AWS infrastructure (Terraform): `staging`, `prod`, and per-machine `dev`. |

The account-wide AWS layer (DNS, certificate, SES, container registries, the CI
deploy role, IAM users) and the transactional **mailer** live in a sibling
repository, [`insolvia-ai/insolvia-services`](https://github.com/insolvia-ai/insolvia-services)
— see [ADR 0025](docs/adr/0025-shared-infra-and-mailer-move-to-insolvia-services.md).
| [`docs/`](docs/) | [Business plan](docs/business/business-plan.html) + engineering runbooks. |

## Getting started

The `scripts/` directory is the toolchain — prefer it over hand-running `npm` or
`npx expo`. One-time system setup (Homebrew installs Terraform, AWS CLI, Node,
the .NET SDK, Python for a few stdlib checkers), idempotent and re-runnable:

```bash
./scripts/dev-setup.sh          # add --check to report without installing
```

Then run everything with `./scripts/dev-up.sh`, or set up and run whichever
thing you're working on — each area has its own `scripts/dev-up.sh`:

```bash
apps/insolvia_app/scripts/dev-setup.sh   &&  apps/insolvia_app/scripts/dev-up.sh
apps/insolvia_marketing/scripts/dev-setup.sh  &&  apps/insolvia_marketing/scripts/dev-up.sh
backend/scripts/dev-up.sh                # Postgres, the migrator, the API (8080) and admin (8090)
```

The full catalogue — including per-machine dev AWS resources and deploys — is in
[`scripts/README.md`](scripts/README.md).

**A prerequisite the scripts can't install for you:** [Docker
Desktop](https://www.docker.com/products/docker-desktop/) — the backend and its
Postgres run in compose, and its data-tier tests start Postgres through
Testcontainers. Nothing here needs Xcode: there are no native builds in the repo.

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
insolvia-services' [`docs/runbooks/aws-bootstrap.md`](https://github.com/insolvia-ai/insolvia-services/blob/main/docs/runbooks/aws-bootstrap.md) and
[`docs/reference/terraform.md`](docs/reference/terraform.md). Shared
infra (applied from insolvia-services) is up and the `*.insolvia.ai` ACM cert is `ISSUED`, so pushes to
`main` deploy for real.

- **staging** → `staging-app.insolvia.ai` (auto, on merge to `main`)
- **production** → `app.insolvia.ai` (manual, gated)
