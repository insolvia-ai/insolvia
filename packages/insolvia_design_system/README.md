# insolvia_design_system

Insolvia's owned, cross-platform design system, published as
`@insolvia-ai/design-system` (0.2.x). One set of component names serves both
front-end stacks: the marketing site (React DOM + Tailwind) and the app
(React Native / Expo). Agent rules: [`CLAUDE.md`](CLAUDE.md).

It succeeds `packages/insolvia_design_system_react` (0.1.x, web-only, Base UI),
which coexists frozen until marketing moves to 0.2.x and it is deleted.

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

| Consumer | Bundler | Leaf | Why |
|---|---|---|---|
| Marketing site | Vite | `.web.tsx` | Tailwind classes over `theme.css` |
| App (native, later) | Metro | `.native.tsx` | RN primitives, tokens values |
| App (web, today) | Metro | `.native.tsx` | react-native-web renders the RN tree — the app has no Tailwind (ADR 0004), so the `.web` leaf would be unstyled there |

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

| Consumer | Resolves the package via |
|---|---|
| `apps/insolvia_app` | workspace symlink (root npm workspace member) |
| `apps/insolvia_marketing` | the **published version** from GitHub Packages |

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
npm run typecheck       --workspace @insolvia-ai/design-system   # web program
npm run typecheck:native --workspace @insolvia-ai/design-system  # RN program
npm run test            --workspace @insolvia-ai/design-system
```

Typechecking is split because the imports are: each tsconfig sets
`moduleSuffixes` (`[".web", ""]` / `[".native", ""]`) so tsc resolves the same
extensionless imports to the same leaves the bundlers do. Tests are Vitest +
Testing Library against the `.web` leaves (plus direct unit tests for props
modules that carry real logic); the native leaves are typechecked against real
React Native types and rendered by the app's own test harness.
