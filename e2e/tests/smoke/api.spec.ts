import { expect, test } from '@playwright/test';

import { apiEnvironment, apiUrl, baseUrl } from '../../support/env';

/**
 * The deployed API, from the outside, with no credentials.
 *
 * The deploy workflows already curl `/health` and check the environment
 * name; this keeps that and adds the two properties a health check cannot
 * see: that a protected route FAILS CLOSED (a 401 with the API's own body,
 * not a 500 from lost auth configuration and not a 200 from a bypass), and
 * that CORS admits the app's origin and nobody else's — the one failure in
 * this system that carries no message at all when it breaks, because the
 * browser refuses the response before the app can read it.
 *
 * Playwright's `request` fixture rather than the browser: these are HTTP
 * facts, and a page would add nothing but a place for a redirect to hide.
 */

test.describe('smoke: the deployed API', () => {
  test('reports this environment on /health', async ({ request }) => {
    const response = await request.get(`${apiUrl()}/health`);
    expect(response.status(), '/health should answer 200').toBe(200);
    const body = await response.json();
    expect(body.status).toBe('ok');
    expect(body.service).toBe('insolvia-api');
    expect(
      body.environment,
      'the API should name the environment this run targets — a mismatch means ' +
        'the suite is aimed at the wrong API, or the Lambda lost INSOLVIA_ENV',
    ).toBe(apiEnvironment());
  });

  test('a protected route without a token is a 401, not a 500', async ({ request }) => {
    const response = await request.get(`${apiUrl()}/v1/me`);
    expect(
      response.status(),
      'an anonymous /v1/me should be refused with 401 — a 500 means the deployed ' +
        'configuration lost its auth settings and the fail-closed path is what is ' +
        'answering; a 200 would be a bypass',
    ).toBe(401);
  });

  test('CORS admits the app’s origin and refuses a stranger’s', async ({ request }) => {
    const appOrigin = new URL(baseUrl()).origin;
    const preflight = (origin: string) =>
      request.fetch(`${apiUrl()}/v1/me`, {
        method: 'OPTIONS',
        headers: {
          Origin: origin,
          'Access-Control-Request-Method': 'GET',
          'Access-Control-Request-Headers': 'authorization',
        },
      });

    const ours = await preflight(appOrigin);
    expect(
      ours.headers()['access-control-allow-origin'],
      `a preflight from ${appOrigin} should be admitted — without this every ` +
        'authenticated request from the app dies in the browser with no message',
    ).toBe(appOrigin);

    const theirs = await preflight('https://not-insolvia.example');
    expect(
      theirs.headers()['access-control-allow-origin'] ?? null,
      'a preflight from an unlisted origin must not be admitted — the allowlist is ' +
        'exact origins, no wildcard (services/api core/config.py)',
    ).not.toBe('https://not-insolvia.example');
  });
});
