# insolvia_design_system

Insolvia's owned, cross-platform design system, published as
`@insolvia-ai/design-system` (0.6.x). One set of component names serves both
front-end stacks: the marketing site (React DOM + Tailwind) and the app
(React Native / Expo). Agent rules: [`CLAUDE.md`](CLAUDE.md).

It succeeded `packages/insolvia_design_system_react` (0.1.x, web-only, Base
UI), deleted when marketing cut over to 0.2.x.

## Components

The original surfaces: `Accordion` · `Button` · `Card` · `Field` · `Footer` ·
`NavBar`. The 0.3.0 wave added owned equivalents of the portable Base UI
primitives (equivalent behavior, zero Base UI dependency): `AlertDialog` ·
`Avatar` · `Checkbox` · `CheckboxGroup` · `Collapsible` · `Dialog` · `Meter` ·
`Progress` · `RadioGroup` · `Separator` · `Switch` · `Tabs` · `Toggle` ·
`ToggleGroup`. 0.4.0 adds `Select`, the first anchored-popup component — see
the note below on why it stopped being deferred. 0.5.0 adds `DateInput`, a
masked `YYYY-MM-DD` text field with no calendar (the reasoning is at the top of
`date-input.props.ts`); 0.6.0 gives its `onValueChange` a second argument, because
`''` alone cannot distinguish "cleared" from "still typing" and an autosaving
caller wiped saved dates on that ambiguity. Compound components export their parts under one name
(`Dialog.Root`, `Dialog.Trigger`, …); input-taking components support both
uncontrolled (`default*`) and controlled (`*` + change callback) modes via
`src/lib/controllable.ts`.

Deliberately not ported, and why: hover-only surfaces (Tooltip, Preview Card —
inaccessible on touch, per Base UI's own docs) and desktop-menu surfaces
(Menubar, Navigation Menu); anchored-popup components (Popover, Menu, Combobox,
Autocomplete, Number Field, Scroll Area, Context Menu) need a positioning
primitive and mobile idioms (sheets, native pickers) that deserve their own
design pass; Toast needs an app-level provider architecture; Form / Fieldset /
Input overlap with `Field`, which already owns form-control wiring.

**`Select` came off that list in 0.4.0**, because the intake questionnaire
needs it and a form cannot route around a missing select. The two reasons it
was deferred were answered rather than waived: positioning is an absolute
anchor under a full-width trigger, which needs no positioning primitive because
the popup is exactly as wide as the control; and the mobile sheet idiom is not
needed while the only target is web — the native leaf's popup renders inline,
with a comment saying what a real device would want instead. Anything that has
to float free of its trigger (Popover, Menu) still needs the deferred design
pass; a select does not.

## The pattern: one props module, two leaves

Every component is three files:

```
src/button/
  button.props.ts    shared: types, variant maps, state hooks, a11y string rules
  button.web.tsx     React DOM + Tailwind — what marketing renders
  button.native.tsx  React Native primitives over @insolvia-ai/tokens
  index.ts           re-exports the extensionless "./button"
```

The per-component `index.ts` deliberately imports `"./button"` with no
extension — **the consumer's bundler picks the leaf**:

| Consumer            | Bundler | Leaf          | Why                                                                                                                   |
| ------------------- | ------- | ------------- | --------------------------------------------------------------------------------------------------------------------- |
| Marketing site      | Vite    | `.web.tsx`    | Tailwind classes over `theme.css`                                                                                     |
| App (native, later) | Metro   | `.native.tsx` | RN primitives, tokens values                                                                                          |
| App (web, today)    | Metro   | `.native.tsx` | react-native-web renders the RN tree — the app has no Tailwind (ADR 0004), so the `.web` leaf would be unstyled there |

The native leaves resolve their **colors at render time** through
`src/lib/native-theme.native.ts` (`useNativeColors()` — anything but `'dark'`
resolves to light, mirroring the app's `themeFor`). A color read statically —
`colors.light` at module load, or a color inside `StyleSheet.create` — can
never follow the OS scheme: 0.2.1 shipped exactly that, and every
design-system surface stayed light inside a dark app. Only scheme-independent
layout belongs in `StyleSheet.create`.

The props module is the platform-SHARED third and must never import a
renderer — no `react-native`, no `react-dom`, no `@base-ui/*`. That rule is
what keeps react-native-web out of marketing's bundle, so it is machine-
enforced: `eslint.config.js` bans those imports in `**/*.props.ts` (and
`src/lib/`), and the spike measured the result — a web build from these
leaves is byte-equivalent to the old package's, zero react-native-web.

**Litmus test for a new component:** pure data (variant → class/value maps)
is a candidate to collapse into a single shared file later; anything with
events, state, or accessibility wiring is a leaf pair from day one — the two
platforms' event and a11y models do not unify.

## Two consumers, two channels

| Consumer                  | Resolves the package via                       |
| ------------------------- | ---------------------------------------------- |
| `apps/insolvia_app`       | workspace symlink (root npm workspace member)  |
| `apps/insolvia_marketing` | the **published version** from GitHub Packages |

The app's channel is live: it consumes this package as **source**, through the
workspace symlink plus a Metro `resolveRequest` (`apps/insolvia_app/metro.config.js`).

Marketing consuming by version is why **any change here is its own PR with a
`version` bump** — see the `insolvia-design-system-pr` skill. The app sees
your change on save; marketing sees nothing until a publish.

## No build step — the package publishes source

`files: ["src"]`, exports point at `.ts`/`.tsx`, and there is no tsup/tsc
emit. Leaf resolution happens in the consumer's bundler, so the
`.web.tsx`/`.native.tsx` pairs must survive into the published artifact
verbatim; a package-side build would collapse each pair into one compiled
entry and break the pattern.

## theme.css is generated

`src/styles/theme.css` (public specifier
`@insolvia-ai/design-system/theme.css`) is rendered from
`packages/insolvia_tokens/tokens.json` — never hand-edit it. Change a value
there, then `npm run tokens` from the repo root; `npm run tokens:check` gates
drift in CI.

## Checks

```bash
npm run lint            --workspace @insolvia-ai/design-system
npm run typecheck       --workspace @insolvia-ai/design-system   # all three programs
npm run typecheck:native --workspace @insolvia-ai/design-system  # RN program only
npm run test            --workspace @insolvia-ai/design-system   # web + native projects
```

Typechecking is split because the imports are: each tsconfig sets
`moduleSuffixes` (`[".web", ""]` / `[".native", ""]`) so tsc resolves the same
extensionless imports to the same leaves the bundlers do. A third program,
`tsconfig.native.test.json`, checks the native-leaf tests (native suffixes
plus the DOM lib, since those tests assert on react-native-web's DOM).

Tests run as two vitest projects (`vitest.config.ts`): `web` is Vitest +
Testing Library against the `.web` leaves, resolved web-first as Vite does;
`native` resolves the `.native` leaves native-first as Metro does and aliases
`react-native` to `react-native-web` — the exact pair the app ships on web —
rendering them into the same jsdom. `vitest.native.setup.ts` supplies the
`matchMedia` mock that drives `prefers-color-scheme` in those tests. Props
modules with real logic keep direct unit tests. Native tests live in
`*.native.test.tsx` beside the leaf; Button and Field carry the a11y wiring
and must keep native coverage.
