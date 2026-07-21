import { ORIGIN, SEO_ROUTES } from "../lib/seo";

// sitemap.xml (issue #42), generated from the SEO_ROUTES list in app/lib/seo.ts.
// A new page adds itself by appending one entry there. URLs are always the
// canonical production origin, whatever host served this response.
export function loader() {
  const urls = SEO_ROUTES.map(
    ({ path }) => `  <url>\n    <loc>${ORIGIN}${path}</loc>\n  </url>`,
  ).join("\n");
  const body = `<?xml version="1.0" encoding="UTF-8"?>
<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
${urls}
</urlset>
`;
  return new Response(body, {
    headers: {
      "Content-Type": "application/xml; charset=utf-8",
      "Cache-Control": "no-cache",
    },
  });
}
