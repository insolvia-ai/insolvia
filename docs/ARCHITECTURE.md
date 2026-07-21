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

- **Workspace resolution:** pub workspaces (root `pubspec.yaml` `workspace:`) so
  one resolve covers every package; the app depends on the design system by
  **path** (`../../packages/insolvia_design_system`).
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
repo variable until DNS is live; builds/artifacts run regardless.
