# insolvia_app

The Insolvia app — cross-platform (desktop + web). Currently a themed
hello-world that imports `insolvia_design_system`.

## Platform folders

The `web/` and `macos/` targets are committed, so a fresh clone builds without
any extra setup. To add another platform later (e.g. Windows or Linux), run from
this directory with Flutter installed:

```bash
fvm flutter create --platforms=windows,linux --org ai.insolvia --project-name insolvia_app .
```

`flutter create` adds missing platform folders without touching existing `lib/`,
`test/`, or `pubspec.yaml`.

## Run

```bash
fvm flutter run -d chrome --dart-define=INSOLVIA_ENV=local     # web
fvm flutter run -d macos  --dart-define=INSOLVIA_ENV=local     # desktop
```

## Build

```bash
fvm flutter build web   --dart-define=INSOLVIA_ENV=staging
fvm flutter build macos --dart-define=INSOLVIA_ENV=staging
```

See the repo [`README.md`](../../README.md) for the unsigned-macOS install step.
