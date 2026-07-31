import { reactRouter } from "@react-router/dev/vite";
import tailwindcss from "@tailwindcss/vite";
import { defineConfig } from "vite";

export default defineConfig(({ command }) => ({
  plugins: [tailwindcss(), reactRouter()],
  resolve: {
    // Vite 8 resolves tsconfig `paths` natively.
    tsconfigPaths: true,
    // Platform-split resolution. @insolvia-ai/design-system publishes SOURCE:
    // each per-component index re-exports an extensionless `./button` etc., and
    // the CONSUMER'S bundler picks the leaf by extension. Putting `.web.tsx`
    // first makes Vite pick the DOM leaf and NEVER the `.native.tsx`
    // (react-native) leaf. This list is what keeps react-native /
    // react-native-web out of the web bundle — the grep guard in
    // marketing-pr.yml fails the build if that ever stops holding.
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
  ssr: {
    // @insolvia-ai/design-system must ALWAYS be bundled, dev and build alike:
    // it ships raw .ts/.tsx whose extensionless platform-split leaf imports
    // only Vite (via the `extensions` list above) can resolve — Node at SSR
    // runtime cannot. (The previous web-only package shipped compiled JS, so
    // it was bundled only for the production build, to spare the Lambda image
    // registry auth and its Base UI dependency subtree; that subtree — and its
    // dev-SSR-hostile CJS deps like use-sync-external-store — is gone.)
    //
    // clsx + tailwind-merge (the package's only runtime deps) still join only
    // for the production build: the Docker image installs with
    // `npm ci --omit=dev`, and as transitives of a devDependency they would
    // be missing at Lambda runtime unless bundled in. In dev they load fine
    // from node_modules.
    noExternal:
      command === "build"
        ? ["@insolvia-ai/design-system", "clsx", "tailwind-merge"]
        : ["@insolvia-ai/design-system"],
  },
}));
