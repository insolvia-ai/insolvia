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
| `packages/insolvia_design_system_react/` | Marketing-site UI only, on Base UI + Tailwind v4, published as `@insolvia-ai/design-system`. Scope-capped at six components — see *Dual-target parity discipline*. **Outside the pub workspace** (npm, not pub). | React/TypeScript |
| `apps/insolvia_app/` | The Insolvia application (hello-world today). | Flutter app (`insolvia_app`), desktop + web |
| `apps/insolvia_marketing/` | The marketing site for `www.insolvia.ai` — React Router v7 framework mode, SSR. Consumes `@insolvia/design-system` as a `file:` path dep. **Outside the pub workspace** (npm, not pub). | React/TypeScript (`@insolvia/marketing`) |
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

## Dual-target parity discipline

The design system is **dual-target** (decision D4 in `docs/MVP_PLAN.md`): one
token source of truth, rendered into a Flutter package and a React package.
Tokens are the *only* thing the two targets share. A Flutter widget and a React
component cannot share a line of code, so anything implemented in both is
implemented twice and will drift — silently, because nothing compiles across the
boundary to tell you it has.

Two rules contain that. They are rules, not preferences.

**Rule 1 — the React package stays scoped to what the marketing site actually
renders.** `app.insolvia.ai` and the macOS/Windows desktop app are Flutter and
**stay Flutter**; the React package exists solely to serve
`www.insolvia.ai`. The surface today is **six components — `Button`, `Card`,
`NavBar`, `Footer`, `Accordion`, `Field`** — exported from
`packages/insolvia_design_system_react/src/index.ts`. That barrel is the answer
to "has it grown?"; read it rather than counting directories. This package is
deliberately *not* a port of the ~40 Base UI wrappers in `andreas-services`.
Every component here that the marketing site does not render is a second
implementation of something the Flutter design system already owns, and it is a
permanent parity liability. The marketing site's needs are the ceiling, not the
floor.

**Rule 2 — generated token files are never hand-edited.** Five files are
generated from `packages/insolvia_tokens/tokens.json` by
`packages/insolvia_tokens/tool/generate_tokens.dart`:
`packages/insolvia_design_system/lib/src/tokens/{colors,spacing,radii,semantics}.dart`
and `packages/insolvia_design_system_react/src/styles/theme.css`. Each opens
with a `DO NOT EDIT` banner. To change a color, radius, spacing step, or font:
edit `tokens.json`, then `melos run tokens`. A hand-edit is not a shortcut — it
is a divergence between the two targets' brand values, which is exactly the
drift the shared token source exists to prevent.

### How each rule is actually enforced — and the asymmetry

Be clear-eyed about this; a rule readers believe is machine-enforced when it is
not is worse than no rule at all.

- **Rule 2 is machine-enforced.** `melos run tokens:check` runs the generator
  with `--check`, which regenerates all five outputs in memory and exits
  non-zero on any drift. CI runs it as the **`Token drift check`** step of
  `.github/workflows/design-system-pr.yml`. That gate runs on every PR but only
  does real work when its `steps.filter` guard matches (see *PR gates, required
  status checks, and why no `paths:` filter* below). The guard's path list names
  packages individually rather than globbing `packages/**`, and one entry is the
  single file `packages/insolvia_design_system_react/src/styles/theme.css` — the
  generated CSS lives in the React package but is an output of the *Dart*
  generator, so the gate that can check it has to be woken by it. Drop that line
  and a PR hand-editing only `theme.css` runs no drift check at all: the React
  gate doesn't have one, and this one would no-op.
- **Rule 1 has no automated check whatsoever.** Nothing counts components,
  diffs the barrel, or fails a build when a seventh appears. It depends entirely
  on review. A reviewer seeing a new directory under
  `packages/insolvia_design_system_react/src/components/` is the whole
  mechanism.

### Changing the rules

