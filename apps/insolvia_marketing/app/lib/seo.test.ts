import { describe, expect, it } from "vitest";

import {
  ORGANIZATION_JSONLD,
  ORIGIN,
  PRODUCTION_HOST,
  SEO_ROUTES,
  isProductionHost,
  seo,
} from "./seo";

function requestFor(url: string, headers: Record<string, string> = {}): Request {
  return new Request(url, { headers });
}

/**
 * `isProductionHost` is an allowlist, and the reason it is an allowlist rather
 * than a staging denylist is that it must fail CLOSED: a host nobody
 * anticipated should be noindexed by default, because a crawlable copy of the
 * site competes with production for its own keywords (issue #48). Every case
 * below exists to keep that property, so a future refactor toward
 * "block the hosts we know about" fails here first.
 */
describe("isProductionHost", () => {
  it("accepts exactly the production host", () => {
    expect(isProductionHost(requestFor(`https://${PRODUCTION_HOST}/`))).toBe(true);
  });

  it("is case-insensitive about the host", () => {
    expect(
      isProductionHost(requestFor("https://example.test/", { "x-forwarded-host": "WWW.Insolvia.AI" })),
    ).toBe(true);
  });

  it.each([
    ["staging", "staging.insolvia.ai"],
    ["the apex, which 301s to www at the infra layer", "insolvia.ai"],
    ["a direct CloudFront URL", "d111111abcdef8.cloudfront.net"],
    ["a direct API Gateway URL", "abc123.execute-api.us-east-1.amazonaws.com"],
    ["localhost", "localhost:3000"],
    ["a lookalike suffix", "notwww.insolvia.ai"],
    ["a lookalike prefix", "www.insolvia.ai.evil.test"],
  ])("noindexes %s", (_label, host) => {
    expect(isProductionHost(requestFor(`https://${host}/`))).toBe(false);
  });

  it("prefers X-Forwarded-Host over the request's own host", () => {
    // Behind CloudFront → API Gateway the Lambda sees the API Gateway domain as
    // its own host; the viewer's real host only arrives in this header.
    expect(
      isProductionHost(
        requestFor("https://abc123.execute-api.us-east-1.amazonaws.com/", {
          "x-forwarded-host": PRODUCTION_HOST,
        }),
      ),
    ).toBe(true);
  });

  it("uses only the first value of a comma-separated X-Forwarded-Host", () => {
    expect(
      isProductionHost(
        requestFor("https://example.test/", {
          "x-forwarded-host": `${PRODUCTION_HOST}, evil.test`,
        }),
      ),
    ).toBe(true);
    expect(
      isProductionHost(
        requestFor("https://example.test/", {
          "x-forwarded-host": `evil.test, ${PRODUCTION_HOST}`,
        }),
      ),
    ).toBe(false);
  });

  it("falls back to the request host when X-Forwarded-Host is empty", () => {
    expect(
      isProductionHost(
        requestFor(`https://${PRODUCTION_HOST}/`, { "x-forwarded-host": "" }),
      ),
    ).toBe(true);
  });
});

describe("seo", () => {
  it("does not suffix the site name on the home page title", () => {
    const meta = seo({ title: "Insolvia", description: "d", path: "/" });

    expect(meta).toContainEqual({ title: "Insolvia" });
  });

  it("suffixes the site name on every other page", () => {
    const meta = seo({ title: "Privacy", description: "d", path: "/privacy" });

    expect(meta).toContainEqual({ title: "Privacy · Insolvia" });
  });

  it("builds canonical and og:url absolutely from the canonical origin", () => {
    const meta = seo({ title: "Waitlist", description: "d", path: "/waitlist" });

    expect(meta).toContainEqual({
      tagName: "link",
      rel: "canonical",
      href: `${ORIGIN}/waitlist`,
    });
    expect(meta).toContainEqual({
      property: "og:url",
      content: `${ORIGIN}/waitlist`,
    });
  });

  it("defaults to the home path when none is given", () => {
    const meta = seo({ title: "Insolvia", description: "d" });

    expect(meta).toContainEqual({ tagName: "link", rel: "canonical", href: `${ORIGIN}/` });
  });

  it("uses a summary card without an image and a large one with", () => {
    const without = seo({ title: "t", description: "d" });
    const withImage = seo({ title: "t", description: "d", image: "https://img.test/a.png" });

    expect(without).toContainEqual({ name: "twitter:card", content: "summary" });
    expect(withImage).toContainEqual({
      name: "twitter:card",
      content: "summary_large_image",
    });
    expect(withImage).toContainEqual({
      property: "og:image",
      content: "https://img.test/a.png",
    });
  });

  it("keeps the description identical across name, og and twitter", () => {
    const description = "Bankruptcy case preparation and e-filing.";
    const meta = seo({ title: "t", description });

    const descriptions = meta.filter(
      (d) =>
        ("name" in d && d.name === "description") ||
        ("property" in d && d.property === "og:description") ||
        ("name" in d && d.name === "twitter:description"),
    );
    expect(descriptions).toHaveLength(3);
    for (const d of descriptions) {
      expect((d as { content: string }).content).toBe(description);
    }
  });
});

describe("SEO_ROUTES", () => {
  it("lists only absolute paths", () => {
    // sitemap.xml is generated from this list, so a relative entry would
    // produce an invalid sitemap URL.
    for (const route of SEO_ROUTES) expect(route.path.startsWith("/")).toBe(true);
  });

  it("has no duplicates", () => {
    const paths = SEO_ROUTES.map((r) => r.path);
    expect(new Set(paths).size).toBe(paths.length);
  });
});

describe("ORGANIZATION_JSONLD", () => {
  it("is valid JSON describing the organisation at the canonical origin", () => {
    const parsed = JSON.parse(ORGANIZATION_JSONLD);

    expect(parsed["@type"]).toBe("Organization");
    expect(parsed.url).toBe(ORIGIN);
  });
});
