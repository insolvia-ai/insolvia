# Codex Instructions – Insolvia Monorepo

## What this repo is

Insolvia is a modern, cross-platform (native **desktop** + **web**) bankruptcy
case-preparation and e-filing platform for consumer-bankruptcy law firms — a
direct competitor to **Best Case by Stretto**. Our wedge is meeting
desktop-loyal attorneys where they are: one **Dart/Flutter** codebase ships a
real macOS/Windows desktop app *and* a web app.

This is a **monorepo** of independently buildable packages that share one
design system. It intentionally mirrors the conventions of the
`andreas-services` repo (infra layout, OIDC to AWS, Route53 + ACM + CloudFront,
`<project>-<thing>-<env>` naming), adapted from Python/React to Dart/Flutter.

The layout follows the standard Flutter monorepo split: runnable apps in
`apps/`, shared libraries in `packages/` (apps depend on packages, never the
reverse).

| Path | Purpose | Stack |
|---|---|---|
| `packages/insolvia_design_system/` | Shared UI: tokens, theme, components. The one deliberately shared package. | Flutter package (`insolvia_design_system`) |
| `apps/insolvia_app/` | The Insolvia application (hello-world today). | Flutter app (`insolvia_app`), desktop + web |
| `infra/` | All AWS infrastructure. | Terraform |
| `docs/` | Business plan + engineering runbooks. | Markdown |

Workspace tooling: **pub workspaces** (root `pubspec.yaml`) + **Melos** (`melos.yaml`), Flutter pinned via **FVM** (`.fvmrc`).

## Environment access

- **AWS:** reuse the shared account (also home to `andreas-services`); everything Insolvia is namespaced by the `insolvia` project and by environment.
- **GitHub:** `Insolvia-AI/insolvia` (private). Deploys authenticate to AWS via GitHub OIDC — **no long-lived AWS keys anywhere**. The OIDC `sub` is case-sensitive, so keep the org casing exact.
- **Domain:** `insolvia.ai` (staging → `staging-app.insolvia.ai`, prod → `app.insolvia.ai`).

## Environments — staging AND production, always

Unlike `andreas-services` (prod-only), Insolvia runs **`staging` and `prod`** for
everything, plus a `shared` env for account-wide resources. Each environment is
a **separate `infra/envs/<env>/` directory with its own state key** — full state
isolation, never Terraform workspaces.

The app selects its environment at build time via
`--dart-define=INSOLVIA_ENV=staging|production` (defaults to `local`).

## Shared Infrastructure (`infra/`)

`infra/envs/shared/` owns account-wide, environment-independent resources:
Route53 hosted zone for `insolvia.ai`, the wildcard ACM cert `*.insolvia.ai`
(us-east-1, DNS-validated), the account-level **GitHub OIDC provider**, and the
`github-actions-insolvia` IAM deploy role. (Insolvia has its own dedicated AWS
account — `521762924626` — so `shared` creates the OIDC provider itself; there
is exactly one per account.)

## Patterns Every Package Follows

### Flutter app (`apps/insolvia_app/`)
- Depends on the design system by **path**: `insolvia_design_system: { path: ../../packages/insolvia_design_system }`.
- **Feature-first** layout under `lib/src/`: `features/<feature>/{data,domain,presentation}`, plus shared `routing/` (go_router) and `config/`. `main.dart` stays thin (`runApp(InsolviaApp())`); the app shell lives in `src/app.dart`.
- No hard-coded colors/spacing/fonts — pull everything from the design system's theme and `ThemeExtension`s.
- Environment config lives in `lib/src/config/environment.dart`, read from `--dart-define=INSOLVIA_ENV`.
- Targets checked in: `web/`, `macos/`. Others added as needed.

### Design system (`packages/insolvia_design_system/`)
- Structure: `lib/src/{tokens,theme,components}/`, one barrel export `lib/insolvia_design_system.dart`.
- Tokens are the single source of truth. Brand-specific values Material lacks go in a `ThemeExtension` (`InsolviaColors`, `InsolviaSpacing`), read via `Theme.of(context).extension<...>()`.
- Every exported component has at least one widget test.

### Infrastructure
- Terraform `~> 1.5`, AWS provider `~> 5.0`. Region `us-east-1` everywhere (CloudFront ACM requirement).
- Resources named `insolvia-<thing>-<env>`; tags `{ Project = "insolvia", Environment, ManagedBy = "terraform" }`.
- Sensitive variables declared `sensitive = true`, never committed. Commit `terraform.tfvars.example`, never real `*.tfvars`.

### Infrastructure directory naming
- The infra directory is **always** `infra/` — **never** `terraform/`.

### Terraform directory structure
```
infra/
  modules/<concern>/{main,variables,outputs}.tf
  envs/<env>/{main,variables,providers,backend,outputs}.tf + terraform.tfvars.example
```
- State backend: S3 bucket `insolvia-terraform-state`, `encrypt = true`, key `insolvia/<env>/terraform.tfstate`.

### Deployment (CI/CD)
- GitHub Actions only. Auth via OIDC (`AWS_ROLE_ARN` secret), never static keys.
- Workflow names use the `Area · Phase · Env` dotted style; files are `<area>-<env>.yml` (deploy) and `<area>-pr.yml` (PR checks).
- `staging` deploys on push to `main`; `prod` is `workflow_dispatch`-gated behind the `insolvia-production` GitHub Environment.
- Static web deploy: `s3 sync` hashed assets `Cache-Control: public,max-age=31536000,immutable` (exclude `*.html`), then HTML `no-cache`, then CloudFront `/*` invalidation.
- **Deploys are currently gated OFF** until the `insolvia.ai` domain/DNS is live; CI still builds and uploads web + macOS artifacts.

## AWS Credentials — Critical Rule

Never hard-code, echo, or commit AWS credentials. Locally, rely on your own AWS
CLI profile / SSO. In CI, rely on the assumed OIDC role. If a tool needs
credentials it does not have, **stop and ask** — do not invent a workaround.

## Adding a New Package
1. Create it under `packages/<name>/` (shared library) or `apps/<name>/` (runnable app), with its own `pubspec.yaml` (`resolution: workspace`).
2. Add it to the root `pubspec.yaml` `workspace:` list.
3. If it deploys, add `<name>-pr.yml` + `<name>-<env>.yml` workflows and an `infra/envs/*` entry.
4. Document it in this table and in `docs/`.

## Branch Conventions
- Feature branches: `claude/<feature-name>-<id>`.
- `staging` deploys from `main`; `prod` is manually dispatched from `main`.
- `main` is protected: PR + CODEOWNER review + green `*-pr` checks required.
