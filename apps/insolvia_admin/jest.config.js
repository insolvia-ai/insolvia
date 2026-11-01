// Jest, at the major jest-expo requires (ADR 0029 rule 3). This app is
// outside the npm workspace, so it states its own runner and transform here
// rather than spreading the root's jest.base.js — whose packages it could
// only reach by Node walking up out of this directory (package.json's note).
//
// ESM and TSX are TRANSPILED to CommonJS by Babel, as jest-expo does; never
// `--experimental-vm-modules`. `import.meta` does not survive that, which is
// why the one read of it lives alone in src/config/build-environment.ts and
// is mapped away below.
import { createRequire } from "node:module";

const require = createRequire(import.meta.url);

export default {
  testEnvironment: "<rootDir>/jest.environment.js",
  testMatch: ["<rootDir>/src/**/*.test.{ts,tsx}"],
  setupFilesAfterEnv: ["<rootDir>/src/test-setup.ts"],
  // vite.config.ts's resolve.extensions, in Jest's spelling: the design
  // system publishes SOURCE with extensionless platform-split leaf imports,
  // and a test must resolve the same `.web` leaf the bundle does.
  moduleFileExtensions: ["web.tsx", "web.ts", "web.js", "tsx", "ts", "js", "mjs", "json"],
  moduleNameMapper: {
    "^(.*/)?build-environment$": "<rootDir>/src/config/build-environment.jest.ts",
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
