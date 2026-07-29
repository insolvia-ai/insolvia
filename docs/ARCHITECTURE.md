# Architecture

## Monorepo shape

```
insolvia/
├── apps/
│   ├── insolvia_app/                  Expo app (React Native) — web today
│   │   ├── app.config.ts              Expo config; no eas.json, on purpose
│   │   └── src/
│   │       ├── app/                   Expo Router routes ONLY (+not-found.tsx)
│   │       ├── screens/               screen bodies the routes render
│   │       ├── components/            our design system — RN primitives
│   │       ├── config/                environment.ts (EXPO_PUBLIC_INSOLVIA_ENV)
│   │       └── theme.ts               StyleSheet helpers over the tokens
│   └── insolvia_marketing/            React Router v7 + Vite, SSR
├── packages/
│   ├── insolvia_tokens/               @insolvia-ai/tokens — tokens.json + generator
│   ├── insolvia_design_system_react/  published; marketing's components
│   └── insolvia_api_client/           @insolvia-ai/api-client
├── services/                          api · mailer (Python on Lambda)
├── infra/                             Terraform — ci-trust / shared / staging / prod
└── docs/                              business plan + runbooks
```

Everything is TypeScript. The app follows the layout Expo itself publishes —
`src/app/` is routes-only, screen bodies live in `src/screens/` — see
[ADR 0005](adr/0005-expo-app-layout.md) for why, and
[`apps/insolvia_app/CLAUDE.md`](../apps/insolvia_app/CLAUDE.md) for the rules.
[ADR 0004](adr/0004-react-native-replaces-flutter.md) is the stack decision
behind all of it, including the measurements that ruled out a component library.

- **Workspace resolution:** npm workspaces, root `package.json`. The member list
  is **explicit and never `packages/*`** — globbing would swallow
  `insolvia_design_system_react`, which marketing consumes *by published
  version*. The reasoning is in the root `package.json`'s own comments; read
  them before adding a member.
- **Not members, deliberately:** `apps/insolvia_marketing` and
  `packages/insolvia_design_system_react`. Each keeps its own lockfile and its
  own CI job that installs from it, because Node resolution walks *up* the tree
  and would otherwise let a missing dependency resolve from the root.
- **Two design systems, one token source.** The React package serves marketing;
  the app has its own React Native components. They share token *values* only,
  generated from `packages/insolvia_tokens/tokens.json` into a Tailwind `@theme`
  block for marketing and a typed `tokens.ts` for the app — see
  [`PACKAGE_PUBLISHING.md`](PACKAGE_PUBLISHING.md).

## Toolchain

| | |
|---|---|
| Node | ≥ 24 (`engines.node`), installed by `scripts/dev-setup.sh` |
| App | **Expo SDK 57**, pinned exact · Metro · Expo Router (`web.output: "single"`) |
| Marketing | React Router v7 framework mode · Vite |
| Services | Python 3.12 on Lambda |
| Lint/format | ESLint 9 + Prettier, fanned out per workspace member |

**Expo's free tier only — and CI enforces it.** No EAS Build, Submit, Update or
Hosting, and no Expo account anywhere in the pipeline. Every part of Expo we
depend on (the SDK, Metro, Expo Router, `expo export`, `expo prebuild`) is open
source and runs locally; EAS is a paid service that would put a vendor account
on the critical path of a deploy that is otherwise entirely ours — and
`eas deploy` targets Cloudflare Workers, which `infra/modules/web_hosting`
already does for free under Terraform. A guard step in `app-pr.yml` fails the
build on an `eas.json`, an `eas-cli` dependency, or a `.eas/` directory, because
this is a constraint that erodes by accident: several vendored agent skills in
`.agents/skills/` present EAS as the normal way to ship. Root
[`CLAUDE.md`](../CLAUDE.md) carries the applicability table for those.

**Desktop is not built.** No macOS or Windows targets, no per-OS CI jobs, no
artifact hosting. Decision D9 in [`MVP_PLAN.md`](MVP_PLAN.md) records the trade
— under Flutter one toolchain built every target, so the option was nearly free;
under React Native it would be a port. **Mobile is the held-open target now**,
and `expo prebuild` holds it open with nothing committed under `ios/`/`android/`
and no CI job at all.

## Environment model (staging vs production)

The app is a single bundle configured at **build time** — no separate codepaths.
Selection is one inlined environment variable:

```bash
EXPO_PUBLIC_INSOLVIA_ENV=staging npx expo export -p web   # or production, or local (default)
```

