// Jest, at the major jest-expo requires (ADR 0029 rule 3). This app is
// outside the npm workspace (root package.json's note), so it states its own
// runner and transform here rather than spreading the root's jest.base.js.
//
// Deliberately NOT reading vite.config.ts. That config loads the React
// Router and Tailwind plugins, which want a route manifest and a CSS pipeline
// neither of which a unit test needs — pulling them in makes the suite depend
// on `react-router typegen` having run, so a clean checkout would fail here
// before it failed anywhere useful.
//
// `testEnvironment: "node"` because everything covered so far is server-side:
// the `.server.ts` libraries and route `loader`/`action` functions, which run
// in the SSR Lambda and never touch a DOM. Component tests would need jsdom —
// add it when the first one arrives, not before.
//
// ESM and TSX are TRANSPILED to CommonJS by Babel, as jest-expo does; never
// `--experimental-vm-modules`.
import { createRequire } from "node:module";

const require = createRequire(import.meta.url);

export default {
  testEnvironment: "node",
  testMatch: ["<rootDir>/app/**/*.test.{ts,tsx}"],
  // Tests call jest.spyOn / jest.replaceProperty freely; every one is undone
  // after its test. process.env is put back by test/env.ts's restoreEnv().
  restoreMocks: true,
  // The ONE thing borrowed from vite.config.ts, restated rather than imported
  // for the reason above. @insolvia-ai/design-system publishes source whose
  // per-component index re-exports an extensionless `./button`, and the
  // CONSUMER's bundler picks the leaf — so without this list, importing any
  // module that reaches the design system fails to resolve, which is what a
  // route-component test does.
  //
  // `.web` first, exactly as vite.config.ts orders it: that is what keeps the
  // react-native leaf out. The two lists must agree — a test resolving a
  // different leaf than the build would be worse than no test.
  moduleFileExtensions: ["web.tsx", "web.ts", "web.jsx", "web.js", "mjs", "js", "mts", "ts", "jsx", "tsx", "json"],
  moduleNameMapper: {
    // tsconfig.json's `~/*` path.
    "^~/(.*)$": "<rootDir>/app/$1",
  },
  transform: {
    "\\.[cm]?[jt]sx?$": [
      "babel-jest",
      {
        babelrc: false,
        configFile: false,
        presets: [
          [require.resolve("@babel/preset-typescript"), { isTSX: true, allExtensions: true }],
          [require.resolve("@babel/preset-react"), { runtime: "automatic" }],
        ],
        plugins: [require.resolve("@babel/plugin-transform-modules-commonjs")],
      },
    ],
  },
  // Jest leaves node_modules untransformed by default; the design system
  // publishes untranspiled TypeScript, so it has to be let through.
  transformIgnorePatterns: ["/node_modules/(?!@insolvia-ai/)"],
};
