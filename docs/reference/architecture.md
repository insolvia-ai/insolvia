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
│   │       ├── components/            app-local components — RN primitives
│   │       │                          (Button/Field come from the package)
│   │       ├── config/                environment.ts (EXPO_PUBLIC_INSOLVIA_ENV)
│   │       └── theme.ts               StyleSheet helpers over the tokens
│   └── insolvia_marketing/            React Router v7 + Vite, SSR
├── packages/
│   └── insolvia_api_client/           @insolvia-ai/api-client
├── services/                          api · admin · mailer · mcp (Python on Lambda)
├── infra/                             Terraform — ci-trust / shared / staging / prod
├── tool/                              reconcile-cognito-branding.ts
└── docs/                              business plan + runbooks
```

The design system is **not** in this tree. `@insolvia-ai/design-system` and
`@insolvia-ai/tokens` live in
[`insolvia-ai/design-system`](https://github.com/insolvia-ai/design-system) and
install from GitHub Packages —
[ADR 0010](../adr/0010-design-system-moves-to-its-own-repository.md).

Everything is TypeScript. The app follows the layout Expo itself publishes —
`src/app/` is routes-only, screen bodies live in `src/screens/` — see
[ADR 0005](../adr/0005-expo-app-layout.md) for why, and
[`apps/insolvia_app/CLAUDE.md`](../../apps/insolvia_app/CLAUDE.md) for the rules.
[ADR 0004](../adr/0004-react-native-replaces-flutter.md) is the stack decision
behind all of it, including the measurements that ruled out a component library.

- **Workspace resolution:** npm workspaces, root `package.json`. The member
  list is **explicit**; the reasoning is in the root `package.json`'s own
  comments, so read them before adding a member.
- **Not a member, deliberately:** `apps/insolvia_marketing`. It keeps its own
  lockfile and its own CI job that installs from it, because Node resolution
  walks *up* the tree and would otherwise let a missing dependency resolve
  from the root.
- **One design system, consumed by version on both surfaces.**
  `@insolvia-ai/design-system` is platform-split — per component, a shared
  props module plus a `.web` and a `.native` leaf, with the consumer's bundler
  picking the leaf. Marketing's Vite picks the `.web` leaves; the app's Metro
  picks `.native` on **every** platform, web included, via a scoped
  `resolveRequest` override in its `metro.config.js` — react-native-web renders
  them, and no Tailwind enters the app.
  [ADR 0006](../adr/0006-owned-cross-platform-design-system.md) is the design
  decision, with the measurements;
  [ADR 0010](../adr/0010-design-system-moves-to-its-own-repository.md) is why
  the package is no longer here. **Never give either consumer a path
  dependency on it** — the app had one, through a workspace symlink, and one
  package with two simultaneous truths is what ADR 0010 removed.
- **Tokens generate elsewhere, with one output left behind.** The token values
  render in the design-system repo. Cognito's managed-login branding
  (`infra/modules/auth/managed-login-settings.json`) is this repo's
  infrastructure, so it stayed: `tool/reconcile-cognito-branding.ts` reconciles
  it against the installed `@insolvia-ai/tokens`, gated by the `Cognito
  branding` check. See [`package-publishing.md`](package-publishing.md).

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
build on an EAS config file, the EAS command-line tool as a dependency, an Expo
access token, or the over-the-air update client, because this is a constraint
that erodes by accident: several installed agent skills in `.agents/skills/`
present EAS as the normal way to ship. (The guard greps tracked files for the
exact package and secret names, so this paragraph avoids writing them.) Root
[`CLAUDE.md`](../../CLAUDE.md) carries the applicability table for those.

**Desktop is not built.** No macOS or Windows targets, no per-OS CI jobs, no
artifact hosting — [ADR 0004](../adr/0004-react-native-replaces-flutter.md) records
the trade. **Mobile is the held-open target**, and `expo prebuild` holds it open
with nothing committed under `ios/`/`android/` and no CI job at all.

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
absent at runtime, with no error — a workflow that sets a bare `INSOLVIA_ENV`
would silently read as `local` in production.

The corollary is that **nothing secret may go in an `EXPO_PUBLIC_*` variable.**
Everything so prefixed is compiled into a public static asset. Per
[ADR 0001](../adr/0001-client-stays-dumb-trust-boundary.md) the client holds no
credentials anyway, so there is nothing that wants to be there.

## Web hosting topology

`expo export -p web` produces a **static** SPA (`web.output: "single"`), so
hosting is intentionally compute-free:

```
Route53 (A-alias)  →  CloudFront (wildcard ACM TLS, SPA rewrite, /* -> index.html on 403/404)  →  S3 (private, OAC)
```

- `staging-app.insolvia.ai` → staging distribution/bucket
- `app.insolvia.ai` → prod distribution/bucket

The **marketing site** (`apps/insolvia_marketing`) does not use this topology —
it is server-side rendered, so it gets its own module with an SSR Lambda behind
the same CloudFront front (`infra/modules/marketing_site`, see
[`terraform.md`](terraform.md)). It runs in both
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
real (prod ships when the release run's gate is approved).

### One pipeline: staging, an approval, then production

`release.yml` is the deploy pipeline, and a push to `main` runs all of it:

| Stage | Jobs | Gate |
|---|---|---|
| Staging | `shared-infra-deploy.yml` (if shared paths changed) → `infra-staging.yml` (`mode: apply`) → changed services, ordered by `needs` | none — staging *is* `main` |
| Evidence | `record` (sha image tags + commit status), `supersede` | none |
| Production | `promote` → `infra-prod.yml` (`mode: apply`) → every service, same order | `promote` carries the `insolvia-production` environment |

Shared infra rides in the pipeline too, as the staging stage's very first leg:
both env roots resolve shared's resources (the wildcard cert, the container
repositories) with hard-failing `data` lookups, so `shared` must apply before
either env can even plan — which is also why it sits *before* the gate,
ungated, rather than behind the production approval. It is account-wide, so
one apply per commit serves both stages. It used to deploy from its own push
trigger, racing the staging jobs on any merge that touched both
([`terraform.md`](terraform.md) § deploy order has the race); `needs` ordering
replaced that.

Staging green parks the run at `promote`, which waits for the
`insolvia-production` environment's **required reviewer** (a repo-settings
gate nothing in this repo can change for itself). Approving it — once, in the
GitHub UI — releases the production stage for the same commit. That `needs`
edge is also the ordering proof: production exists downstream of staging in
the same run, so "staging was green for this exact commit" is guaranteed by
GitHub rather than re-derived from run history. `promote` is the *only* job
carrying the environment; the called prod workflows are told `gated: true` so
they don't each demand their own click.

Not approving is normal. Each new green staging run cancels older runs still
waiting at the gate (the `supersede` job), so exactly one promotion is ever
pending and it is always the newest validated commit; an unapproved release
ends `cancelled`, which is its expected fate, not a failure. After approval
the gate re-checks that nothing newer is waiting, closing the approve-vs-merge
race from the other side.

The service workflows **never apply Terraform** — they `init` and read outputs.
Infra is the first job of *both* stages for the same reason: the service legs
resolve the ECR repository, function name, alias and domain they act on from
Terraform outputs, and a leg that runs ahead of its infra deploys against
stale ones, silently. Staging's apply is ungated because reconciling `main`
on every merge is the definition being enforced; prod's apply sits behind the
release's approval, for the exact commit staging just validated — the approval
is the deliberate act that a separate infra dispatch used to be.

The pipeline declares no concurrency group of its own; each called workflow's
deploy job holds its environment's group (`insolvia-terraform-staging` /
`insolvia-terraform-prod`), and `needs` orders the legs. A group on both
caller and callee would deadlock the callee behind its own parent.

Both environments can also be planned before they are applied: `infra-staging.yml`
and `infra-prod.yml` each take `mode: plan`, which writes the plan to the job
summary. `shared-infra-plan.yml` validates every env offline on a PR, which
catches syntax and type errors but can never show what a change would *do*.

### Production promotes; it does not rebuild

The production stage ships **the whole product at the approved commit**, not
the push's diff — staging legs are path-filtered, but you might merge five PRs
and approve only the fifth run, and services touched by the first four must
not be left behind. Three mechanisms make that promotion sound:

- **It ships the artifact staging validated.** The container repositories are
  shared across environments (`infra/envs/shared`), so the image staging tested
  is already in the repository prod pulls from. A prod leg resolves the
  commit's `sha-<commit>` tag to an immutable digest and deploys *that* — there
  is no `docker build` in any prod workflow. The `record` job completes the
  tag set: a service the push didn't touch gets its currently-serving staging
  digest tagged with the new commit, so every staging-green commit resolves
  for every service. The app is the one exception and rebuilds, because it
  inlines its environment at build time (see *Environment model* above); it
  pins an exact Expo SDK version so "same source" also means "same bundler".
- **Hand-dispatched prod deploys refuse commits staging never blessed.** The
  `*-prod.yml` workflows stay individually dispatchable as the single-service
  emergency path, each behind its own `insolvia-production` approval.
  `.github/actions/verified-commit` blocks them unless the commit carries the
  `insolvia/staging-release` commit status the `record` job stamps the moment
  a staging stage finishes green. (A status, not a run conclusion — a release
  run's conclusion now includes the production stage, so "cancelled" no longer
  says anything about staging.) `force: true` bypasses it for a hotfix,
  loudly, in the job summary.
- **Traffic moves last.** The API and marketing SSR Lambdas sit behind a `live`
  alias. A deploy publishes a new version, smoke-tests it by its own version
  ARN while nothing routes to it, and shifts the alias only on success — so a
  failed smoke test leaves the previous version serving instead of leaving a
  broken build live. Rollback is `aws lambda update-alias --function-version
  <previous>`: seconds, no rebuild, no image pull. A full re-promotion of an
  older commit is a re-run of its green release run, approved again.

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
number is the problem — which is why the script resolves
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
| `Cognito branding` | `branding-pr.yml` |
| `Marketing site` | `marketing-pr.yml` |
| `API service` | `api-pr.yml` |
| `Mailer service` | `mailer-pr.yml` |
| `API client` | `api-client-pr.yml` |
| `Terraform validate (shared)` | `shared-infra-plan.yml` |
| `Terraform validate (staging)` | `shared-infra-plan.yml` |
| `Terraform validate (prod)` | `shared-infra-plan.yml` |

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
