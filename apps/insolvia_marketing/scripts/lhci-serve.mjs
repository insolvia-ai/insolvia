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
// internal port and fronts it with a dumb proxy that stamps
// `X-Forwarded-Host: www.insolvia.ai` onto every request — the same shape the
// Lambda sees behind CloudFront. Lighthouse then audits the exact variant
// production serves, robots.txt included.
//
// Used by lighthouserc.json's `startServerCommand`. Not part of the deployed
// app.
//
// PUBLIC_PORT is 3100 and NOT 3000, which it was until this comment appeared.
// 3000 belongs to the app and cannot move: `infra/envs/dev` registers
// `http://localhost:3000` as an exact-match Cognito origin, so the app's own
// dev-up.sh pins it (see docs/reference/terraform.md). This proxy binds its
// port directly rather than negotiating for a free one — it has to, because
// lighthouserc.json's `collect.url` list is static JSON and cannot follow a
// port chosen at runtime — so on a machine running the app it died with a bare
// EADDRINUSE stack trace, which reads as a broken script rather than a port
// clash. CI never saw it: a fresh runner has nothing on 3000.
//
// Keep PUBLIC_PORT and those three URLs in step; they are the one thing here
// that is duplicated, and only a failed run would tell you they drifted.

import { spawn } from "node:child_process";
import http from "node:http";

const INTERNAL_PORT = 3999;
const PUBLIC_PORT = 3100;
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

// Say what a taken port means. Node's default is an unhandled 'error' event —
// a raw EADDRINUSE stack naming net.js, which sends the reader looking for a
// bug in this script instead of at whatever already holds the port.
proxy.on("error", (error) => {
  if (error.code === "EADDRINUSE") {
    console.error(
      `lhci proxy cannot bind :${PUBLIC_PORT} — something else is already listening.\n` +
        `Find it with:  lsof -nP -iTCP:${PUBLIC_PORT} -sTCP:LISTEN\n` +
        `Stop that, or change PUBLIC_PORT here AND the collect.url list in lighthouserc.json.`,
    );
    child.kill("SIGTERM");
    process.exit(1);
  }
  throw error;
});

proxy.listen(PUBLIC_PORT, () => {
  // lighthouserc.json's `startServerReadyPattern` matches this line.
  console.log(`lhci proxy ready on http://localhost:${PUBLIC_PORT}`);
});
