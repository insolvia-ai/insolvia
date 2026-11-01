// Sign in to a Cognito user pool headlessly, with SRP and nothing else — the
// deployed target's only way in (ADR 0029 rule 2).
//
// WHY SRP, WHEN A PASSWORD FLOW WOULD BE THREE LINES. The pool's web and
// portal app clients (insolvia-services' infra/modules/auth) allow exactly one
// explicit flow, `ALLOW_USER_SRP_AUTH`: nothing should ever ship a raw
// password through them. A test suite that turned `ALLOW_USER_PASSWORD_AUTH`
// on for its own convenience would loosen that decision on every pool it runs
// against. SRP is the flow the clients already permit, so this speaks it.
//
// WHY NOT A LIBRARY, AND NO AWS SDK. `InitiateAuth`, `RespondToAuthChallenge`
// and `GetTokensFromRefreshToken` are UNSIGNED Cognito operations: a plain
// JSON POST with no AWS credentials. That is load-bearing — a CI job holding a
// pool id, a client id and a password is enough, and every API call after
// sign-in runs under the deployed service's OWN role, never a developer's.
// node:crypto and BigInt carry the arithmetic.
//
// The arithmetic is the SRP-6a variant Cognito speaks (RFC 5054 group 15, the
// 3072-bit prime; Cognito's "Caldera Derived Key" HKDF label; a timestamp in
// Cognito's own format). Every constant below is Cognito's, not ours. It was
// ported from integration/tests/cognito_srp.py, which signed in against the
// dev pool and staging on every run until #582 retired it.
//
// NEVER LOG THE PASSWORD. It arrives as an argument, is hashed once, and goes
// nowhere else. Errors name the failure, never the credential.
import { createHash, createHmac, randomBytes } from 'node:crypto';

// RFC 5054, 3072-bit group. Cognito's SRP uses this N and g = 2.
const N_HEX =
  'FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74' +
  '020BBEA63B139B22514A08798E3404DDEF9519B3CD3A431B302B0A6DF25F1437' +
  '4FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7ED' +
  'EE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3DC2007CB8A163BF05' +
  '98DA48361C55D39A69163FA8FD24CF5F83655D23DCA3AD961C62F356208552BB' +
  '9ED529077096966D670C354E4ABC9804F1746C08CA18217C32905E462E36CE3B' +
  'E39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9DE2BCBF695581718' +
  '3995497CEA956AE515D2261898FA051015728E5A8AAAC42DAD33170D04507A33' +
  'A85521ABDF1CBA64ECFB850458DBEF0A8AEA71575D060C7DB3970F85A6E1E4C7' +
  'ABF5AE8CDB0933D71E8C94E04A25619DCEE3D2261AD2EE6BF12FFA06D98A0864' +
  'D87602733EC86A64521F2B18177B200CBBE117577A615D6C770988C0BAD946E2' +
  '08E24FA074E5AB3143DB5BFCE0FD108E4B82D120A93AD2CAFFFFFFFFFFFFFFFF';
const G_HEX = '2';
const INFO_BITS = Buffer.from('Caldera Derived Key');
const N = BigInt(`0x${N_HEX}`);
const G = BigInt(`0x${G_HEX}`);

const WEEKDAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'] as const;
const MONTHS = [
  'Jan',
  'Feb',
  'Mar',
  'Apr',
  'May',
  'Jun',
  'Jul',
  'Aug',
  'Sep',
  'Oct',
  'Nov',
  'Dec',
] as const;

/** The pool refused, or answered with something this handshake does not understand. */
export class SignInError extends Error {}

/** What Cognito's `AuthenticationResult` carries that the suite reads. */
export interface AuthenticationResult {
  readonly AccessToken: string;
  readonly IdToken?: string;
  readonly RefreshToken?: string;
}

const toBig = (hex: string): bigint => BigInt(`0x${hex}`);

