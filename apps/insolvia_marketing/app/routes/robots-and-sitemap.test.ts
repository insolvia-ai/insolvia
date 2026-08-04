import { afterEach, describe, expect, it, vi } from "vitest";

import { loader as robotsLoader } from "./[robots.txt]";
import { loader as sitemapLoader } from "./[sitemap.xml]";
import { ORIGIN, PRODUCTION_HOST, SEO_ROUTES } from "../lib/seo";

/**
 * Both loaders are pure functions of the request, so they are tested directly
 * rather than through a rendered route.
 *
 * These two files are the whole of the site's crawler contract, and the
 * expensive failure is silent: a staging host that serves the production
 * `robots.txt` gets indexed and competes with production for its own keywords
 * (issue #48), and nothing about the page looks wrong when it happens.
 * `marketing-staging.yml` checks this against deployed staging; these run in
 * milliseconds on every PR, which is where a mistake should actually surface.
 */

// Both loaders take React Router's generated `LoaderArgs` — `request`, `url`,
// `params`, `pattern` and a `context` provider — and read ONLY `request`.
// Building the rest would mean constructing a router context purely to throw
// it away, and it would couple this test to the generated type's shape, so the
// arg is narrowed to what the loaders actually use and cast once, here.
//
// The cast is load-bearing in one direction only: if a loader ever starts
// reading `params` or `context`, it gets `undefined` at runtime and these
// tests fail loudly rather than passing on a lie.
function callLoader<Args>(loader: (args: Args) => Response, host: string): Response {
  const request = new Request(`https://${host}/`);
  return loader({ request } as unknown as Args);
}

describe("robots.txt", () => {
  it("allows crawling on the production host", async () => {
    const body = await callLoader(robotsLoader, PRODUCTION_HOST).text();

    expect(body).toContain("Allow: /");
    expect(body).not.toContain("Disallow: /");
  });

  it("advertises the sitemap on the production host", async () => {
    const body = await callLoader(robotsLoader, PRODUCTION_HOST).text();

    expect(body).toContain(`Sitemap: ${ORIGIN}/sitemap.xml`);
  });

  it.each(["GPTBot", "ClaudeBot", "PerplexityBot"])(
    "explicitly welcomes %s on production",
    async (agent) => {
      // A deliberate decision (issue #42), not an accident of a wildcard rule:
      // most AI crawlers don't execute JS, and the SSR'd HTML is what they see.
      const body = await callLoader(robotsLoader, PRODUCTION_HOST).text();

      expect(body).toContain(`User-agent: ${agent}`);
    },
  );

  it.each([
    "staging.insolvia.ai",
    "insolvia.ai",
    "d111111abcdef8.cloudfront.net",
    "localhost:3000",
  ])("forbids crawling entirely on %s", async (host) => {
    const body = await callLoader(robotsLoader, host).text();

    expect(body).toContain("Disallow: /");
    expect(body).not.toContain("Allow: /");
    expect(body).not.toContain("Sitemap:");
  });

  it("is served as plain text and kept out of shared caches", () => {
    // The body varies by host, so a shared cache could hand a staging response
    // to production or the reverse.
    const response = callLoader(robotsLoader, PRODUCTION_HOST);

    expect(response.headers.get("Content-Type")).toContain("text/plain");
    expect(response.headers.get("Cache-Control")).toBe("no-cache");
  });
});

describe("sitemap.xml", () => {
  it("lists every registered SEO route as an absolute canonical URL", async () => {
    const body = await callLoader(sitemapLoader, PRODUCTION_HOST).text();

    for (const route of SEO_ROUTES) {
      expect(body).toContain(`<loc>${ORIGIN}${route.path}</loc>`);
    }
  });

  it("contains exactly as many entries as there are registered routes", async () => {
    // Guards the generation loop against emitting a stray or duplicated entry.
    const body = await callLoader(sitemapLoader, PRODUCTION_HOST).text();

    expect(body.match(/<loc>/g) ?? []).toHaveLength(SEO_ROUTES.length);
  });

  it("is well-formed enough to declare the sitemap namespace", async () => {
    const body = await callLoader(sitemapLoader, PRODUCTION_HOST).text();

    expect(body).toContain("<urlset");
    expect(body).toContain("sitemaps.org/schemas/sitemap");
  });
});

/**
 * Placeholder mode (see lib/site-mode.server.ts) has to change the crawler
 * contract, not just the page: a holding page indexed as the site's canonical
 * content would have to be undone at launch, and a sitemap advertising pages
 * robots.txt forbids would simply contradict itself.
 */
describe("placeholder mode", () => {
  afterEach(() => {
    vi.unstubAllEnvs();
  });

  it("disallows crawling even on the production host", async () => {
    vi.stubEnv("INSOLVIA_SITE_MODE", "placeholder");

    const body = await callLoader(robotsLoader, PRODUCTION_HOST).text();

    expect(body).toContain("Disallow: /");
    expect(body).not.toContain("Allow: /");
  });

  it("advertises no sitemap entries", async () => {
    vi.stubEnv("INSOLVIA_SITE_MODE", "placeholder");

    const body = await callLoader(sitemapLoader, PRODUCTION_HOST).text();

    expect(body.match(/<loc>/g) ?? []).toHaveLength(0);
    expect(body).toContain("<urlset");
  });

  it("leaves the production contract intact when off", async () => {
    // The regression that matters: placeholder mode must not leak into the
    // normal path, or launching would silently keep the site out of the index.
    vi.stubEnv("INSOLVIA_SITE_MODE", "full");

    const robots = await callLoader(robotsLoader, PRODUCTION_HOST).text();
    const sitemap = await callLoader(sitemapLoader, PRODUCTION_HOST).text();

    expect(robots).toContain("Allow: /");
    expect(sitemap.match(/<loc>/g) ?? []).toHaveLength(SEO_ROUTES.length);
  });
});
