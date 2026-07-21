import { ORIGIN, isProductionHost } from "../lib/seo";
import type { Route } from "./+types/[robots.txt]";

// AI crawlers are explicitly allowed on production (issue #42): an increasing
// share of inbound discovery arrives through them, and most don't execute JS —
// the SSR'd HTML is exactly what they see.
const PRODUCTION_ROBOTS = `# www.insolvia.ai

# AI crawlers are explicitly welcome: an increasing share of inbound discovery
# arrives through them, and most don't execute JS.
User-agent: GPTBot
Allow: /

User-agent: ClaudeBot
Allow: /

User-agent: PerplexityBot
Allow: /

User-agent: *
Allow: /

Sitemap: ${ORIGIN}/sitemap.xml
`;

// Issue #48: any non-production host (staging, PR previews, direct
// CloudFront/API-Gateway URLs, localhost) must not be crawled at all — a
// crawlable staging copy competes with production for its own keywords.
const NON_PRODUCTION_ROBOTS = `# Non-production host — do not crawl.
User-agent: *
Disallow: /
`;

export function loader({ request }: Route.LoaderArgs) {
  const body = isProductionHost(request) ? PRODUCTION_ROBOTS : NON_PRODUCTION_ROBOTS;
  return new Response(body, {
    headers: {
      "Content-Type": "text/plain; charset=utf-8",
      // The body varies by host, so keep shared caches out of it.
      "Cache-Control": "no-cache",
    },
  });
}
