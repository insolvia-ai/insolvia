# insolvia_app

The Insolvia app — cross-platform (desktop + web). Currently a themed
hello-world that imports `insolvia_design_system`.

## Platform folders

The `web/` and `macos/` targets are committed, so a fresh clone builds without
any extra setup. To add another platform later (e.g. Windows or Linux), run from
this directory with Flutter installed:

```bash
flutter create --platforms=windows,linux --org ai.insolvia --project-name insolvia_app .
```

`flutter create` adds missing platform folders without touching existing `lib/`,
`test/`, or `pubspec.yaml`.

## Run

```bash
scripts/dev-setup.sh            # once: resolve the workspace
scripts/dev-up.sh               # flutter run (INSOLVIA_ENV=local; prompts for a device)
scripts/dev-up.sh -d chrome     # any flutter run flag passes through
```

To build a release artifact, `flutter build web|macos
--dart-define=INSOLVIA_ENV=staging`; see the repo [`README.md`](../../README.md)
for the unsigned-macOS install step. Working on the code? The agent rules for
this app are in [`CLAUDE.md`](CLAUDE.md).