function modPow(base: bigint, exponent: bigint, modulus: bigint): bigint {
  let result = 1n;
  let b = ((base % modulus) + modulus) % modulus;
  let e = exponent;
  while (e > 0n) {
    if (e & 1n) result = (result * b) % modulus;
    b = (b * b) % modulus;
    e >>= 1n;
  }
  return result;
}

function hashBuffer(buffer: Buffer): string {
  return createHash('sha256').update(buffer).digest('hex').padStart(64, '0');
}

const hexHash = (hex: string): string => hashBuffer(Buffer.from(hex, 'hex'));

/**
 * Cognito's byte-padding rule: even length, and a leading zero byte when the
 * top bit is set so the number is not read as negative.
 */
function padHex(value: bigint | string): string {
  let hex = typeof value === 'string' ? value : value.toString(16);
  if (hex.length % 2 === 1) hex = `0${hex}`;
  else if ('89abcdefABCDEF'.includes(hex[0] ?? '')) hex = `00${hex}`;
  return hex;
}

function hkdf(ikm: Buffer, salt: Buffer): Buffer {
  const prk = createHmac('sha256', salt).update(ikm).digest();
  return createHmac('sha256', prk)
    .update(Buffer.concat([INFO_BITS, Buffer.from([1])]))
    .digest()
    .subarray(0, 16);
}

/**
 * Day of month UNPADDED — Cognito's format, verified rather than guessed: a
 * zero-padded day makes the signature fail with "Incorrect username or
 * password", which is the least helpful error the pool has.
 */
function cognitoTimestamp(now: Date): string {
  const two = (n: number): string => String(n).padStart(2, '0');
  return (
    `${WEEKDAYS[now.getUTCDay()]} ${MONTHS[now.getUTCMonth()]} ${now.getUTCDate()} ` +
    `${two(now.getUTCHours())}:${two(now.getUTCMinutes())}:${two(now.getUTCSeconds())} ` +
    `UTC ${now.getUTCFullYear()}`
  );
}

const K = toBig(hexHash(`00${N_HEX}0${G_HEX}`));

class Srp {
  readonly poolName: string;
  readonly smallA: bigint;
  readonly largeA: bigint;

  constructor(poolId: string) {
    this.poolName = poolId.split('_', 2)[1] ?? '';
    let a: bigint;
    let largeA: bigint;
    do {
      a = toBig(randomBytes(128).toString('hex')) % N;
      largeA = modPow(G, a, N);
    } while (largeA % N === 0n);
    this.smallA = a;
    this.largeA = largeA;
  }

  private passwordKey(userId: string, password: string, serverB: bigint, salt: string): Buffer {
    const u = toBig(hexHash(padHex(this.largeA) + padHex(serverB)));
    if (u === 0n) throw new SignInError("SRP: the server's public value hashed to zero");
    const identity = hashBuffer(Buffer.from(`${this.poolName}${userId}:${password}`));
    const x = toBig(hexHash(padHex(salt) + identity));
    const base = serverB - K * modPow(G, x, N);
    const s = modPow(base, this.smallA + u * x, N);
    return hkdf(Buffer.from(padHex(s), 'hex'), Buffer.from(padHex(u.toString(16)), 'hex'));
  }

  challengeResponse(params: Record<string, string>, password: string): Record<string, string> {
    const userId = params.USER_ID_FOR_SRP;
    const salt = params.SALT;
    const serverB = params.SRP_B;
    const secretBlock = params.SECRET_BLOCK;
    if (!userId || !salt || !serverB || !secretBlock) {
      throw new SignInError(
        "SRP: the pool's challenge is missing a parameter — is USER_SRP_AUTH enabled on this app client?",
      );
    }
    const timestamp = cognitoTimestamp(new Date());
    const key = this.passwordKey(userId, password, toBig(serverB), salt);
    const message = Buffer.concat([
      Buffer.from(this.poolName),
      Buffer.from(userId),
      Buffer.from(secretBlock, 'base64'),
      Buffer.from(timestamp),
    ]);
    return {
      TIMESTAMP: timestamp,
      USERNAME: userId,
      PASSWORD_CLAIM_SECRET_BLOCK: secretBlock,
      PASSWORD_CLAIM_SIGNATURE: createHmac('sha256', key).update(message).digest('base64'),
    };
  }
}

