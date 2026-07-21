import type { MetaDescriptor } from "react-router";

/**
 * Site-wide SEO configuration and helpers for www.insolvia.ai (issue #42),
 * plus the single source of truth for "is this the production host" (issue #48).
 */

export const SITE_NAME = "Insolvia";

/** Canonical origin. Every canonical/OG/sitemap URL is built from this. */
export const ORIGIN = "https://www.insolvia.ai";

/**
 * The one host that is allowed to be indexed. Everything else — staging hosts,
 * PR previews, direct CloudFront/API-Gateway URLs, localhost, and even the apex
 * `insolvia.ai` (which 301s to www at the infra layer) — gets noindex.
 */
export const PRODUCTION_HOST = "www.insolvia.ai";

/**
 * Every indexable route on the marketing site. `sitemap.xml` is generated from
 * this list, so a new page registers itself for SEO by adding one entry here
 * and calling `seo()` from its route `meta` export — nothing else to wire up.
 */
export interface SeoRoute {
  /** Route path, starting with "/". */
  path: string;
}

export const SEO_ROUTES: SeoRoute[] = [{ path: "/" }];

/**
 * Is this request being served on the production host?
 *
 * Issue #48: a crawlable staging copy competes with production for its own
 * keywords, so anything that is not exactly the production host must be
 * noindexed. The check is an allowlist — `www.insolvia.ai` is indexable,
 * every other host (staging, PR previews, direct CloudFront/API-Gateway URLs,
 * localhost, the apex) is not — because an allowlist fails closed when a new
 * host starts serving the app.
 *
 * Host precedence: `X-Forwarded-Host` (first value) → the host of
 * `request.url`. Behind CloudFront → API Gateway the Lambda sees the API
 * Gateway domain as its own host (CloudFront strips the viewer `Host`), so the
 * viewer's real host only arrives via the forwarded header.
 */
export function isProductionHost(request: Request): boolean {
  const forwarded = request.headers.get("x-forwarded-host");
  const host = (forwarded?.split(",")[0]?.trim() || new URL(request.url).host).toLowerCase();
  return host === PRODUCTION_HOST;
}

export interface SeoInput {
  title: string;
  description: string;
  /** Route path, starting with "/". Used for the canonical + og:url. */
  path?: string;
  /** Absolute URL of a social-share image. Omitted → summary twitter card. */
  image?: string;
  type?: "website" | "article";
}

/** Build a complete, consistent set of title/description/OG/Twitter/canonical
 * meta descriptors for a route's `meta` export. */
export function seo({ title, description, path = "/", image, type = "website" }: SeoInput) {
  const fullTitle = path === "/" ? title : `${title} · ${SITE_NAME}`;
  const url = `${ORIGIN}${path}`;
  const meta: MetaDescriptor[] = [
    { title: fullTitle },
    { name: "description", content: description },
    { property: "og:title", content: fullTitle },
    { property: "og:description", content: description },
    { property: "og:type", content: type },
    { property: "og:url", content: url },
    { property: "og:site_name", content: SITE_NAME },
    { name: "twitter:card", content: image ? "summary_large_image" : "summary" },
    { name: "twitter:title", content: fullTitle },
    { name: "twitter:description", content: description },
    { tagName: "link", rel: "canonical", href: url },
  ];
  if (image) {
    meta.push({ property: "og:image", content: image });
    meta.push({ name: "twitter:image", content: image });
  }
  return meta;
}

/** schema.org Organization, SSR-rendered from the root layout (#42). */
export const ORGANIZATION_JSONLD = JSON.stringify({
  "@context": "https://schema.org",
  "@type": "Organization",
  name: SITE_NAME,
  url: ORIGIN,
});
