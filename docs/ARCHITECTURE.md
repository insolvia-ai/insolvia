# Architecture

## Monorepo shape

```
insolvia/
├── apps/
│   └── insolvia_app/                  Flutter app — desktop + web (feature-first)
│       └── lib/
│           ├── main.dart              runApp(InsolviaApp())
│           └── src/
│               ├── app.dart           app shell (MaterialApp.router + themes)
│               ├── routing/           go_router config
│               ├── config/            environment.dart (--dart-define)
│               └── features/
│                   └── home/presentation/   home_screen.dart + widgets/
├── packages/
│   └── insolvia_design_system/        shared UI — tokens, theme, components
├── infra/                             Terraform — shared / staging / prod
└── docs/                              business plan + runbooks
```

Layout follows the standard Flutter monorepo split (`apps/` + `packages/`) and a
**feature-first** app: code is grouped by feature under `lib/src/features/`, with
shared concerns (`routing/`, `config/`) alongside — not by technical layer.

- **Workspace resolution:** pub workspaces (root `pubspec.yaml` `workspace:`)
  cover `insolvia_tokens` + the app. The design system is deliberately
  **outside** the workspace and resolves standalone; the app consumes it as a
  **git dependency pinned to a version tag**
  (`insolvia_design_system-v<version>`), never by path — see
  `docs/PACKAGE_PUBLISHING.md`.
- **Task runner:** Melos (`melos.yaml`) — `melos bootstrap`, `melos run ci`.
- **Flutter:** Homebrew cask `flutter` (latest stable); CI uses the same via
  `subosito/flutter-action`. See *Flutter toolchain* below.

## Flutter toolchain

Flutter is the Homebrew cask `flutter` (latest stable), not FVM.
`scripts/dev-setup.sh` runs `brew install --cask flutter`, and the `dart` bundled
inside it powers Melos. CI installs the same latest stable via
`subosito/flutter-action` (`channel: stable`), so local and CI share one channel
— no pinned copy to drift.

**Multi-user machines.** The cask installs the SDK to a shared prefix
(`/opt/homebrew/share/flutter`) owned by whoever ran `brew`. Because the SDK is a
git repo and Flutter shells out to git, a *second* user account hits
`fatal: detected dubious ownership` and cannot write the SDK cache. To share one
Flutter across users, trust it system-wide and make it group-writable (both
accounts must share the group, e.g. `admin`):

```bash
sudo git config --system --add safe.directory /opt/homebrew/share/flutter
sudo chgrp -R admin /opt/homebrew/share/flutter
sudo chmod -R g+rwX /opt/homebrew/share/flutter
```

## Environment model (staging vs production)

The app is a single binary/bundle configured at **build time** — no separate
codepaths. Selection is via a compile-time define:

```bash
flutter build web --dart-define=INSOLVIA_ENV=staging      # or production, or local (default)
```

`apps/insolvia_app/lib/src/config/environment.dart` reads `INSOLVIA_ENV` and exposes a typed
`AppEnvironment` (label, `isProduction`, future API base URLs, etc.). The
hello-world screen renders the active environment so staging vs prod is visually
obvious.

We use `--dart-define` rather than full Flutter *flavors* deliberately: flavors
add per-platform Xcode/Gradle scheme plumbing that a hello-world doesn't need.
Flavors can be introduced later if we need distinct bundle IDs / icons per env.

## Web hosting topology

Flutter web compiles to a **static** SPA. Hosting is intentionally
compute-free:

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

## Desktop distribution

`flutter build macos` produces `insolvia_app.app`. It is currently **unsigned**;
CI zips it as an artifact. First launch requires right-click → Open (Gatekeeper).
Signing + notarization is deferred (needs an Apple Developer account).

## CI/CD

See `.github/workflows/`. Each area has a `*-pr.yml` (checks) and, where it
deploys, a `*-<env>.yml`. Deploys are live: shared infra is applied, the
`*.insolvia.ai` ACM cert is `ISSUED`, and merges to `main` deploy staging for
real (prod is dispatched manually).

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

`main` is protected by the `protect-main` ruleset. Verify, don't assume:
`gh api repos/insolvia-ai/insolvia/rulesets/18947945 --jq .rules`.

**Enforced today:** a PR is required (no direct pushes); linear history; no
force-push; no branch deletion; squash or rebase merges only; review threads must
be resolved, and pushes dismiss stale reviews.

**Not enforced today** (despite `.github/CODEOWNERS` existing):
`required_approving_review_count` is `0` and `require_code_owner_review` is
`false` — a PR can merge with **no approval** — and there are **no required
status checks**, so a PR with red CI can still merge. CODEOWNERS only *requests*
the code owner's review; it does not gate the merge.

### Required status checks — pending manual step

Turning red CI into a merge blocker is a **repo-settings change a human makes in
the GitHub UI/API** — nothing in this repo can grant itself branch protection.
The workflows above are already shaped (always-run-and-report) to allow it. In
`protect-main` → *Require status checks to pass*, add exactly these eleven job
`name:` values (matrix legs get a `(leg)` suffix):

| Check name | Workflow |
|---|---|
| `Flutter app` | `app-pr.yml` |
| `macOS build` | `app-pr.yml` |
| `Flutter design system` | `design-system-pr.yml` |
| `React design system` | `design-system-react-pr.yml` |
| `Marketing site` | `marketing-pr.yml` |
| `API service` | `api-pr.yml` |
| `Mailer service` | `mailer-pr.yml` |
| `Dart API client` | `api-client-pr.yml` |
| `Terraform validate (shared)` | `shared-infra-plan.yml` |
| `Terraform validate (staging)` | `shared-infra-plan.yml` |
| `Terraform validate (prod)` | `shared-infra-plan.yml` |

These strings are a **contract with the ruleset**: renaming a job `name:` (or a
matrix leg) silently orphans the required check — the ruleset waits forever for a
name nobody reports. Change one only alongside the ruleset. Also enable *Require
branches to be up to date before merging*, and set
`required_approving_review_count` to 1 with `require_code_owner_review: true` if
CODEOWNER review is wanted.
