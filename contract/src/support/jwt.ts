// RS256 JWTs with node:crypto alone — the suite's token mint.
//
// The tokens are real: signed with the run's private key, verified by the
// services against the public half the stack publishes, carrying the claims
// the real issuers put there. Nothing here is a shortcut the backend knows
// about.
import { createHash, createPrivateKey, createPublicKey, sign, type JsonWebKey } from 'node:crypto';
import { readFileSync } from 'node:fs';

function base64url(input: Buffer | string): string {
  return Buffer.from(input).toString('base64url');
}

/** RFC 7638 thumbprint of an RSA public JWK — the kid. */
export function thumbprint(jwk: JsonWebKey): string {
  const canonical = JSON.stringify({ e: jwk.e, kty: jwk.kty, n: jwk.n });
  return createHash('sha256').update(canonical).digest('base64url');
}

export interface SigningKey {
  readonly kid: string;
  sign(claims: Record<string, unknown>): string;
}

export function loadSigningKey(pemPath: string): SigningKey {
  const privateKey = createPrivateKey(readFileSync(pemPath));
  const kid = thumbprint(createPublicKey(privateKey).export({ format: 'jwk' }));
  return {
    kid,
    sign(claims) {
      const header = base64url(JSON.stringify({ alg: 'RS256', typ: 'JWT', kid }));
      const payload = base64url(JSON.stringify(claims));
      const signature = sign('sha256', Buffer.from(`${header}.${payload}`), privateKey);
      return `${header}.${payload}.${base64url(signature)}`;
    },
  };
}
