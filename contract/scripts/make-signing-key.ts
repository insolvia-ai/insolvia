// Mints a throwaway RS256 keypair for one contract run.
//
//   node contract/scripts/make-signing-key.ts <out-dir>
//
// Writes <out-dir>/signing-key.pem (PKCS#8, the suite signs with it) and
// <out-dir>/jwks.json (the public half, which the stack publishes where its
// services' JWKS providers look). The kid is the key's RFC 7638 thumbprint,
// so the two files can never disagree about which key they describe.
//
// A fresh key per run, never committed: the stack trusts whatever this
// writes, so a key in the repo would be a key anybody could mint with.
import { generateKeyPairSync } from 'node:crypto';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

import { thumbprint } from '../src/support/jwt.ts';

const out = process.argv[2];
if (!out) {
  console.error('usage: make-signing-key.ts <out-dir>');
  process.exit(2);
}
mkdirSync(out, { recursive: true });

const { privateKey, publicKey } = generateKeyPairSync('rsa', { modulusLength: 2048 });
const jwk = publicKey.export({ format: 'jwk' });
const kid = thumbprint(jwk);

writeFileSync(
  join(out, 'signing-key.pem'),
  privateKey.export({ format: 'pem', type: 'pkcs8' }).toString(),
  { mode: 0o600 },
);
writeFileSync(
  join(out, 'jwks.json'),
  JSON.stringify({ keys: [{ ...jwk, kid, alg: 'RS256', use: 'sig' }] }),
);
console.log(`signing key ${kid} written to ${out}`);
