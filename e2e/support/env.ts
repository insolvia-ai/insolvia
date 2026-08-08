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
 */

import { readFileSync } from 'node:fs';

/**
 * The staging app's public origin.
 *
 * The workflow passes the Terraform `url` output instead, so this constant is
 * the local-run convenience, not the source of truth. It is a public hostname,
 * not a secret.
 */
export const DEFAULT_BASE_URL = 'https://staging-app.insolvia.ai';

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
 * Called from `playwright.config.ts`, so an unset credential fails the run
 * during config load — before a browser starts, before anything is typed
 * anywhere — rather than surfacing as a mysterious "sign-in did not complete"
 * ten minutes into a deploy.
 */
export function required(name: string): string {
  const value = optional(name);
  if (value === undefined) {
    throw new Error(
      `${name} is not set. The staging E2E suite reads its test-user ` +
        `credentials from the environment only — see docs/runbooks/staging-e2e-setup.md.`,
    );
  }
  return value;
}

/** The origin under test. */
export function baseUrl(): string {
  return optional('E2E_BASE_URL') ?? DEFAULT_BASE_URL;
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
 * The seed fixture this environment was provisioned from.
 *
 * ONE FILE DECIDES WHO EXISTS. The same fixture the seeder loads is the one
 * this suite reads, so a person added to an environment and a person a spec
 * can sign in as cannot drift apart — which they did while the address lived
 * in a secret and the firm lived in a fixture.
 *
 * Addresses are committed on purpose and are safe to be: every one ends in
 * `.test`, a reserved TLD (RFC 2606) that can never be a real mailbox, which
 * is what this package's no-addresses rule protects against. The PASSWORD is
 * still a secret and still has no default.
 */
const FIXTURE = new URL(
  `../../${optional('E2E_SEED_FIXTURE') ?? 'seeds/staging.json'}`,
  import.meta.url,
);

interface FixtureUser {
  handle?: string;
  email?: string;
}

let fixtureUsers: Map<string, string> | undefined;

function seededUsers(): Map<string, string> {
  if (fixtureUsers !== undefined) return fixtureUsers;
  let parsed: { firms?: { users?: FixtureUser[] }[] };
  try {
    parsed = JSON.parse(readFileSync(FIXTURE, 'utf8'));
  } catch (cause) {
    throw new Error(`Could not read the seed fixture at ${FIXTURE.pathname}.`, { cause });
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
      `No user with handle '${handle}' in ${FIXTURE.pathname}. Known handles: ${known}.`,
    );
  }
  return { email, password: required('E2E_TEST_USER_PASSWORD') };
}
