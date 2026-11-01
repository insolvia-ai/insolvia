// The deployed target's signed-in people: one real SRP sign-in per seeded
// person, made ONCE per run by the global setup and handed to the test files
// through the environment (Jest's globalSetup runs before any worker starts,
// and workers inherit it — the coverage gate's directory travels the same
// way). Never written to disk.
//
// Once, because SRP is several round trips and a token is valid for an hour;
// minting per test would add minutes and prove nothing a single mint does
// not. The acts that need a RECENT sign-in (ADR 0024: signing the filing
// authorization, approving a filing) take `freshSignIn` instead — a new SRP
// sign-in now, a real password verified by the real pool, which is what
// `auth_time` records.
//
// Inputs, all required, none defaulted (this repo is public; a guessed pool
// would be a run against an environment nobody chose):
//   CONTRACT_AUTH_POOL_ID           the user pool
//   CONTRACT_AUTH_CLIENT_ID         the staff app's client
//   CONTRACT_AUTH_PORTAL_CLIENT_ID  the client portal's (ADR 0023)
//   E2E_TEST_USER_PASSWORD          the seeded people's shared password
//   CONTRACT_AUTH_MCP_CLIENT_ID     an MCP harness's client (the inspector's),
//                                   read only by a run that reaches the MCP
//                                   specs (mcpSignIn below)
import { type AuthenticationResult, claimsOf, signIn, SignInError } from './cognito-srp.ts';
import { PEOPLE, person, type SeededPerson } from './target.ts';

const SESSIONS = 'CONTRACT_SESSIONS';

export interface Session {
  readonly token: string;
  readonly subject: string;
}

export function deployedInput(name: string): string {
  const value = process.env[name]?.trim();
  if (!value) {
    throw new Error(
      `${name} is not set. The deployed target takes its target and its credentials from ` +
        'the environment only — see contract/README.md. On CI an environment-scoped secret ' +
        'resolves to an empty string in silence when a job is missing its `environment:` ' +
        'key, so check that first.',
    );
  }
  return value;
}

/** A new SRP sign-in for one seeded person, through the app client their kind uses. */
export async function freshSignIn(handle: string): Promise<AuthenticationResult> {
  const who: SeededPerson = person(handle);
  return signInThrough(
    handle,
    who.kind === 'client' ? 'CONTRACT_AUTH_PORTAL_CLIENT_ID' : 'CONTRACT_AUTH_CLIENT_ID',
  );
}

/**
 * A new SRP sign-in for one seeded firm user through an MCP harness's app
 * client — the token an agent's harness holds after the person signs in
 * there. The MCP server admits it only if that client is on its allowlist
 * (AUTH_CLIENT_IDS), which is what the deployed MCP specs prove. Made per
 * test file rather than in the global setup, so a run that never reaches
 * the MCP specs (api-staging.yml's) never needs the input.
 */
export async function mcpSignIn(handle: string): Promise<AuthenticationResult> {
  return signInThrough(handle, 'CONTRACT_AUTH_MCP_CLIENT_ID');
}

async function signInThrough(handle: string, clientInput: string): Promise<AuthenticationResult> {
  const who: SeededPerson = person(handle);
  try {
    return await signIn({
      poolId: deployedInput('CONTRACT_AUTH_POOL_ID'),
      clientId: deployedInput(clientInput),
      email: who.email,
      password: deployedInput('E2E_TEST_USER_PASSWORD'),
    });
  } catch (error) {
    if (error instanceof SignInError) {
      throw new Error(`could not sign in as '${handle}': ${error.message}`);
    }
    throw error;
  }
}

export function sessionOf(token: string): Session {
  const subject = claimsOf(token).sub;
  if (typeof subject !== 'string') throw new Error('the access token carried no subject');
  return { token, subject };
}

/** Called from the global setup on the deployed target: everyone, signed in once. */
export async function startSessions(): Promise<void> {
  const sessions: Record<string, Session> = {};
  for (const who of PEOPLE) {
    sessions[who.handle] = sessionOf((await freshSignIn(who.handle)).AccessToken);
  }
  if (Object.keys(sessions).length === 0) {
    throw new Error('the seed names nobody with a handle and an email');
  }
  process.env[SESSIONS] = JSON.stringify(sessions);
}

let cache: Record<string, Session> | undefined;

/** The run's session for one seeded person. Deployed target only. */
export function session(handle: string): Session {
  cache ??= (JSON.parse(process.env[SESSIONS] ?? 'null') as Record<string, Session> | null) ?? {};
  const found = cache[handle];
  if (!found) {
    throw new Error(
      `no session for '${handle}': run the suite through Jest, whose global setup signs ` +
        'the seeded people in on the deployed target',
    );
  }
  return found;
}
