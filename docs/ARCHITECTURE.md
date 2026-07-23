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
- **Flutter version:** pinned via FVM (`.fvmrc`).

## Environment model (staging vs production)

The app is a single binary/bundle configured at **build time** — no separate
codepaths. Selection is via a compile-time define:

```bash
fvm flutter build web --dart-define=INSOLVIA_ENV=staging      # or production, or local (default)
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

No Lambda/API Gateway/DynamoDB yet — those arrive with the intake/forms phases
(see `docs/business-plan.html`).

## Desktop distribution

`flutter build macos` produces `insolvia_app.app`. It is currently **unsigned**;
CI zips it as an artifact. First launch requires right-click → Open (Gatekeeper).
Signing + notarization is deferred (needs an Apple Developer account).

## CI/CD

See `.github/workflows/`. Each area has a `*-pr.yml` (checks) and, where it
deploys, a `*-<env>.yml`. Deploy steps are gated behind the `DEPLOY_ENABLED`
repo variable (currently `false`); builds/artifacts run regardless. DNS is live;
the gate now waits on shared infra being applied (#15) and the ACM cert reaching
`ISSUED` (#16).

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

Required-check names, and the fact that enabling them is a manual repo-settings
step, are documented in the root `CLAUDE.md` under *PR gates, required status
checks, and why no `paths:` filter*.
