/**
 * The holding page for an environment in placeholder mode.
 *
 * Says nothing about what Insolvia does. That is the whole requirement: the
 * apex has to resolve (see `lib/site-mode.server.ts`) before the positioning is
 * ready to be public, so this page exists to occupy the domain and nothing
 * else. No product claims, no audience, no waitlist, no navigation.
 *
 * It is rendered with `noindex` set and behind a `Disallow: /` robots.txt, so
 * search engines do not index a thin page as the site's canonical content and
 * the real launch gets a clean first crawl.
 */
export function Holding() {
  return (
    <div className="flex min-h-screen flex-col items-center justify-center px-lg text-center">
      <h1 className="font-display text-3xl text-ink sm:text-4xl">Insolvia</h1>
      <p className="mt-md max-w-prose text-lg text-muted">Something is being built here.</p>
    </div>
  );
}
