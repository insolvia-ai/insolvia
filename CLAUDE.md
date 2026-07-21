# Claude Instructions – Insolvia Monorepo

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
| `packages/insolvia_tokens/` | Stack-agnostic design tokens (`tokens.json`) + the generator that renders them into Dart and Tailwind CSS. | Pure Dart (`insolvia_tokens`) |
| `packages/insolvia_design_system/` | Shared UI: tokens, theme, components. The one deliberately shared package. | Flutter package (`insolvia_design_system`) |
| `packages/insolvia_design_system_react/` | Marketing-site UI only: six components (`Button`, `Card`, `NavBar`, `Footer`, `Accordion`, `Field`) on Base UI + Tailwind v4, published as `@insolvia/design-system`. **Outside the pub workspace** (npm, not pub). | React/TypeScript |
| `apps/insolvia_app/` | The Insolvia application (hello-world today). | Flutter app (`insolvia_app`), desktop + web |
| `infra/` | All AWS infrastructure. | Terraform |
| `docs/` | Business plan + engineering runbooks. | Markdown |

Workspace tooling: **pub workspaces** (root `pubspec.yaml`) + **Melos** (`melos.yaml`), Flutter pinned via **FVM** (`.fvmrc`).

## Environment access

- **AWS:** Insolvia runs in its **own dedicated AWS account** (`521762924626`), separate from `andreas-services`. Resources are still namespaced by the `insolvia` project and by environment. (This is why `infra/envs/shared` **creates** the GitHub OIDC provider rather than reading it as a `data` source — there is exactly one per account.)
- **GitHub:** `insolvia-ai/insolvia` — **public** (verified: `gh api repos/insolvia-ai/insolvia --jq .visibility`). Treat everything committed here as world-readable: no secrets, no real mailbox addresses, no customer or case data. Secrets belong in SSM SecureString or GitHub secrets and are injected at deploy time, never committed. Deploys authenticate to AWS via GitHub OIDC — **no long-lived AWS keys anywhere**. The org login is **lowercase** (`gh api orgs/insolvia-ai --jq .login`); the display name "Insolvia AI" is not the login. GitHub emits the stored casing in the OIDC `sub`, and the IAM `StringLike` condition is case-sensitive — a mismatch fails `AssumeRoleWithWebIdentity` with an error that names nothing useful. Keep it lowercase everywhere.
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
`insolvia-github-actions` IAM deploy role. (Insolvia has its own dedicated AWS
account — `521762924626` — so `shared` creates the OIDC provider itself; there
is exactly one per account.)

## Patterns Every Package Follows

### Flutter app (`apps/insolvia_app/`)
- Depends on the design system by **path**: `insolvia_design_system: { path: ../../packages/insolvia_design_system }`.
- **Feature-first** layout under `lib/src/`: `features/<feature>/{data,domain,presentation}`, plus shared `routing/` (go_router) and `config/`. `main.dart` stays thin (`runApp(InsolviaApp())`); the app shell lives in `src/app.dart`.
- No hard-coded colors/spacing/fonts — pull everything from the design system's theme and `ThemeExtension`s.
- Environment config lives in `lib/src/config/environment.dart`, read from `--dart-define=INSOLVIA_ENV`.
- Targets checked in: `web/`, `macos/`. Others added as needed.

### Design tokens (`packages/insolvia_tokens/`)
- `tokens.json` is the **single source of truth** for every color, spacing, radius, shadow, and font value. It is pure data — no Flutter, no CSS.
- `tool/generate_tokens.dart` (plain Dart, zero deps) renders it into `packages/insolvia_design_system/lib/src/tokens/{colors,spacing,radii,semantics}.dart` **and** `packages/insolvia_design_system_react/src/styles/theme.css` (Tailwind v4 `@theme`).
- **Never hand-edit a generated file** — each opens with a `DO NOT EDIT` banner. Edit `tokens.json`, then `melos run tokens`. `melos run tokens:check` (also a step in `design-system-pr.yml`) fails the PR on drift.
- Raw palette names (`ink`/`brass`/`paper`) are an implementation detail. Consumers speak only the **semantic** layer (`primary`, `accent`, `bg`, `ink`, `muted`, `line`, `card`, `danger`, …), so a re-brand is a one-file change.

### Design system (`packages/insolvia_design_system/`)
- Structure: `lib/src/{tokens,theme,components}/`, one barrel export `lib/insolvia_design_system.dart`. Everything under `tokens/` except `typography.dart` is generated (see above).
- Themes and components read `InsolviaSemanticColors`, never `InsolviaPalette`. Brand-specific values Material lacks go in a `ThemeExtension` (`InsolviaColors`, `InsolviaSpacing`), read via `Theme.of(context).extension<...>()`.
- Every exported component has at least one widget test.

### React design system (`packages/insolvia_design_system_react/`)
- npm package `@insolvia/design-system`. **Not a pub workspace member** — do not add it to the root `pubspec.yaml`; it has no `pubspec.yaml` of its own.
- **Hard scope limit: six components** (`Button`, `Card`, `NavBar`, `Footer`, `Accordion`, `Field`). This package serves the marketing site only. `app.insolvia.ai` and the desktop app are Flutter and stay Flutter, so nothing here can ever be shared with them — every extra React component is a parity-drift liability. Adding a seventh needs an explicit scope decision.
- `src/styles/theme.css` is **generated** by `packages/insolvia_tokens/tool/generate_tokens.dart`. Never hand-edit it; `tsup` copies it verbatim to `dist/` via `publicDir` and never writes back.
- Components style themselves from **semantic** Tailwind tokens (`bg-bg`, `text-ink`, `border-line`, `bg-primary`, …) — never a hard-coded hex.
- Every exported component has at least one **behavioural** test (Vitest + Testing Library), mirroring the Flutter package's rule. No snapshot tests.

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
- GitHub Environments mirror the Terraform envs one-for-one: `insolvia-shared`, `insolvia-staging`, `insolvia-production`. A deploy job **must** declare `environment:` — environment-scoped secrets are invisible to jobs that don't, resolving silently to empty strings rather than erroring. Never borrow another environment's name to reach its secrets; that hands the job every secret that environment holds.
- Static web deploy: `s3 sync` hashed assets `Cache-Control: public,max-age=31536000,immutable` (exclude `*.html`), then HTML `no-cache`, then CloudFront `/*` invalidation.
- **Deploys are currently gated OFF** via the `DEPLOY_ENABLED` repo variable (`false`). CI still builds and uploads web + macOS artifacts; only the deploy/apply jobs are skipped.
  - DNS is **live** as of 2026-07-21 (`insolvia.ai` registered at Gandi, NS delegated to Route53 zone `Z01038711J6IZ68FD6ZDW`) — the domain is no longer the blocker.
  - The gate now stays until **`infra/envs/shared` is applied** (issue #15 / 1.3) **and** the `*.insolvia.ai` **ACM cert reaches `ISSUED`** (issue #16 / 1.3b). Every downstream env looks the cert up with `statuses = ["ISSUED"]`, so flipping early fails at plan time with a misleading "no matching certificate" error.
  - Once both hold, flip it: `gh variable set DEPLOY_ENABLED --repo insolvia-ai/insolvia --body "true"`. Nothing in the workflows needs to change.

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
