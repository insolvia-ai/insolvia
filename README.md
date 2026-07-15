# Insolvia

Modern, cross-platform bankruptcy case-preparation & e-filing for consumer
bankruptcy law firms — a competitor to Best Case by Stretto. One **Dart/Flutter**
codebase ships a native **desktop** app *and* a **web** app, so we can meet
desktop-loyal attorneys where they are.

> **Agents:** read [`CLAUDE.md`](CLAUDE.md) first — it is the source of truth for
> conventions in this monorepo.

## Layout

| Path | What |
|---|---|
| [`design-system/`](design-system/) | Shared Flutter UI package (`insolvia_design_system`): tokens, theme, components. |
| [`app/`](app/) | The Insolvia app (`insolvia_app`) — desktop + web. Currently a themed hello-world. |
| [`infra/`](infra/) | AWS infrastructure (Terraform): `shared`, `staging`, `prod`. |
| [`docs/`](docs/) | [Business plan](docs/business-plan.html) + engineering runbooks. |

## Prerequisites

- [FVM](https://fvm.app) (pins Flutter — see [`.fvmrc`](.fvmrc)): `dart pub global activate fvm && fvm install`
- [Melos](https://melos.invertase.dev): `dart pub global activate melos`
- For macOS desktop builds: full **Xcode** (Command Line Tools alone are not enough).

## Quick start

```bash
fvm install                 # install the pinned Flutter
melos bootstrap             # resolve all workspace packages
melos run ci                # format-check + analyze + test everything

# Run the app locally
cd app
fvm flutter run -d chrome   --dart-define=INSOLVIA_ENV=local   # web
fvm flutter run -d macos    --dart-define=INSOLVIA_ENV=local   # desktop
```

## Builds

```bash
cd app
fvm flutter build web   --dart-define=INSOLVIA_ENV=staging     # -> build/web
fvm flutter build macos --dart-define=INSOLVIA_ENV=staging     # -> build/macos/Build/Products/Release/insolvia_app.app
```

### Installing the macOS build (unsigned, for now)

The desktop app is **not yet code-signed/notarized**, so on first launch macOS
Gatekeeper will block it. To open it: **right-click the app → Open → Open**. This
is a one-time step per download. Signing/notarization is on the roadmap.

## Deployment

Deploys run through GitHub Actions (AWS via OIDC). They are **gated off** until
the `insolvia.ai` domain/DNS is live — see [`docs/AWS_SETUP.md`](docs/AWS_SETUP.md)
and [`docs/TERRAFORM_ARCHITECTURE.md`](docs/TERRAFORM_ARCHITECTURE.md). Until then,
CI builds and uploads web + macOS artifacts without publishing.

- **staging** → `staging.insolvia.ai` (auto, on merge to `main`)
- **production** → `app.insolvia.ai` (manual, gated)