A rule with no legitimate escape hatch gets quietly broken instead of amended.
If a seventh React component is genuinely warranted — the marketing site needs
a surface none of the six can express — that is a scope decision, not a
judgement call inside a feature PR. Make it explicitly: in the same PR that adds
the component, name the marketing page that renders it, update the component
list in this section, in
`packages/insolvia_design_system_react/README.md`, and in the package table
above, and note the decision against D4 in `docs/MVP_PLAN.md`. The component
carries the same behavioural-test rule as the other six. A PR that grows the
surface without touching this section is the failure mode, and the reviewer's
job is to reject it — the count in this file is the contract.

## Patterns Every Package Follows

### Flutter app (`apps/insolvia_app/`)
- Depends on the design system by **path**: `insolvia_design_system: { path: ../../packages/insolvia_design_system }`.
- **Feature-first** layout under `lib/src/`: `features/<feature>/{data,domain,presentation}`, plus shared `routing/` (go_router) and `config/`. `main.dart` stays thin (`runApp(InsolviaApp())`); the app shell lives in `src/app.dart`.
- No hard-coded colors/spacing/fonts — pull everything from the design system's theme and `ThemeExtension`s.
- Environment config lives in `lib/src/config/environment.dart`, read from `--dart-define=INSOLVIA_ENV`.
- Targets checked in: `web/`, `macos/`. Others added as needed.

### Design tokens (`packages/insolvia_tokens/`)
- `tokens.json` is the **single source of truth** for every color, spacing, radius, shadow, and font value. It is pure data — no Flutter, no CSS.
- `tool/generate_tokens.dart` (plain Dart, zero deps) renders it into four Dart files for the Flutter package **and** the React package's `theme.css` (Tailwind v4 `@theme`) — the five outputs are listed under *Dual-target parity discipline*.
- **Never hand-edit a generated file** — see *Dual-target parity discipline* above for the rule and how CI enforces it.
- Raw palette names (`ink`/`brass`/`paper`) are an implementation detail. Consumers speak only the **semantic** layer (`primary`, `accent`, `bg`, `ink`, `muted`, `line`, `card`, `danger`, …), so a re-brand is a one-file change.

### Design system (`packages/insolvia_design_system/`)
- Structure: `lib/src/{tokens,theme,components}/`, one barrel export `lib/insolvia_design_system.dart`. Everything under `tokens/` except `typography.dart` is generated (see above).
- Themes and components read `InsolviaSemanticColors`, never `InsolviaPalette`. Brand-specific values Material lacks go in a `ThemeExtension` (`InsolviaColors`, `InsolviaSpacing`), read via `Theme.of(context).extension<...>()`.
- Every exported component has at least one widget test.

### React design system (`packages/insolvia_design_system_react/`)
- npm package `@insolvia-ai/design-system`, published to **GitHub Packages**.
  The scope is a contract with the registry: GitHub Packages only accepts a
  scope equal to the owning org's login (`insolvia-ai`), and rejects anything
  else with a misleading "installation does not exist" 403 — same family of
  trap as the lowercase-org-login OIDC note under *Environment access*. Keep it
  `@insolvia-ai`. **Not a pub workspace member** — do not add it to the root
  `pubspec.yaml`; it has no `pubspec.yaml` of its own.
- **Every change to this package must bump `version` in `package.json`.** Merge
  to `main` auto-publishes to GitHub Packages
  (`design-system-react-publish.yml`), and that publish is idempotent by
  version — an unbumped merge publishes nothing and the registry silently goes
  stale. Consumers (the marketing site) install the published
  `@insolvia-ai/design-system`, **never** a committed `file:`/path dependency;
  a local `file:` override is a legitimate *uncommitted* debugging aid, nothing
  more. This rule is **machine-enforced**: the *Require a version bump when the
  package changed* step in `design-system-react-pr.yml` diffs the package
  against the PR base and fails when it changed with an unchanged version. The
  no-path-dep half is review-enforced only — nothing scans consumer
  `package.json`s for a committed `file:`. See `docs/PACKAGE_PUBLISHING.md`.
- **Hard scope limit: six components**, and `src/styles/theme.css` is **generated** — both rules, their reasoning, and their enforcement live in *Dual-target parity discipline* above. Read it before adding anything here.
- `tsup` copies `theme.css` verbatim to `dist/` via `publicDir` and never writes back, so `dist/` is not a second place to edit it either.
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

### PR gates, required status checks, and why no `paths:` filter

