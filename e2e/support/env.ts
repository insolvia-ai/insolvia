/**
 * Environment the E2E suite reads, and the one place credentials enter it.
 *
 * Two rules hold everywhere in this file:
 *
 *   1. **No defaults for credentials.** A default would be a credential
 *      committed to a public repo. `E2E_TEST_USER_EMAIL` and
 *      `E2E_TEST_USER_PASSWORD` have none and never will.
 *   2. **A value is never echoed.** Every error below names the *variable*
 *      that is missing, never what it contained. Nothing in this suite
 *      interpolates the password into a test title, an assertion message, a
 *      console line, or a URL.
 */

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
 * The exact Cognito hosted-UI hostname this environment redirects to, when the
 * caller knows it (the workflow passes the Terraform `auth_domain` output).
 *
 * Unset is legitimate for a local run by someone without Terraform state to
 * hand; `expectsCognitoHost` then falls back to the weaker suffix assertion.
 */
export function cognitoDomain(): string | undefined {
  return optional('E2E_COGNITO_DOMAIN');
}

/** True when `hostname` is the hosted UI we expect to be redirected to. */
export function isCognitoHost(hostname: string): boolean {
  const expected = cognitoDomain();
  return expected === undefined
    ? hostname.endsWith(COGNITO_HOSTED_UI_SUFFIX)
    : hostname === expected;
}

/** Human-readable description of what `isCognitoHost` is checking, for messages. */
export function cognitoHostDescription(): string {
  return cognitoDomain() ?? `any *${COGNITO_HOSTED_UI_SUFFIX} host`;
}

/**
 * The test user. Read once, here, and passed by value from the spec — so there
 * is exactly one line in this suite that touches the password.
 */
export function testUser(): { email: string; password: string } {
  return {
    email: required('E2E_TEST_USER_EMAIL'),
    password: required('E2E_TEST_USER_PASSWORD'),
  };
}
