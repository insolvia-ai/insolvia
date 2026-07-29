// Flat config for the npm workspace members only.
//
// apps/insolvia_marketing and packages/insolvia_design_system_react each own
// their own ESLint config, lockfile and required check, and are NOT workspace
// members — so they are not linted from here. The `lint` script in the root
// package.json names its targets explicitly rather than passing `.`, which is
// the real guard; the ignores below are belt-and-braces for anyone running
// `npx eslint .` by hand.
import js from '@eslint/js';
import globals from 'globals';
import tseslint from 'typescript-eslint';

export default tseslint.config(
  {
    ignores: [
      'node_modules/**',
      '**/node_modules/**',
      '**/dist/**',
      'apps/insolvia_marketing/**',
      'packages/insolvia_design_system_react/**',
      // Dart build output, until PR 3 removes the Dart half entirely.
      '**/.dart_tool/**',
      'build/**',
      // Generated output is deliberately NOT ignored — see .prettierignore for
      // the reasoning. `packages/insolvia_tokens/src/tokens.ts` is linted like
      // any other source, and the generator emits bytes that satisfy it.
    ],
  },
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