`apps/insolvia_app/src/config/environment.ts` reads it and exposes a typed
`AppEnvironment` (label, `isProduction`, API base URLs). The home screen renders
the active environment so staging vs prod is visually obvious.

**The `EXPO_PUBLIC_` prefix is not a style choice.** Expo inlines *only*
variables named `EXPO_PUBLIC_*` into the client bundle; anything else is simply
absent at runtime, with no error. That is why the old `--dart-define`
`INSOLVIA_ENV` became `EXPO_PUBLIC_INSOLVIA_ENV` rather than keeping its name —
the rename was forced by the bundler, and a stray `INSOLVIA_ENV` in a workflow
would read as `local` in production.

The corollary is that **nothing secret may go in an `EXPO_PUBLIC_*` variable.**
Everything so prefixed is compiled into a public static asset. Per
[ADR 0001](adr/0001-client-stays-dumb-trust-boundary.md) the client holds no
credentials anyway, so there is nothing that wants to be there.

## Web hosting topology

`expo export -p web` produces a **static** SPA (`web.output: "single"`), so
hosting is unchanged from the Flutter era and intentionally compute-free:

```
Route53 (A-alias)  →  CloudFront (wildcard ACM TLS, SPA rewrite, /* -> index.html on 403/404)  →  S3 (private, OAC)
```

- `staging-app.insolvia.ai` → staging distribution/bucket
- `app.insolvia.ai` → prod distribution/bucket

The **marketing site** (`apps/insolvia_marketing`) does not use this topology —
it is server-side rendered, so it gets its own module with an SSR Lambda behind
the same CloudFront front (`infra/modules/marketing_site`, see
[`TERRAFORM_ARCHITECTURE.md`](TERRAFORM_ARCHITECTURE.md)). It runs in both
environments:

- `staging-www.insolvia.ai` → staging distribution (noindexed, no apex)
- `www.insolvia.ai` + the `insolvia.ai` apex 301 → prod distribution

Only prod owns the apex — a hosted zone has exactly one, so staging passes
`apex_domain = null` and the module omits the alias, the records, and the
redirect.

## CI/CD

See `.github/workflows/`. Each area has a `*-pr.yml` (checks) and, where it
deploys, a `*-<env>.yml`. Deploys are live: shared infra is applied, the
`*.insolvia.ai` ACM cert is `ISSUED`, and merges to `main` deploy staging for
real (prod is dispatched manually).

### Production deploys promote; they do not rebuild

Merging to `main` ships staging. Production is a separate, manual dispatch —
`./scripts/prod-deploy.sh <target>`, or `release` to ship one commit to every
service in order. Three things make that dispatch safe:

- **It ships the artifact staging validated.** The container repositories are
  shared across environments (`infra/envs/shared`), so the image staging tested
  is already in the repository prod pulls from. A prod deploy resolves the
  commit's `sha-<commit>` tag to an immutable digest and deploys *that* — there
  is no `docker build` in any prod workflow. The app is the one exception and
  rebuilds, because it inlines its environment at build time (see *Environment
  model* above); it pins an exact Expo SDK version so "same source" also means
  "same bundler".
- **It refuses commits staging never blessed.** `.github/actions/verified-commit`
  fails the run unless that exact commit has a successful `*-staging.yml` run.
  There is no `workflow_run` chain, so ordering is asserted rather than assumed.
  `force: true` bypasses it for a hotfix, loudly, in the job summary.
- **Traffic moves last.** The API and marketing SSR Lambdas sit behind a `live`
  alias. A deploy publishes a new version, smoke-tests it by its own version
  ARN while nothing routes to it, and shifts the alias only on success — so a
  failed smoke test leaves the previous version serving instead of leaving a
  broken build live. Rollback is `aws lambda update-alias --function-version
  <previous>`: seconds, no rebuild, no image pull.

Prod deploys no longer run `terraform apply`. Applying prod infrastructure is
`infra-prod.yml` alone (`prod-deploy.sh prod-infra`, `mode: plan` by default),
so a routine code deploy cannot carry unrelated infra drift into production.

The human gate is the `insolvia-production` GitHub Environment's **required
reviewers** — like the required status checks below, that is a repo-settings
change nothing in this repo can make for itself.

### PR gates have no `paths:` filter — on purpose

