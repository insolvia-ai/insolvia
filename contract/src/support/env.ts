// Where the stack under test is, and what it trusts.
//
// Every variable is required and none has a default: a suite that guessed a
// URL would be a run against a stack nobody chose. contract/scripts/dev-test.sh
// sets them all for the local .NET stack; support/session.ts names the
// deployed target's sign-in inputs.
//
// The getters are lazy, so a deployed run never asks for the offline stack's
// signing key, and a run that never touches admin or MCP never asks for
// their URLs.
// A JSON import rather than a read beside `import.meta.url`: Jest runs this
// file transpiled to CommonJS (jest.base.js), where `import.meta` does not
// exist.
import stackJson from '../../stack/stack.json' with { type: 'json' };
import { deployedInput, session } from './session.ts';
import { person, TARGET } from './target.ts';

function required(name: string): string {
  const value = process.env[name]?.trim();
  if (!value) {
    throw new Error(
      `${name} is not set. The contract suite takes its target from the environment ` +
        'only — run it through contract/scripts/dev-test.sh, or see contract/README.md.',
    );
  }
  return value;
}

function offline(name: string): string {
  if (TARGET !== 'offline') {
    throw new Error(
      `${name} belongs to the offline stack; a spec that reads it on the deployed target ` +
        'must be marked offlineOnly (support/target.ts).',
    );
  }
  return required(name);
}

interface StackFile {
  readonly issuerPath: string;
  readonly clientIds: {
    readonly app: string;
    readonly portal: string;
    readonly mcp: string;
    readonly google: string;
  };
  readonly workspaceDomain: string;
  readonly accounts: Readonly<Record<string, string>>;
}

/** contract/stack/stack.json — the trust configuration the offline stack was started with. */
export const stack: StackFile = stackJson;

export const env = {
  get apiUrl(): string {
    return required('CONTRACT_API_URL').replace(/\/$/, '');
  },
  get adminUrl(): string {
    return required('CONTRACT_ADMIN_URL').replace(/\/$/, '');
  },
  /**
   * The admin portal's origin, which the admin service's CORS must admit:
   * a loopback origin offline and on a dev stack, the portal's own on staging.
   */
  get adminPortalOrigin(): string {
    return required('CONTRACT_ADMIN_ORIGIN').replace(/\/$/, '');
  },
  /** The MCP endpoint itself, path included (`…/mcp`). */
  get mcpUrl(): string {
    return required('CONTRACT_MCP_URL').replace(/\/$/, '');
  },
  /**
   * The issuer the API and MCP server trust. Offline, the run's own
   * Cognito-shaped issuer; deployed, the real pool's, which Cognito names
   * after the pool (`<region>_<id>`).
   */
  get issuerUrl(): string {
    if (TARGET === 'deployed') {
      const pool = deployedInput('CONTRACT_AUTH_POOL_ID');
      return `https://cognito-idp.${pool.split('_')[0]}.amazonaws.com/${pool}`;
    }
    return offline('CONTRACT_ISSUER_URL').replace(/\/$/, '');
  },
  /** PKCS#8 PEM whose public half the offline stack publishes as its JWKS. */
  get signingKeyPath(): string {
    return offline('CONTRACT_SIGNING_KEY');
  },
};

/** A seeded person as a test sees them: who the seed says, and the subject they sign in as. */
export interface SeededIdentity {
  readonly handle: string;
  readonly email: string;
  readonly firmName: string;
  readonly subject: string;
}

function identity(handle: string): SeededIdentity {
  const who = person(handle);
  return {
    handle,
    email: who.email,
    firmName: who.firmName,
    get subject(): string {
      return subjectOf(handle);
    },
  };
}

/**
 * The subject a seeded person signs in as. Offline: the one the migrator
 * derives, which stack.json lists. Deployed: the real pool's, read off the
 * token the global setup minted.
 */
export function subjectOf(handle: string): string {
  if (TARGET === 'deployed') return session(handle).subject;
  const email = person(handle).email;
  const subject = stack.accounts[email];
  if (!subject) throw new Error(`contract/stack/stack.json has no account for ${email}`);
  return subject;
}

/** The seeded firm administrator and portal client, by the seed's handles. */
export const seeded = {
  firmAdmin: identity('admin'),
  portalClient: identity('client'),
};
