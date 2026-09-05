/**
 * Environment the E2E suite reads, and the one place credentials enter it.
 *
 * Two rules hold everywhere in this file:
 *
 *   1. **No defaults for credentials.** A default would be a credential
 *      committed to a public repo. `E2E_TEST_USER_PASSWORD` has none and never
 *      will. Addresses are a different matter — see `testUser` below.
 *   2. **A value is never echoed.** Every error below names the *variable*
 *      that is missing, never what it contained. Nothing in this suite
 *      interpolates the password into a test title, an assertion message, a
 *      console line, or a URL.
 *
 * THREE TARGETS, ONE VARIABLE. `E2E_TARGET` says which environment this run
 * is aimed at, and everything that legitimately differs between them follows
 * from it: which seed fixture names the people who exist (dev and staging),
 * which environment label the app's footer must show, and whether the
 * authenticated `flows` project is even offered (never against production,
 * which holds real case data and no test identity — see playwright.config.ts).
 * The URLs still arrive explicitly, with no defaults, for the reason the old
 * staging default was removed: a bare `npm test` must refuse rather than
 * quietly aim at a deployed environment.
 */

import { readFileSync } from 'node:fs';

/** Where a run can be aimed. */
export const TARGETS = ['dev', 'staging', 'production'] as const;
export type Target = (typeof TARGETS)[number];

/** The label the app's footer renders per target (`environmentLabels` in the app). */
const ENVIRONMENT_LABELS: Record<Target, string> = {
  dev: 'Local',
  staging: 'Staging',
  production: 'Production',
};

/** What the API's `/health` reports per target (`INSOLVIA_ENV` on the service side). */
const API_ENVIRONMENTS: Record<Target, string> = {
  dev: 'local',
  staging: 'staging',
  production: 'production',
};

/** Cognito's provided hosted-UI domains all live under this suffix. */
const COGNITO_HOSTED_UI_SUFFIX = '.amazoncognito.com';

function optional(name: string): string | undefined {
  const raw = process.env[name];
  if (raw === undefined) return undefined;
  const trimmed = raw.trim();
  return trimmed === '' ? undefined : trimmed;
}

/**
 * Reads a required variable, or throws naming only the variable.
 *
 * Called from `playwright.config.ts`, so an unset input fails the run during
 * config load — before a browser starts, before anything is typed anywhere —
 * rather than surfacing as a mysterious "sign-in did not complete" ten
 * minutes into a deploy.
 */
export function required(name: string): string {
  const value = optional(name);
  if (value === undefined) {
    throw new Error(
      `${name} is not set. The E2E suite reads its target and its credentials ` +
        `from the environment only — see e2e/README.md.`,
    );
  }
  return value;
}

/** Which environment this run is aimed at. */
export function target(): Target {
  const value = required('E2E_TARGET');
  if (!(TARGETS as readonly string[]).includes(value)) {
    throw new Error(`E2E_TARGET must be one of ${TARGETS.join(', ')}, not '${value}'.`);
  }
  return value as Target;
}

/** The app origin under test. */
export function baseUrl(): string {
  const value = optional('E2E_BASE_URL');
  if (value === undefined) {
    throw new Error(
      'E2E_BASE_URL is not set. For a local run use e2e/scripts/dev-test.sh, ' +
        "which aims the suite at this machine's dev stack; the staging and " +
        'production runs are CI-only (app-staging.yml / app-prod.yml pass this ' +
        'from the Terraform output).',
    );
  }
  return value.replace(/\/+$/, '');
}

/** The API origin the smoke project probes. Required by the specs that use it. */
export function apiUrl(): string {
  return required('E2E_API_URL').replace(/\/+$/, '');
}

/** The marketing site's origin, when the caller has one to hand; unset skips those checks. */
export function marketingUrl(): string | undefined {
  return optional('E2E_MARKETING_URL')?.replace(/\/+$/, '');
}

/** The footer label the app must render for this target. */
export function environmentLabel(): string {
  return ENVIRONMENT_LABELS[target()];
}

/** What `/health` must say for this target. */
export function apiEnvironment(): string {
  return API_ENVIRONMENTS[target()];
}

/**
 * The commit the deploy just shipped, when the workflow passes it. The app's
 * footer carries the first seven characters of `EXPO_PUBLIC_BUILD_SHA`, so a
 * smoke run can prove the bundle it is looking at is the one this run built
 * rather than whatever CloudFront was still serving.
 */
export function expectedBuildStamp(): string | undefined {
  return optional('E2E_EXPECTED_BUILD_SHA')?.slice(0, 7);
}

