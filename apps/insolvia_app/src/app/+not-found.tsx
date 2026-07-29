import { usePathname } from 'expo-router';

import { NotFound } from '@/screens/not-found';

/**
 * The catch-all route — the direct port of go_router's `errorBuilder`.
 *
 * **Not optional.** `infra/modules/web_hosting` has CloudFront rewrite 403/404 to
 * `/index.html` with HTTP **200**, which is what makes deep links work in a
 * single-page export. The consequence is that no mistyped URL ever 404s at the
 * edge: it reaches the bundle, and this route is the only thing that can tell the
 * user nothing lives there. Without it, expo-router renders its own unmatched
 * page — a developer artifact on the brand's own domain.
 */
export default function NotFoundRoute() {
  return <NotFound location={usePathname()} />;
}
