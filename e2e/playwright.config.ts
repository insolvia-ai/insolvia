import { defineConfig, devices } from '@playwright/test';

import { baseUrl, testUser } from './support/env';

/**
 * Playwright configuration for the staging E2E suite.
 *
 * This suite runs against a REAL deployed environment (see README.md). It is
 * not a unit test runner and it is deliberately not a required PR check —
 * `app-staging.yml` runs it after the deploy, which is what makes it gate
 * `verified-commit` without making it gate merges.
 */

// Fail fast, at config load: an unset credential must stop the run before a
// browser launches, not halfway through a sign-in. `testUser()` throws naming
// the missing variable and never its value.
testUser();

export default defineConfig({
  testDir: './tests',

  // ── Flake discipline (issue #40) ───────────────────────────────────────
  //
  // A flaky staging E2E does not merely annoy: a failed staging run means
  // `verified-commit` finds no successful run for the commit, so production
  // promotion is blocked. Two retries in CI absorb the genuinely stochastic
  // failures this suite is exposed to and cannot control — a CloudFront edge
  // that has not yet picked up the invalidation, a Cognito hosted-UI cold
  // response. Retries do NOT paper over a broken auth loop: a build that
  // cannot sign in fails all three attempts.
  //
  // If a test starts passing only on retry, quarantine it (`test.fixme`) and
  // fix it — do not raise this number.
  retries: process.env.CI ? 2 : 0,

  // One worker, no parallelism: there is a single test user, and two browsers
  // signing it in concurrently would race on its Cognito session.
  fullyParallel: false,
  workers: 1,

  // `test.only` left in a commit would silently shrink the suite that blesses
  // a commit for production.
  forbidOnly: !!process.env.CI,

  // ── Timeouts are explicit, everywhere ──────────────────────────────────
  //
  // There is not a single `waitForTimeout` in this suite, by rule: every wait
  // is on a condition (a URL, a visible element, a polled predicate). Sleeps
  // are how an E2E suite becomes both slow and flaky at once.
  //
  // The per-test budget is generous because one test spans four real network
  // hops across two origins: app -> hosted UI -> callback -> app, over
  // CloudFront.
  timeout: 90_000,
  expect: { timeout: 15_000 },
  globalTimeout: 15 * 60_000,

  use: {
    baseURL: baseUrl(),
    actionTimeout: 15_000,
    navigationTimeout: 30_000,

    // ── Why no trace in CI ─────────────────────────────────────────────
    //
    // A Playwright trace records each action's parameters verbatim, and one of
    // this suite's actions is `fill()` on the Cognito password field. There is
    // no supported way to redact an action parameter after the fact. GitHub
    // Actions artifacts on a PUBLIC repository are downloadable by anyone with
    // the run URL, so a trace produced in CI is a password published on the
    // internet the moment it is uploaded.
    //
    // So: no trace is recorded in CI at all (not "recorded but not uploaded" —
    // a file that does not exist cannot be uploaded by a future well-meaning
    // edit to the workflow), and `app-staging.yml` has no upload step. Locally
    // a trace is retained on failure, because it never leaves the machine and
    // it is the only practical way to debug this flow.
    trace: process.env.CI ? 'off' : 'retain-on-failure',

    // Screenshots would mask the password (Cognito's field is `type=password`,
    // so it renders as dots) but would still show the signed-in test user's
    // email address. Same public-artifact reasoning: off in CI, on failure
    // locally. A CI failure is diagnosed from the step log and the assertion
    // messages, which name what was expected without naming any secret.
    screenshot: process.env.CI ? 'off' : 'only-on-failure',
    video: 'off',
  },

  reporter: process.env.CI ? [['github'], ['list']] : [['list']],

  // Chromium only. The auth loop is a redirect round trip, not a rendering
  // concern, so a second engine would triple the runtime of a job that sits on
  // the production-promotion path for no extra signal.
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
});
