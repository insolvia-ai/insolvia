// Serve the production build for Lighthouse CI, impersonating CloudFront.
//
// The app noindexes every non-production host (issue #48): app/lib/seo.ts keys
// "is this production?" off `X-Forwarded-Host`, the header CloudFront forwards
// in front of the real site. Lighthouse's own `extraHeaders` setting covers the
// page loads but NOT its out-of-band robots.txt fetch, which therefore gets the
// non-production `Disallow: /` body and zeroes the `is-crawlable` audit.
//
// So instead of asking Lighthouse to send the header, this script starts the
// real SSR server (`react-router-serve`, same as `npm run start`) on an
// internal port and fronts it with a dumb proxy on :3000 that stamps
// `X-Forwarded-Host: www.insolvia.ai` onto every request — the same shape the
// Lambda sees behind CloudFront. Lighthouse then audits the exact variant
// production serves, robots.txt included.
//
// Used by lighthouserc.json's `startServerCommand`. Not part of the deployed
// app.

import { spawn } from "node:child_process";
import http from "node:http";

const INTERNAL_PORT = 3999;
const PUBLIC_PORT = 3000;
const PRODUCTION_HOST = "www.insolvia.ai";

const child = spawn(
  "npx",
  ["react-router-serve", "./build/server/index.js"],
  {
    env: { ...process.env, PORT: String(INTERNAL_PORT) },
    stdio: "inherit",
  },
);
child.on("exit", (code) => process.exit(code ?? 1));
for (const signal of ["SIGINT", "SIGTERM"]) {
  process.on(signal, () => {
    child.kill(signal);
    process.exit(0);
  });
}

/** Poll the internal server until it accepts requests. */
async function waitForBackend() {
  for (let attempt = 0; attempt < 100; attempt++) {
    try {
      await new Promise((resolve, reject) => {
        const probe = http.get(
          { hostname: "127.0.0.1", port: INTERNAL_PORT, path: "/" },
          (res) => {
            res.resume();
            resolve();
          },
        );
        probe.on("error", reject);
      });
      return;
    } catch {
      await new Promise((resolve) => setTimeout(resolve, 200));
    }
  }
  throw new Error(`react-router-serve never came up on :${INTERNAL_PORT}`);
}

await waitForBackend();

const proxy = http.createServer((req, res) => {
  const upstream = http.request(
    {
      hostname: "127.0.0.1",
      port: INTERNAL_PORT,
      path: req.url,
      method: req.method,
      headers: { ...req.headers, "x-forwarded-host": PRODUCTION_HOST },
    },
    (upstreamRes) => {
      res.writeHead(upstreamRes.statusCode ?? 502, upstreamRes.headers);
      upstreamRes.pipe(res);
    },
  );
  upstream.on("error", (error) => {
    res.statusCode = 502;
    res.end(String(error));
  });
  req.pipe(upstream);
});

proxy.listen(PUBLIC_PORT, () => {
  // lighthouserc.json's `startServerReadyPattern` matches this line.
  console.log(`lhci proxy ready on http://localhost:${PUBLIC_PORT}`);
});
