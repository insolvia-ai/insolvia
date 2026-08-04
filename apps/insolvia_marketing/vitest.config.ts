import { defineConfig } from "vitest/config";

// Deliberately NOT importing vite.config.ts. That config loads the React
// Router and Tailwind plugins, which want a route manifest and a CSS pipeline
// neither of which a unit test needs — pulling them in makes the suite depend
// on `react-router typegen` having run, so a clean checkout would fail here
// before it failed anywhere useful.
//
// `environment: "node"` because everything covered so far is server-side: the
// `.server.ts` libraries and route `loader`/`action` functions, which run in
// the SSR Lambda and never touch a DOM. Component tests would need jsdom and a
// separate environment — add it when the first one arrives, not before.
export default defineConfig({
  // The ONE thing borrowed from vite.config.ts, copied rather than imported for
  // the reason above. @insolvia-ai/design-system publishes source whose
  // per-component index re-exports an extensionless `./button`, and the
  // CONSUMER's bundler picks the leaf — so without this list, importing any
  // module that reaches the design system fails to resolve, which is what a
  // route-component test does.
  //
  // `.web` first, exactly as vite.config.ts orders it: that is what keeps the
  // react-native leaf out. The two lists must agree — a test resolving a
  // different leaf than the build would be worse than no test.
  resolve: {
    extensions: [
      ".web.tsx",
      ".web.ts",
      ".web.jsx",
      ".web.js",
      ".mjs",
      ".js",
      ".mts",
      ".ts",
      ".jsx",
      ".tsx",
      ".json",
    ],
  },
  test: {
    environment: "node",
    include: ["app/**/*.test.{ts,tsx}"],
    // No `globals: true`. Tests import `describe`/`it`/`expect` explicitly, so
    // `tsc` and ESLint both see real bindings — this app typechecks and lints
    // its whole `app/` tree, and ambient globals would need type and lint
    // config carve-outs in two more files to stay clean.
    restoreMocks: true,
    unstubEnvs: true,
    unstubGlobals: true,
  },
});
