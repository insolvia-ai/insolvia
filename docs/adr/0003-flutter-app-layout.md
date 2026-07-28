# ADR 0003 — The Flutter app follows Flutter's own architecture guide, not `lib/src/features/`

- **Status:** Accepted
- **Date:** 2026-07-28
- **Relates to:** `apps/insolvia_app/CLAUDE.md`; issue #51 (Cognito sign-in) is
  the first feature this shape has to absorb

## Decision

**`apps/insolvia_app` is laid out the way Flutter's
[architecture guide](https://docs.flutter.dev/app-architecture/case-study) and
its Compass sample lay out an app: UI grouped by feature, data grouped by type,
and no `lib/src/`.**

```
lib/
├── main.dart · app.dart
├── config/ · routing/
├── ui/core/ · ui/<feature>/{view_models,widgets}
├── domain/models/
└── data/{repositories,services}
```

This **reverses** the rule previously written in `apps/insolvia_app/CLAUDE.md`,
which prescribed strict feature-first slices —
`lib/src/features/<feature>/{data,domain,presentation}`.

## Context

The app had eight files and was already five directories deep at the worst
point (`lib/src/features/home/presentation/widgets/env_badge.dart`), while
`not_found_screen.dart` had drifted into `routing/` — a screen in a folder that
otherwise holds route configuration. Three things were wrong, and none of them
get better by waiting:

**`lib/src/` does nothing for an application.** It is a *package privacy*
convention: [Dart's package layout](https://dart.dev/tools/pub/package-layout)
puts implementation code under `lib/src/` so consumers cannot import it, and the
[`implementation_imports`](https://dart.dev/tools/linter-rules/implementation_imports)
lint enforces that across package boundaries. Nothing imports an app. The level
is correct and load-bearing in `packages/insolvia_design_system/`, which the app
consumes — it was copied here by resemblance, not for a reason.

**`presentation/` only means something next to `data/` and `domain/`.** Neither
existed. Every feature folder held exactly one subfolder, so the subfolder
carried no information and cost a directory level on every path.

**Our data will genuinely be cross-feature.** A case, a debtor, a document, a
MyCase sync record will each be read by intake *and* the means test *and* the
forms engine. Under strict feature slices those repositories have no home, so
they accumulate in a `shared/` folder — the documented failure mode of
feature-first, named by
[Code with Andrea](https://codewithandrea.com/articles/flutter-project-structure/),
who otherwise advocates for it. Flutter's hybrid split exists precisely because
screens belong to one feature and repositories do not.

## Consequences

- Future contributors and agents meet the layout Flutter itself publishes and
  maintains a reference sample for, rather than a repo-local invention.
- `test/` mirrors `lib/`, so a file's test has one predictable location. The
  catch-all `widget_test.dart` is gone.
- Empty scaffolding is not created ahead of need: `domain/` and `data/` appear
  with the first model and the first repository, which #51 will add.
- The move was mechanical — file relocations plus import rewrites, no behaviour
  change, all 16 tests green — because it was done at eight files. At eighty it
  would not have been.

## Alternatives considered

**Keep strict feature-first slices.** The stronger argument for them is that a
feature can later be extracted into its own package, since a slice has no
outbound dependencies on sibling features. Insolvia is one Flutter app with one
deployment; we are not going to extract `features/means_test` into a package.
Paying the structural cost for optionality we will not exercise is the wrong
trade.

**Flatten but keep `lib/src/`.** This fixes the depth without the churn of
moving every file. Rejected because it keeps a convention that actively misleads
— an agent reading `lib/src/` in the app and `lib/src/` in the design system
reasonably concludes they mean the same thing, and in the design system it means
something real.
