import { defineConfig } from "vitest/config";

// The site had no test runner while its UI came from a separate, independently
// tested design-system package. Now that the components live here — including a
// hand-rolled accordion and a form field whose label/aria wiring is done by
// hand — their behaviour is tested here, next to them.
//
// No `@vitejs/plugin-react`: it peers on Vite <=7 and this app is on Vite 8, and
// the plugin only adds Fast Refresh (a dev-server concern). Vitest transforms
// JSX through esbuild; `jsx: "automatic"` gives the React 19 runtime with no
// per-file `import React`.
export default defineConfig({
  resolve: { alias: { "~": new URL("./app", import.meta.url).pathname } },
  esbuild: { jsx: "automatic", jsxImportSource: "react" },
  test: {
    environment: "jsdom",
    globals: true,
    setupFiles: ["./vitest.setup.ts"],
    include: ["app/**/*.test.{ts,tsx}"],
  },
});
