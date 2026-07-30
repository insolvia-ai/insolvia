import { reactRouter } from "@react-router/dev/vite";
import tailwindcss from "@tailwindcss/vite";
import { defineConfig } from "vite";

export default defineConfig(() => ({
  plugins: [tailwindcss(), reactRouter()],
  // Vite 8 resolves tsconfig `paths` natively.
  resolve: { tsconfigPaths: true },
  // No `ssr.noExternal` any more. It existed to bundle the published design
  // system (and its Base UI subtree) into the server build so the runtime image
  // needed no registry auth. The UI now lives in the site's own source
  // (app/ui/), and `clsx`/`tailwind-merge` are ordinary runtime dependencies —
  // nothing external left to inline.
}));
