// Where the stack under test is, and what it trusts.
//
// Every variable is required and none has a default: a suite that guessed a
// URL would be a run against a stack nobody chose. contract/scripts/dev-test.sh
// sets them all for the local Python stack.
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

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

/** contract/stack/stack.json — the trust configuration the stack was started with. */
export const stack: StackFile = JSON.parse(
  readFileSync(fileURLToPath(new URL('../../stack/stack.json', import.meta.url)), 'utf8'),
) as StackFile;

export const env = {
  apiUrl: required('CONTRACT_API_URL').replace(/\/$/, ''),
  adminUrl: required('CONTRACT_ADMIN_URL').replace(/\/$/, ''),
  /** The MCP endpoint itself, path included (`…/mcp`). */
  mcpUrl: required('CONTRACT_MCP_URL').replace(/\/$/, ''),
  /** The Cognito-shaped issuer the stack's API and MCP server trust. */
  issuerUrl: required('CONTRACT_ISSUER_URL').replace(/\/$/, ''),
  /** PKCS#8 PEM whose public half the stack publishes as its JWKS. */
  signingKeyPath: required('CONTRACT_SIGNING_KEY'),
};

/** The seeded people (seeds/dev.json), by role, with the subjects stack.json gives them. */
export const seeded = {
  firmAdmin: { email: 'dev@insolvia.test', subject: account('dev@insolvia.test') },
  portalClient: {
    email: 'dev-client@insolvia.test',
    subject: account('dev-client@insolvia.test'),
  },
};

function account(email: string): string {
  const subject = stack.accounts[email];
  if (!subject) throw new Error(`contract/stack/stack.json has no account for ${email}`);
  return subject;
}
