# insolvia_app

The Insolvia app — cross-platform (desktop + web). Currently a themed
hello-world that imports `insolvia_design_system`.

## Platform folders

The `web/`, `macos/` and `windows/` targets are committed, so a fresh clone
builds without any extra setup. To add another platform later (e.g. Linux), run
from this directory with Flutter installed:

```bash
flutter create --platforms=linux --org ai.insolvia --project-name insolvia_app .
```

`flutter create` adds missing platform folders without touching existing `lib/`,
`test/`, or `pubspec.yaml`. It does rewrite `.metadata`, dropping the entries
for platforms it did not just create — check that diff and restore them.

## Desktop builds

Desktop is deliberately **built but not promoted**, and both artifacts are
**unsigned** (see `docs/MVP_PLAN.md` decision D8). Distribution is a download
link handed to design partners, not a public download on the marketing site.

- **macOS** produces a `.dmg` (packaged with `hdiutil` in CI). Gatekeeper will
  refuse to launch it until the user allows it in **System Settings → Privacy &
  Security → Open Anyway**.
- **Windows** produces a `setup.exe` built with
  [Inno Setup](https://jrsoftware.org/isinfo.php) from
  [`windows/packaging/insolvia_app.iss`](windows/packaging/insolvia_app.iss) —
  `flutter build windows` only emits a directory of loose files, which is not
  something you can hand to anyone. It installs per-user (no admin needed) and
  SmartScreen will warn: **More info → Run anyway**.

Every artifact filename carries its environment and version, e.g.
`insolvia_app-staging-0.1.0-a1b2c3d.dmg`, because the environment is compiled
in and a staging build is otherwise indistinguishable from a production one.
CI also publishes a stable `insolvia_app-<env>-latest.*` alias.

Both targets are compiled on every PR (`.github/workflows/app-pr.yml`). That is
the whole mechanism keeping D8's option open — an unpromoted target that nobody
builds breaks silently.

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
