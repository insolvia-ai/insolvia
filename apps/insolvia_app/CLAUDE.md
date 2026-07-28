# insolvia_app — agent rules

Flutter app, desktop + web. Human docs: [`README.md`](README.md). Run it with
`scripts/dev-up.sh`.

## Where code goes

The layout follows Flutter's own
[architecture guide](https://docs.flutter.dev/app-architecture/case-study) and
its Compass sample: **UI grouped by feature, data grouped by type.** The
reasoning is in [ADR 0003](../../docs/adr/0003-flutter-app-layout.md) — read it
before proposing a different shape.

```
lib/
├── main.dart              entry point; nothing but runApp(InsolviaApp())
├── app.dart               root widget — MaterialApp.router, themes, router
├── config/                build-time configuration (environment.dart)
├── routing/               go_router config: route names, paths, error builder
├── ui/
│   ├── core/              chrome shared across features (not_found_screen.dart)
│   ├── <feature>/         one folder per feature — auth/, home/, …
│   │   ├── <name>_screen.dart
│   │   ├── view_models/   added when the feature holds state
│   │   └── widgets/       widgets used only by this feature
├── domain/models/         app-wide data types
└── data/
    ├── repositories/      the API a feature calls; groups by type, not feature
    └── services/          external clients (insolvia_api_client, Cognito, …)
```

Rules that follow from it:

- **No `lib/src/`.** `lib/src/` is a *package privacy* convention — it stops
  other packages importing internals, enforced by the `implementation_imports`
  lint. Nothing imports an app, so here it is dead depth. It is correct and
  load-bearing in `packages/insolvia_design_system/`; do not copy it back.
- **A screen lives under `ui/<feature>/`, never in `routing/`.** `routing/` holds
  route configuration only.
- **Create folders when the second file arrives, not before.** `domain/` and
  `data/` do not exist yet and should not be scaffolded empty. A feature folder
  gets `view_models/` when it holds state and `widgets/` when a widget is used
  twice — a folder holding one file carries no information.
- **`data/repositories/` groups by type, not by feature.** A case, a debtor, a
  document will each be read by several features; filing repositories under one
  feature would force a `shared/` folder that becomes a dumping ground.
- **A widget used by two features moves to `ui/core/`.** If it is also *brand*
  rather than app logic, it belongs in the design system instead — that is its
  own PR plus a version bump (see the `insolvia-design-system-pr` skill).
- **`test/` mirrors `lib/`.** `lib/ui/home/home_screen.dart` is tested by
  `test/ui/home/home_screen_test.dart`. No catch-all `widget_test.dart`.

## Everything else

- **No hard-coded colors, spacing, or fonts.** Pull everything from the design
  system's theme and its `ThemeExtension`s
  (`Theme.of(context).extension<InsolviaColors>()`, `…<InsolviaSpacing>()`).
- **Environment** comes from `--dart-define=INSOLVIA_ENV` (`local` default),
  read in [`lib/config/environment.dart`](lib/config/environment.dart).
- **The design system is a git-tag dependency, never a path.** To hack on it
  live, add an *uncommitted* `pubspec_overrides.yaml` pointing at
  `../../packages/insolvia_design_system` and delete it before committing (see
  [`docs/PACKAGE_PUBLISHING.md`](../../docs/PACKAGE_PUBLISHING.md)).
- Committed platform targets: `web/`, `macos/`.
