# insolvia_app — agent rules

Flutter app, desktop + web. Human docs: [`README.md`](README.md). Run it with
`scripts/dev-up.sh`.

- **Feature-first layout** under `lib/src/`: `features/<feature>/{data,domain,presentation}`,
  plus shared `routing/` (go_router) and `config/`. `main.dart` stays thin
  (`runApp(InsolviaApp())`); the app shell is `src/app.dart`.
- **No hard-coded colors, spacing, or fonts.** Pull everything from the design
  system's theme and its `ThemeExtension`s
  (`Theme.of(context).extension<InsolviaColors>()`, `…<InsolviaSpacing>()`).
- **Environment** comes from `--dart-define=INSOLVIA_ENV` (`local` default),
  read in `lib/src/config/environment.dart`.
- **The design system is a git-tag dependency, never a path.** To hack on it
  live, add an *uncommitted* `pubspec_overrides.yaml` pointing at
  `../../packages/insolvia_design_system` and delete it before committing (see
  [`docs/PACKAGE_PUBLISHING.md`](../../docs/PACKAGE_PUBLISHING.md)).
- Committed platform targets: `web/`, `macos/`.
