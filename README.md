# Insolvia

Modern, cross-platform bankruptcy case-preparation & e-filing for consumer
bankruptcy law firms — a competitor to Best Case by Stretto. One **Dart/Flutter**
codebase ships a native **desktop** app *and* a **web** app, so we can meet
desktop-loyal attorneys where they are.

> **Agents:** read [`CLAUDE.md`](CLAUDE.md) first — it is the source of truth for
> conventions in this monorepo.

## Layout

Runnable apps in `apps/`, shared libraries in `packages/`, backend services in
`services/`, infrastructure in `infra/`. The full annotated map is in
[`CLAUDE.md`](CLAUDE.md).

| Path | What |
|---|---|
| [`apps/insolvia_app/`](apps/insolvia_app/) | The Insolvia app — desktop + web (themed, feature-first hello-world today). |
| [`apps/insolvia_marketing/`](apps/insolvia_marketing/) | Marketing site for `www.insolvia.ai` — React Router v7, SSR. |
| [`packages/`](packages/) | Shared libraries: design tokens, the Flutter + React design systems, the Dart API client. |
| [`services/`](services/) | Backend services (Python on Lambda): `api`, `mailer`. |
| [`infra/`](infra/) | AWS infrastructure (Terraform): `ci-trust`, `shared`, `staging`, `prod`. |
| [`docs/`](docs/) | [Business plan](docs/business-plan.html) + engineering runbooks. |

## Getting started

The `scripts/` directory is the toolchain — prefer it over hand-running
`fvm`/`melos`/`npm`. One-time system setup (Homebrew installs Terraform, AWS CLI,
Node, the pinned Flutter via FVM, Melos, Python), idempotent and re-runnable:

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

**Prerequisites the scripts can't install for you:** [Docker
Desktop](https://www.docker.com/products/docker-desktop/) (the services run in
compose) and, for macOS desktop builds, full **Xcode** (Command Line Tools alone
are not enough).

### The macOS desktop build is unsigned (for now)

`flutter build macos` produces an app that is **not yet code-signed/notarized**,
so on first launch Gatekeeper blocks it: **right-click the app → Open → Open**
(a one-time step per download). Signing/notarization is on the roadmap.

## Deployment

Deploys run through GitHub Actions (AWS via OIDC) — see
[`docs/AWS_SETUP.md`](docs/AWS_SETUP.md) and
[`docs/TERRAFORM_ARCHITECTURE.md`](docs/TERRAFORM_ARCHITECTURE.md). Shared
infra is applied and the `*.insolvia.ai` ACM cert is `ISSUED`, so pushes to
`main` deploy for real; CI also builds and uploads web + macOS artifacts.

- **staging** → `staging-app.insolvia.ai` (auto, on merge to `main`)
- **production** → `app.insolvia.ai` (manual, gated)
