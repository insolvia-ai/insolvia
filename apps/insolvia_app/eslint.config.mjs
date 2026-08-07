// Rules live in the repo root's `eslint.base.js`. That file is not named
// `eslint.config.js` on purpose — see its header for what breaks when a
// discoverable flat config sits at the repo root.
//
// `.mjs`, not `.js`, unlike the other workspace members: this package.json has
// no `"type": "module"` because `metro.config.js` must stay CommonJS (Metro
// `require()`s it). Node resolves module type from the NEAREST package.json, so
// an ESM `eslint.config.js` here would be reparsed with a warning on every run.
//
// Three additions on top of the shared base, all because this is the only
// workspace member that renders UI and the only one with a CommonJS config file.
import base from '../../eslint.base.js';

export default [
  ...base,
  { ignores: ['.expo/**', 'dist/**', 'expo-env.d.ts'] },
  {
    // The two CommonJS config files. Both are loaded by a tool that `require()`s
    // them, and this package.json has no `"type": "module"`, so neither can be
    // ESM — Metro loads metro.config.js, Jest loads jest.config.js.
    //
    // jest.config.js has to `require('jest-expo/jest-preset')` specifically:
    // it COMPUTES `transformIgnorePatterns` from the preset's own array rather
    // than restating it, because the preset carries entries (standard-navigation)
    // that hand-copied lists silently drop. That computation is why the config
    // is a .js file at all instead of a `jest` key in package.json.
    files: ['metro.config.js', 'jest.config.js'],
    rules: { '@typescript-eslint/no-require-imports': 'off' },
  },
  {
    files: ['**/*.test.ts', '**/*.test.tsx'],
    languageOptions: {
      globals: {
        afterEach: 'readonly',
        beforeEach: 'readonly',
        describe: 'readonly',
        expect: 'readonly',
        it: 'readonly',
        jest: 'readonly',
      },
    },
  },
];
