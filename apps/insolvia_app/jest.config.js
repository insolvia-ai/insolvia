// Jest configuration for the Insolvia app.
//
// This lives in a .js file rather than the `jest` key in package.json for one
// reason: `transformIgnorePatterns` now has to be COMPUTED from the preset's
// own value, and package.json cannot compute.
//
// ── Why it has to be computed ────────────────────────────────────────────────
//
// @insolvia-ai/design-system publishes untranspiled TypeScript source, by
// design — the consumer's bundler picks the `.web`/`.native` leaf, so a
// package-side build would collapse the pair. Metro transforms it happily.
// Jest does not: its default is to skip transforming anything under
// node_modules, so an import of the package fails with
// "SyntaxError: Unexpected token 'export'".
//
// This did not bite while the design system was a workspace MEMBER of this
// repo. Its source sat at packages/insolvia_design_system, outside
// node_modules, so the preset's transform applied and nothing had to be said.
// Extracting it into github.com/insolvia-ai/design-system moved the same
// source inside node_modules and turned a non-issue into a hard failure of
// every route test.
//
// ── Why it is computed rather than written out ───────────────────────────────
//
// The preset's list is what has to win. expo-router 57 pulls in
// `standard-navigation`, which ships untransformed ESM and appears ONLY in the
// preset's list; hand-copied versions of this array circulate widely, silently
// drop it, and every route test dies with "Cannot use import statement outside
// a module". So this reads the preset's array and injects one alternative into
// its negative lookahead, rather than restating it. If the preset's shape ever
// changes, the assertion below fails loudly instead of quietly un-transforming
// half the tree.
const preset = require('jest-expo/jest-preset');

// The preset's first entry is the negative-lookahead list of packages that
// SHOULD be transformed: "/node_modules/(?!(a|b|c))". Injecting right after
// the "(?!(" keeps every existing alternative intact.
const NEEDLE = '/node_modules/(?!(';
const transformIgnorePatterns = preset.transformIgnorePatterns.map((pattern) =>
  pattern.startsWith(NEEDLE) ? pattern.replace(NEEDLE, `${NEEDLE}@insolvia-ai|`) : pattern,
);

if (!transformIgnorePatterns.some((pattern) => pattern.includes('@insolvia-ai'))) {
  throw new Error(
    "jest-expo's transformIgnorePatterns no longer starts with " +
      `"${NEEDLE}", so @insolvia-ai was never added and every test importing ` +
      '@insolvia-ai/design-system will fail with "Unexpected token \'export\'". ' +
      "Re-derive the injection from the preset's current shape — do not paste " +
      "a literal list here, or the preset's own entries (standard-navigation in " +
      'particular) will be silently dropped.',
  );
}

module.exports = {
  preset: 'jest-expo',

  // Jest's default is 5000ms, which is a UNIT-test number, and this suite's
  // route-level tests are not unit tests: `renderRouter('src/app', …)` mounts
  // the real route tree, which is the shape ADR 0008 chose on purpose
  // (docs/adr/0008-testing-shape-follows-the-code-it-tests.md). That is paid by
  // the FIRST
  // `it()` in each route suite, which measurably costs ~10x its siblings:
  // documents/index.test.tsx runs 172ms for its first test and 12-32ms for the
  // seven after it.
  //
  // Locally that is nowhere near 5000ms. On a loaded CI runner it is: the same
  // suite takes 15-21s of wall clock there, and the multiplier lands the first
  // test around 7s. That is not a hang — the work still completes — so the
  // default turned runner speed into a red build. Observed on one commit, with
  // the tree byte-identical between runs: 14.8s → passed, 20.7s → failed on
  // `documents`, 20.3s → failed on `documents` AND `intake`, each time on that
  // block's first test. A sibling branch passed at 15.8s in the same minute as
  // one of the failures, which is how thin the margin was.
  //
  // 20000ms is a ceiling for a genuinely stuck test, not a target. Nothing here
  // should approach it; if something does, that is a real defect and this
  // number should not be raised to accommodate it.
  testTimeout: 20000,

  // Restates the `@/*` alias from tsconfig.json. Metro reads the tsconfig
  // paths itself; Jest does not, so without this every `@/` import fails to
  // resolve under test while building fine.
  moduleNameMapper: {
    '^@/(.*)$': '<rootDir>/src/$1',
  },

  transformIgnorePatterns,
};
