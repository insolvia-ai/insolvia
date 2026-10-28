// Tokens the way the real issuers mint them.
//
// - Cognito ACCESS tokens (staff app, client portal, MCP harness): `iss`,
//   `sub`, `token_use: "access"`, `client_id` and NO `aud` — the services
//   compare `client_id`, which is the whole audience check.
// - Google Workspace ID tokens (admin service): `iss` accounts.google.com,
//   `aud` the client id, `hd` the Workspace domain, a verified `email`.
//
// Each helper takes overrides so a test can break exactly one claim.
import { env, stack } from './env.ts';
import { loadSigningKey, type SigningKey } from './jwt.ts';

let key: SigningKey | undefined;
function signer(): SigningKey {
  key ??= loadSigningKey(env.signingKeyPath);
  return key;
}

const now = (): number => Math.floor(Date.now() / 1000);

export type Claims = Record<string, unknown>;

function cognitoAccess(subject: string, clientId: string, overrides: Claims): string {
  const issued = now();
  return signer().sign({
    iss: env.issuerUrl,
    sub: subject,
    username: subject,
    token_use: 'access',
    client_id: clientId,
    scope: 'openid email profile',
    auth_time: issued,
    iat: issued,
    exp: issued + 3600,
    jti: crypto.randomUUID(),
    ...overrides,
  });
}

/** A firm user's access token for the app's client — what the Expo app sends. */
export function staffToken(subject: string, overrides: Claims = {}): string {
  return cognitoAccess(subject, stack.clientIds.app, overrides);
}

/** A portal client's access token (ADR 0023) — the portal's own app client. */
export function portalToken(subject: string, overrides: Claims = {}): string {
  return cognitoAccess(subject, stack.clientIds.portal, overrides);
}

/** An MCP harness's access token — the MCP server's own client (ADR 0016). */
export function mcpToken(subject: string, overrides: Claims = {}): string {
  return cognitoAccess(subject, stack.clientIds.mcp, overrides);
}

/** A Google Workspace ID token for a staff member of the admin portal (ADR 0011). */
export function googleIdToken(overrides: Claims = {}): string {
  const issued = now();
  return signer().sign({
    iss: 'https://accounts.google.com',
    aud: stack.clientIds.google,
    azp: stack.clientIds.google,
    sub: '100000000000000000001',
    hd: stack.workspaceDomain,
    email: 'contract-staff@insolvia.test',
    email_verified: true,
    iat: issued,
    exp: issued + 3600,
    ...overrides,
  });
}