/** One unsigned Cognito call. A refusal comes back as `{__type, message}`. */
async function cognito(
  poolId: string,
  operation: string,
  body: Record<string, unknown>,
): Promise<Record<string, unknown>> {
  const region = poolId.split('_', 1)[0];
  const response = await fetch(`https://cognito-idp.${region}.amazonaws.com/`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/x-amz-json-1.1',
      'X-Amz-Target': `AWSCognitoIdentityProviderService.${operation}`,
    },
    body: JSON.stringify(body),
  });
  const answer = (await response.json().catch(() => ({}))) as Record<string, unknown>;
  if (!response.ok) {
    // The type is sometimes namespaced (`com.amazonaws…#NotAuthorizedException`).
    const type = String(answer.__type ?? `HTTP ${response.status}`)
      .split('#')
      .at(-1);
    throw new SignInError(`${operation} was refused (${type})`);
  }
  return answer;
}

function authenticationResult(answer: Record<string, unknown>): AuthenticationResult {
  const result = answer.AuthenticationResult as AuthenticationResult | undefined;
  if (!result?.AccessToken) throw new SignInError('the pool returned no AuthenticationResult');
  return result;
}

/** Complete the SRP handshake and return Cognito's `AuthenticationResult`. */
export async function signIn(options: {
  poolId: string;
  clientId: string;
  email: string;
  password: string;
}): Promise<AuthenticationResult> {
  const srp = new Srp(options.poolId);
  let opened: Record<string, unknown>;
  try {
    opened = await cognito(options.poolId, 'InitiateAuth', {
      AuthFlow: 'USER_SRP_AUTH',
      AuthParameters: { USERNAME: options.email, SRP_A: srp.largeA.toString(16) },
      ClientId: options.clientId,
    });
  } catch (error) {
    throw new SignInError(
      `${(error as Error).message}. With prevent_user_existence_errors on, a wrong password ` +
        'and a missing account look identical — check the seed step ran for this target.',
    );
  }
  if (opened.ChallengeName !== 'PASSWORD_VERIFIER') {
    throw new SignInError(
      `expected a PASSWORD_VERIFIER challenge, got ${String(opened.ChallengeName)}`,
    );
  }
  const answered = await cognito(options.poolId, 'RespondToAuthChallenge', {
    ClientId: options.clientId,
    ChallengeName: 'PASSWORD_VERIFIER',
    ChallengeResponses: srp.challengeResponse(
      opened.ChallengeParameters as Record<string, string>,
      options.password,
    ),
  });
  if (answered.ChallengeName) {
    // NEW_PASSWORD_REQUIRED is what a seeded account presents when the
    // seeder's permanent-password step did not run; an MFA challenge means
    // somebody enrolled a factor on a test account, which the staging
    // runbook forbids.
    throw new SignInError(
      `the pool wants a further challenge (${String(answered.ChallengeName)}); a seeded ` +
        'test account must sign in without one.',
    );
  }
  return authenticationResult(answered);
}

/** A refresh, as the app makes one (refresh-token rotation is on for the clients). */
export async function refresh(options: {
  poolId: string;
  clientId: string;
  refreshToken: string;
}): Promise<AuthenticationResult> {
  return authenticationResult(
    await cognito(options.poolId, 'GetTokensFromRefreshToken', {
      RefreshToken: options.refreshToken,
      ClientId: options.clientId,
    }),
  );
}

/** A JWT's claims, read — NOT verified. For what the real pool wrote into its own token. */
export function claimsOf(token: string): Record<string, unknown> {
  const payload = token.split('.')[1];
  if (!payload) throw new Error('not a JWT');
  return JSON.parse(Buffer.from(payload, 'base64url').toString('utf8')) as Record<string, unknown>;
}
