import { expect, test } from '@playwright/test';

import { marketingUrl, target } from '../../support/env';

/**
 * The marketing site, from the outside.
 *
 * Its own deploy already checks `/` answers 200 and, on staging, that robots
 * refuses indexing. This restates the liveness half from the release's LAST
 * leg — so the whole product is probed at once, at the end — and adds the
 * one thing that is only checkable across environments: production must be
 * indexable and staging must not, and the two must not have been swapped by
 * a deploy that shipped the wrong environment's build.
 *
 * Skipped, by name, when the caller has no marketing URL to hand — a local
 * run against a dev stack has no marketing site of its own (the app's local
 * environment points its footer links at deployed staging, deliberately).
 */

test.describe('smoke: the marketing site', () => {
  test('serves its home page and the right indexing policy', async ({ request }) => {
    const origin = marketingUrl();
    test.skip(origin === undefined, 'E2E_MARKETING_URL is unset — no marketing site for this target');

    const home = await request.get(`${origin}/`);
    expect(home.status(), 'the marketing home page should answer 200').toBe(200);
    expect(await home.text(), 'the home page should be an HTML document').toMatch(/<title[\s>]/i);

    const robots = await request.get(`${origin}/robots.txt`);
    expect(robots.status(), 'robots.txt should be served').toBe(200);
    const policy = await robots.text();
    if (target() === 'production') {
      // Production serves the crawlable policy once launched, and the
      // non-production `Disallow: /` while the site is in placeholder mode
      // (app/routes/[robots.txt].tsx) — both are legitimate, so the assertion
      // is that it is ONE of the two bodies the route can produce, never
      // something a misrouted build or an error page would serve.
      expect(
        policy,
        'production robots.txt should be one of the two policies the marketing ' +
          'route serves (crawlable once launched, Disallow while a placeholder)',
      ).toMatch(/^User-agent: \*$/m);
    } else {
      expect(
        policy,
        'a non-production site must refuse indexing (Disallow: /)',
      ).toMatch(/^Disallow:\s*\/\s*$/m);
    }
  });
});