Every `*-pr.yml` workflow triggers on **every** pull request. None of them has an
`on.pull_request.paths:` filter. This is deliberate and load-bearing — do not
"tidy it up" by putting the filters back.

A branch ruleset requires a status check **by name**, and then waits for it. A
`paths:`-filtered workflow does not run on a PR that misses its filter, so its
check is never *reported* — not failed, never reported. GitHub parks the PR on
"Expected — waiting for status to be reported" and it can never merge. With
required checks on, a `paths:` filter on the Flutter gate makes every docs-only
PR permanently unmergeable. The failure looks like a GitHub bug rather than a
config mistake, which is what makes it worth writing down.

So instead each job **always runs** and guards its own work:

- `.github/actions/changed-paths` (local composite action, plain `git` + bash)
  diffs the PR against `github.event.pull_request.base.sha` and outputs
  `run=true|false`. Checkout uses `fetch-depth: 0` so the base commit is
  present. Every failure inside it is a hard error — it never falls back to
  `false`, because a guard that silently answers "nothing changed" would turn
  every gate in the repo green without running it.
- Each real step carries `if: steps.filter.outputs.run == 'true'`.
- **Step-level `if:`, never job-level.** A skipped job reports conclusion
  `skipped`, and whether that satisfies a required status check is a subtlety
  this design refuses to rest on. Always-runs-and-reports-`success` is
  unambiguous.

The path list lives in the `with: paths:` block of the guard step, in the same
syntax the old `paths:` filter used (blank lines and `#` comments are ignored,
so the reasoning for each entry stays next to it). Each workflow still lists
itself, plus the shared action, so changing a gate re-runs it.

Cost of the design: an irrelevant PR runs each job for a few seconds
(checkout + diff) instead of not at all. That is the price of a check that can
be required.

### Required status checks — pending manual step

`protect-main` currently has **no required status checks**, so red CI cannot
block a merge. The workflows above are now shaped to allow turning them on. The
remaining step is a **repo-settings change that must be made by a human in the
GitHub UI or API** — nothing in this repo can grant itself branch protection.

In `protect-main` → *Require status checks to pass*, add exactly these eight,
which are the job `name:` values (matrix legs get a `(leg)` suffix):

| Check name | Workflow |
|---|---|
| `Flutter app` | `app-pr.yml` |
| `macOS build` | `app-pr.yml` |
| `Flutter design system` | `design-system-pr.yml` |
| `React design system` | `design-system-react-pr.yml` |
| `Inbound forwarder` | `inbound-forwarder-pr.yml` |
| `Terraform validate (shared)` | `shared-infra-plan.yml` |
| `Terraform validate (staging)` | `shared-infra-plan.yml` |
| `Terraform validate (prod)` | `shared-infra-plan.yml` |

These strings are a **contract with the ruleset**. Renaming a job `name:`, or
renaming a matrix leg, silently orphans the required check — the ruleset waits
forever for a name nobody reports. Change one only alongside the ruleset.

Also enable *Require branches to be up to date before merging*, and set
`required_approving_review_count` to 1 with `require_code_owner_review: true`
if CODEOWNER review is wanted (`.github/CODEOWNERS` already assigns `@ansavva`,
but the ruleset does not currently enforce it — see *Branch Conventions*).

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
- `main` is protected by the `protect-main` ruleset. **What it actually enforces
  today** (verify, don't assume: `gh api repos/insolvia-ai/insolvia/rulesets/18947945 --jq .rules`):
  - a pull request is required — no direct pushes;
  - linear history; no force-push; no branch deletion;
  - squash or rebase merges only;
  - review threads must be resolved, and pushes dismiss stale reviews.
- **Not** enforced today, despite `.github/CODEOWNERS` existing:
  `required_approving_review_count` is `0` and `require_code_owner_review` is
  `false`, so a PR can be merged with **no approval at all**; and there are **no
  required status checks**, so a PR with red CI can be merged. CODEOWNERS only
  requests @ansavva's review, it does not gate the merge.
- Turning the status checks on is a manual repo-settings step — the check names
  to require and the reason the workflows are shaped the way they are live under
  *PR gates, required status checks, and why no `paths:` filter*.
