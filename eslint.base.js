// Shared ESLint rules for the npm workspace members.
//
// DELIBERATELY NOT NAMED `eslint.config.js`, and it must never be renamed to
// that. ESLint discovers a flat config by searching from its cwd and walking
// UP the directory tree, so a flat config at the repo root is found by ESLint
// runs inside *every* subdirectory — including ones outside the workspace with
// their own ESLint setup (apps/insolvia_marketing today).
//
// This bit once, for real: a root `eslint.config.js` flipped a since-retired
// package's own eslintrc-based ESLint 8 run into flat-config mode, and its
// required check went red for a change that never touched it.
//
// So: each workspace member owns a one-line `eslint.config.js` re-exporting
// this file, and nothing discoverable sits at the repo root. Root `npm run
// lint` fans out via `--workspaces`, the same way `typecheck` and `test` do.
//
// Generated output is deliberately NOT ignored — see .prettierignore for that
// reasoning. `packages/insolvia_tokens/src/tokens.ts` is linted like any other
// source, and the generator emits bytes that satisfy it.
import js from '@eslint/js';
import globals from 'globals';
import tseslint from 'typescript-eslint';

export default tseslint.config(
  // Resolved relative to whichever member re-exports this, so every pattern is
  // location-independent (`**/`-prefixed) rather than repo-root-relative.
  { ignores: ['**/node_modules/**', '**/dist/**'] },
  js.configs.recommended,
  ...tseslint.configs.recommended,
  {
    files: ['**/*.{ts,tsx,js,mjs}'],
    languageOptions: {
      globals: { ...globals.node },
    },
    rules: {
      '@typescript-eslint/no-unused-vars': [
        'error',
        { argsIgnorePattern: '^_', varsIgnorePattern: '^_' },
      ],
    },
  },
);
