// Shared Jest settings for the npm workspace members that are plain
// TypeScript (packages/insolvia_api_client, contract). ADR 0029 rule 3: one
// runner, Jest, at the major jest-expo requires; the app keeps jest-expo's
// own preset, and marketing and the admin portal state theirs in their own
// manifests, because they are not workspace members.
//
// DELIBERATELY NOT NAMED `jest.config.js`. Jest looks for its config by
// walking UP from its cwd, so a root one would be found by every directory
// below it — marketing and the admin portal included. Each member owns a
// `jest.config.js` that spreads this, the same shape as eslint.base.js.
//
// ESM is TRANSPILED to CommonJS by Babel, as jest-expo does. Never run Jest
// with `--experimental-vm-modules`: it is experimental in Jest 29, needs a
// Node flag on every invocation, and changes module semantics under the
// mocks. Transpiling has one cost: `import.meta` does not exist in CommonJS,
// so code under test must not read it (contract/src/support/env.ts imports
// its JSON instead).
//
// Relative imports keep their literal `.ts` extension (tsconfig.base.json's
// `//allowImportingTsExtensions`); Jest resolves `./models.ts` as the file it
// names, so nothing has to be mapped.
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);

export default {
  testEnvironment: 'node',
  transform: {
    '\\.ts$': [
      'babel-jest',
      {
        babelrc: false,
        configFile: false,
        presets: [require.resolve('@babel/preset-typescript')],
        plugins: [require.resolve('@babel/plugin-transform-modules-commonjs')],
      },
    ],
  },
};