/**
 * The exact sign-in hostname this environment redirects to, when the caller
 * knows it (the workflow passes the Terraform `auth_domain` output).
 *
 * Unset is legitimate for a local run by someone without Terraform state to
 * hand; `isCognitoHost` then falls back to a weaker assertion.
 */
export function cognitoDomain(): string | undefined {
  return optional('E2E_COGNITO_DOMAIN');
}

/**
 * The app client the redirect must name, when the caller knows it (the
 * workflow passes the Terraform `auth_web_client_id` output). Not a secret —
 * it rides in the query string of every sign-in redirect.
 */
export function cognitoClientId(): string | undefined {
  return optional('E2E_COGNITO_CLIENT_ID');
}

/**
 * True when `hostname` is the sign-in page we expect to be redirected to.
 *
 * The fallback accepts EITHER form because staging and prod now sign in on
 * their own `*-auth.insolvia.ai` domain while dev still uses the Cognito prefix
 * domain, and a local run has no way to tell which it is aimed at. It is only a
 * fallback: CI always passes `E2E_COGNITO_DOMAIN` from the Terraform output and
 * gets the exact-match branch, which is the assertion that would actually catch
 * a redirect to the wrong environment's pool.
 */
export function isCognitoHost(hostname: string): boolean {
  const expected = cognitoDomain();
  if (expected !== undefined) return hostname === expected;
  return hostname.endsWith(COGNITO_HOSTED_UI_SUFFIX) || /^[a-z-]*auth\./.test(hostname);
}

/** Human-readable description of what `isCognitoHost` is checking, for messages. */
export function cognitoHostDescription(): string {
  return cognitoDomain() ?? `any *${COGNITO_HOSTED_UI_SUFFIX} host`;
}

/**
 * The seed fixture this target was provisioned from.
 *
 * ONE FILE DECIDES WHO EXISTS. The same fixture the seeder loads is the one
 * this suite reads, so a person added to an environment and a person a spec
 * can sign in as cannot drift apart — which they did while the address lived
 * in a secret and the firm lived in a fixture. It is `seeds/<target>.json`,
 * derived rather than passed, so the target and the fixture cannot disagree.
 *
 * Addresses are committed on purpose and are safe to be: every one ends in
 * `.test`, a reserved TLD (RFC 2606) that can never be a real mailbox, which
 * is what this package's no-addresses rule protects against. The PASSWORD is
 * still a secret and still has no default.
 */
function fixturePath(): URL {
  const which = target();
  if (which === 'production') {
    throw new Error(
      'There is no seed fixture for production, by design: production holds ' +
        'real case data and no test identity. Only the unauthenticated `smoke` ' +
        'project runs there.',
    );
  }
  return new URL(`../../seeds/${which}.json`, import.meta.url);
}

interface FixtureUser {
  handle?: string;
  email?: string;
}

let fixtureUsers: Map<string, string> | undefined;

function seededUsers(): Map<string, string> {
  if (fixtureUsers !== undefined) return fixtureUsers;
  const fixture = fixturePath();
  let parsed: { firms?: { users?: FixtureUser[] }[] };
  try {
    parsed = JSON.parse(readFileSync(fixture, 'utf8'));
  } catch (cause) {
    throw new Error(`Could not read the seed fixture at ${fixture.pathname}.`, { cause });
  }
  fixtureUsers = new Map(
    (parsed.firms ?? [])
      .flatMap((firm) => firm.users ?? [])
      .filter((user): user is Required<FixtureUser> =>
        Boolean(user.handle && user.email),
      )
      .map((user) => [user.handle, user.email]),
  );
  return fixtureUsers;
}

/**
 * One of the seeded people, by the `handle` the fixture gives them.
 *
 * Defaults to `admin` so a spec that does not care which person it is stays
 * short. A spec that DOES care — cross-firm isolation, a permission refusal —
 * names one, and adding that person is an edit to the fixture rather than a
 * script run and two more secrets.
 *
 * The password is shared by every seeded account and read from the
 * environment; it is the only credential this suite touches.
 */
export function testUser(handle = 'admin'): { email: string; password: string } {
  const email = seededUsers().get(handle);
  if (email === undefined) {
    const known = [...seededUsers().keys()].sort().join(', ') || '(none)';
    throw new Error(
      `No user with handle '${handle}' in ${fixturePath().pathname}. Known handles: ${known}.`,
    );
  }
  return { email, password: required('E2E_TEST_USER_PASSWORD') };
}
