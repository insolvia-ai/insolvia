# insolvia_design_system — agent rules

The owned, platform-split design system, published as
`@insolvia-ai/design-system` (0.2.x). Human docs: [`README.md`](README.md).
Publishing flow: [`docs/PACKAGE_PUBLISHING.md`](../../docs/PACKAGE_PUBLISHING.md).

- **Three files per component**: `<name>.props.ts` (shared), `<name>.web.tsx`
  (React DOM + Tailwind), `<name>.native.tsx` (RN primitives over
  `@insolvia-ai/tokens`). The per-component `index.ts` re-exports the
  extensionless `"./<name>"` and the consumer's bundler picks the leaf — Vite
  takes `.web`, Metro takes `.native` (react-native-web renders the `.native`
  leaf on app-web too; the app has no Tailwind, ADR 0004).
- **Props modules never import a renderer.** No `react-native`,
  `react-native-web`, `react-dom`, or `@base-ui/*` in `*.props.ts` or
  `src/lib/` — this is the load-bearing rule that keeps react-native-web out
  of marketing's bundle, and `eslint.config.js` enforces it. Don't weaken that
  override.
- **Litmus test for new code**: pure data (variant maps, class strings) may
  collapse into one shared file later; events, state, or a11y wiring means a
  leaf pair — the platforms' models do not unify.
- **No build step, ever.** The package publishes `src/` as-is; a tsup/tsc
  emit would collapse the leaf pairs and break resolution in the consumer's
  bundler. `package.json`'s comment block owns the full reasoning.
- **Any change here is its OWN PR with a `version` bump** (CI-enforced, same
  rule as the outgoing package): marketing consumes the PUBLISHED version, the
  app consumes the workspace symlink. See the `insolvia-design-system-pr`
  skill.
- **`src/styles/theme.css` is generated** from `packages/insolvia_tokens` —
  never hand-edit; edit `tokens.json` and `npm run tokens`. Its bytes are
  byte-frozen against the old package's published copy until the cutover
  completes, which is also why it sits in `.prettierignore`.
- **Both typecheck halves must pass**: `typecheck` (web program,
  `moduleSuffixes: [".web", ""]`) and `typecheck:native` (RN program,
  `[".native", ""]`, real `react-native` types). If tsc can't see an
  extensionless leaf import, fix the suffix lists — never add file
  extensions to the index re-exports.
- **Every component keeps ≥1 behavioural test** (Vitest + Testing Library,
  against the `.web` leaf). Props modules with real logic (accordion state
  machine, field id composition) get direct unit tests. No snapshot tests.
- Keep `react-native` and `@insolvia-ai/tokens` **optional** peerDependencies:
  a plain web `npm install` of this package must stay RN-free.