Every `*-pr.yml` triggers on **every** pull request, and each job guards its own
work with `.github/actions/changed-paths` (a local composite action: `git diff`
against the PR base, output `run=true|false`) plus a step-level
`if: steps.filter.outputs.run == 'true'` on each real step.

The reason is that these jobs are meant to be **required status checks**. A
ruleset waits for a check *by name*. A `paths:`-filtered workflow simply does not
run on a PR that misses its filter, so its check is never reported — GitHub then
parks the PR on "Expected — waiting for status to be reported" forever, and a
docs-only PR can never merge. A job-level `if:` is not a fix either: it reports
`skipped`, and we do not want the merge gate resting on whether GitHub counts
`skipped` as satisfied. So the jobs always run and always report; on an
irrelevant PR they succeed in a few seconds having done nothing.

**Restoring a `paths:` filter here would silently re-break the merge gate.** If
you find yourself "cleaning that up", read
`.github/actions/changed-paths/action.yml` first.

### Branch protection — what `protect-main` enforces (and doesn't)

`main` is protected by the `protect-main` ruleset. Verify, don't assume — and
resolve it **by name, never by id**:

```bash
scripts/update-ruleset.sh show
```

**Do not hard-code a ruleset id here or anywhere else.** A ruleset recreated in
the UI comes back with a new id, and the stale one 404s with no hint that the
number is the problem. This document used to print `18947945`; the live ruleset
is a different id today, which is exactly why the script resolves
`name == "protect-main"` through `/repos/{owner}/{repo}/rulesets` and then uses
whatever id that returns. If you need the raw JSON, get the id the same way
first.

**Enforced today:** a PR is required (no direct pushes); linear history; no
force-push; no branch deletion; squash or rebase merges only; review threads must
be resolved, and pushes dismiss stale reviews. **Plus the nine required status
checks below** — red CI cannot merge.

**Not enforced today** (despite `.github/CODEOWNERS` existing):
`required_approving_review_count` is `0` and `require_code_owner_review` is
`false` — a PR can merge with **no approval**. CODEOWNERS only *requests* the
code owner's review; it does not gate the merge. That is deliberate and is the
last paragraph of this section.

### Required status checks

Red CI blocks a merge to `main`. The `protect-main` ruleset requires these
**nine** job `name:` values (matrix legs get a `(leg)` suffix):

| Check name | Workflow |
|---|---|
| `App` | `app-pr.yml` |
| `React design system` | `design-system-react-pr.yml` |
| `Marketing site` | `marketing-pr.yml` |
| `API service` | `api-pr.yml` |
| `Mailer service` | `mailer-pr.yml` |
| `API client` | `api-client-pr.yml` |
| `Terraform validate (shared)` | `shared-infra-plan.yml` |
| `Terraform validate (staging)` | `shared-infra-plan.yml` |
| `Terraform validate (prod)` | `shared-infra-plan.yml` |

This list was twelve before the Expo migration. Four checks went away with the
Flutter stack — `Flutter app`, `macOS build` and `Windows build` collapsed into
the single `App` job, and `Flutter design system` disappeared with its package —
and `Dart API client` was renamed `API client`. **A rename is a ruleset change**,
per the contract note below.

`Terraform validate (ci-trust)` and `(dev)` run alongside the three above but
are deliberately **not** required — neither environment is ever applied by CI,
so they exist for coverage rather than as gates. The reasoning is in
`shared-infra-plan.yml`'s matrix comment.

**Changing this list does not need a human clicking through settings.** Run
`scripts/update-ruleset.sh` (`show` / `add` / `remove`); the
`insolvia-branch-protection` skill covers the traps, the sharpest being that
`PUT /rulesets/{id}` *replaces* the arrays it receives, so a hand-rolled
`gh api` call carrying only your new check silently deletes `deletion`,
`non_fast_forward` and `required_linear_history` from `main`.

These strings are a **contract with the ruleset**: renaming a job `name:` (or a
matrix leg) silently orphans the required check — GitHub accepts a required
check nobody reports, and every PR then parks on *"Expected — waiting for status
to be reported"* forever. Rename a job only alongside the ruleset.

**Two settings are deliberately off.** *Require branches to be up to date before
merging* (`strict_required_status_checks_policy`) is `false`: with nine checks
it would force a rebase-and-rerun on every PR whenever anything lands first.
`required_approving_review_count` is unset, and should stay that way — Insolvia
is maintained by one person, and GitHub does not let you approve your own PR, so
requiring an approval would block every merge permanently. The gate here is CI,
not review.
