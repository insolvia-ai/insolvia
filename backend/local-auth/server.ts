// A local stand-in for the Cognito hosted domain, so sign-in needs no AWS
// (ADR 0030). The compose stack runs it on 127.0.0.1:8071.
//
// It speaks what the app and the MCP harnesses speak: /oauth2/authorize,
// /oauth2/token and /logout, plus the issuer's discovery document and JWKS.
// Its tokens are real RS256 JWTs with Cognito's claims, so the backend's
// TokenVerification runs unchanged.
//
// WHAT IT DOES NOT PRETEND. There are no passwords: anyone who can reach this
// port can be anyone. That is safe only because the port is bound to
// 127.0.0.1 and nothing deployed trusts this issuer. The key pair is minted at
// start and never written down, so a restart ends every session. Codes and
// refresh tokens live in memory for the same reason.
//
// WHAT STAYS STRICT, because a bug here would otherwise reach staging first:
// exact-match redirect and logout URIs, S256 PKCE on every authorize, single-use
// codes, and refresh-token rotation with Cognito's 30-second grace period.
import {
  createHash,
  generateKeyPairSync,
  randomBytes,
  randomUUID,
  sign,
  type JsonWebKey,
  type KeyObject,
} from 'node:crypto';
import { existsSync, readFileSync } from 'node:fs';
import { createServer, type IncomingMessage, type Server, type ServerResponse } from 'node:http';
import { fileURLToPath } from 'node:url';

/** SeedIds.Namespace in Insolvia.Migrator, which LocalSubjects shares. */
const SUBJECT_NAMESPACE = '6f1c2b8e-3d4a-4f5b-9c6d-7e8f9a0b1c2d';
const ACCESS_TOKEN_SECONDS = 3600;
const CODE_SECONDS = 300;
const REFRESH_GRACE_SECONDS = 30;
const SCOPE = 'openid email profile';
const ISSUER_PATH = '/local-pool';
const APP_ORIGIN = 'http://localhost:3000';
const INSPECTOR_ORIGIN = 'http://localhost:6274';

interface Client {
  readonly callbacks: readonly string[];
  readonly logouts: readonly string[];
  /** Which seeded people the page offers: firm staff, or case clients. */
  readonly people: 'staff' | 'clients';
}

/** The app clients, as insolvia-services' infra/modules/auth registers them. */
export const CLIENTS: Readonly<Record<string, Client>> = {
  'local-web': {
    callbacks: [`${APP_ORIGIN}/auth/callback`],
    logouts: [APP_ORIGIN],
    people: 'staff',
  },
  'local-portal': {
    callbacks: [`${APP_ORIGIN}/portal/auth/callback`],
    logouts: [`${APP_ORIGIN}/portal`],
    people: 'clients',
  },
  'local-mcp-inspector': {
    callbacks: [
      `${INSPECTOR_ORIGIN}/oauth/callback`,
      `${INSPECTOR_ORIGIN}/oauth/callback/debug`,
      'http://127.0.0.1:6276/oauth/callback',
    ],
    logouts: [],
    people: 'staff',
  },
};

export interface LocalAuthOptions {
  /** The browser-reachable origin, and the base of the issuer. */
  readonly publicUrl: string;
  /** Email to subject, for people a pool created before this server existed. */
  readonly subjects?: Readonly<Record<string, string>>;
  /** Seeded people the sign-in page offers as one-click choices. */
  readonly people?: { readonly staff: readonly string[]; readonly clients: readonly string[] };
  /** The clock, in epoch seconds. Injectable so tests can expire things. */
  readonly now?: () => number;
}

interface Grant {
  readonly clientId: string;
  readonly email: string;
  readonly authTime: number;
}

interface PendingCode extends Grant {
  readonly redirectUri: string;
  readonly challenge: string;
  readonly expiresAt: number;
}

interface RefreshRecord extends Grant {
  /** Set when rotated away: the token keeps working until this moment. */
  retiredAt?: number;
}

/**
 * RFC 4122 §4.3 uuid5, the subject an address resolves to. LocalSubjects.For
 * in Insolvia.Core computes the same value; both pin the same test vector.
 */
export function subjectFor(email: string): string {
  const namespace = Buffer.from(SUBJECT_NAMESPACE.replaceAll('-', ''), 'hex');
  const name = Buffer.from(`local-account/${email.trim().toLowerCase()}`, 'utf8');
  const id = createHash('sha1').update(namespace).update(name).digest().subarray(0, 16);
  id[6] = (id[6]! & 0x0f) | 0x50;
  id[8] = (id[8]! & 0x3f) | 0x80;
  const hex = id.toString('hex');
  return [
    hex.slice(0, 8),
    hex.slice(8, 12),
    hex.slice(12, 16),
    hex.slice(16, 20),
    hex.slice(20),
  ].join('-');
}

function escapeHtml(value: string): string {
  return value
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#39;');
}

function thumbprint(jwk: JsonWebKey): string {
  const canonical = JSON.stringify({ e: jwk.e, kty: jwk.kty, n: jwk.n });
  return createHash('sha256').update(canonical).digest('base64url');
}

function readBody(req: IncomingMessage): Promise<string> {
  return new Promise((done, fail) => {
    const chunks: Buffer[] = [];
    req.on('data', (chunk: Buffer) => chunks.push(chunk));
    req.on('end', () => done(Buffer.concat(chunks).toString('utf8')));
    req.on('error', fail);
  });
}

function send(res: ServerResponse, status: number, body: string, type: string): void {
  res.writeHead(status, {
    'Content-Type': type,
    'Content-Length': Buffer.byteLength(body),
    'Cache-Control': 'no-store',
  });
  res.end(body);
}

function sendJson(res: ServerResponse, status: number, body: unknown): void {
  send(res, status, JSON.stringify(body), 'application/json');
}

function oauthError(res: ServerResponse, error: string): void {
  sendJson(res, 400, { error });
}

/**
 * A refusal for a request whose redirect cannot be trusted. It renders here
 * and never redirects, because the URI is the thing that failed the check.
 */
function refusal(res: ServerResponse, reason: string): void {
  const body = `<!doctype html><title>Local sign-in</title><h1>Sign-in refused</h1><p>${escapeHtml(reason)}</p>`;
  send(res, 400, body, 'text/html; charset=utf-8');
}

/** The authorize parameters, or the reason they are refused. */
function authorizeRequest(params: URLSearchParams): { client: Client } | { refused: string } {
  const client = CLIENTS[params.get('client_id') ?? ''];
  if (!client) return { refused: 'Unknown client_id.' };
  if (!client.callbacks.includes(params.get('redirect_uri') ?? '')) {
    return { refused: 'redirect_uri is not registered for this client.' };
  }
  if (params.get('response_type') !== 'code') return { refused: 'response_type must be code.' };
  if (!params.get('code_challenge') || params.get('code_challenge_method') !== 'S256') {
    return { refused: 'PKCE is required: send code_challenge with method S256.' };
  }
  return { client };
}

const FORWARDED = [
  'client_id',
  'redirect_uri',
  'response_type',
  'state',
  'code_challenge',
  'code_challenge_method',
];

function page(params: URLSearchParams, people: readonly string[]): string {
  const hidden = FORWARDED.map(
    (name) => `<input type="hidden" name="${name}" value="${escapeHtml(params.get(name) ?? '')}">`,
  ).join('');
  const choices = people
    .map(
      (email) =>
        `<form method="post" action="/login">${hidden}<input type="hidden" name="username" value="${escapeHtml(email)}"><button class="choice">${escapeHtml(email)}</button></form>`,
    )
    .join('');
  return `<!doctype html>
<html lang="en">
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Local sign-in</title>
<style>
  :root { color-scheme: light dark; font-family: system-ui, sans-serif; }
  main { max-width: 22rem; margin: 10vh auto; padding: 0 1rem; }
  h1 { font-size: 1.25rem; margin-bottom: .25rem; }
  p { margin-top: 0; opacity: .75; font-size: .9rem; }
  label { display: block; margin: .75rem 0 .25rem; font-size: .9rem; }
  input:not([type=hidden]) { box-sizing: border-box; width: 100%; padding: .5rem; font: inherit; }
  button { width: 100%; margin-top: .75rem; padding: .55rem; font: inherit; cursor: pointer; }
  .choice { margin-top: .4rem; }
  hr { margin: 1.25rem 0; border: 0; border-top: 1px solid #8884; }
</style>
<main>
  <h1>Local sign-in</h1>
  <p>Development only. No password is checked.</p>
  ${choices}
  ${choices ? '<hr>' : ''}
  <form id="primary-form" method="post" action="/login">
    ${hidden}
    <label for="username">Email</label>
    <input id="username" name="username" type="email" autocomplete="off" required>
    <label for="password">Password (ignored)</label>
    <input id="password" name="password" type="password" autocomplete="off">
    <button>Sign in</button>
  </form>
</main>
</html>`;
}

/** Builds the server. The caller decides where it listens. */
export function createLocalAuth(options: LocalAuthOptions): Server {
  const publicUrl = options.publicUrl.replace(/\/+$/, '');
  const issuer = `${publicUrl}${ISSUER_PATH}`;
  const now = options.now ?? (() => Math.floor(Date.now() / 1000));
  const subjects = new Map(
    Object.entries(options.subjects ?? {}).map(([email, subject]) => [
      email.toLowerCase(),
      subject,
    ]),
  );
  const people = options.people ?? { staff: [], clients: [] };

  const { privateKey, publicKey } = generateKeyPairSync('rsa', { modulusLength: 2048 });
  const jwk = publicKey.export({ format: 'jwk' });
  const kid = thumbprint(jwk);
  const jwks = { keys: [{ ...jwk, kid, alg: 'RS256', use: 'sig' }] };

  const codes = new Map<string, PendingCode>();
  const refreshTokens = new Map<string, RefreshRecord>();

  const subjectOf = (email: string): string => subjects.get(email) ?? subjectFor(email);

  function signJwt(claims: Record<string, unknown>, key: KeyObject = privateKey): string {
    const header = Buffer.from(JSON.stringify({ alg: 'RS256', typ: 'JWT', kid })).toString(
      'base64url',
    );
    const payload = Buffer.from(JSON.stringify(claims)).toString('base64url');
    const signature = sign('sha256', Buffer.from(`${header}.${payload}`), key);
    return `${header}.${payload}.${signature.toString('base64url')}`;
  }

  function tokenResponse(grant: Grant, refreshToken: string): Record<string, unknown> {
    const issued = now();
    const subject = subjectOf(grant.email);
    const common = {
      iss: issuer,
      sub: subject,
      auth_time: grant.authTime,
      iat: issued,
      exp: issued + ACCESS_TOKEN_SECONDS,
      jti: randomUUID(),
    };
    return {
      access_token: signJwt({
        ...common,
        username: subject,
        token_use: 'access',
        client_id: grant.clientId,
        scope: SCOPE,
      }),
      id_token: signJwt({
        ...common,
        aud: grant.clientId,
        token_use: 'id',
        email: grant.email,
        email_verified: true,
        'cognito:username': subject,
      }),
      refresh_token: refreshToken,
      token_type: 'Bearer',
      expires_in: ACCESS_TOKEN_SECONDS,
    };
  }

  function mintRefreshToken(grant: Grant): string {
    const token = randomBytes(32).toString('base64url');
    refreshTokens.set(token, { ...grant });
    return token;
  }

  function login(res: ServerResponse, form: URLSearchParams): void {
    const request = authorizeRequest(form);
    if ('refused' in request) {
      refusal(res, request.refused);
      return;
    }
    const email = (form.get('username') ?? '').trim().toLowerCase();
    if (!email.includes('@')) {
      refusal(res, 'Enter an email address.');
      return;
    }
    const code = randomBytes(24).toString('base64url');
    const issued = now();
    codes.set(code, {
      clientId: form.get('client_id')!,
      email,
      authTime: issued,
      redirectUri: form.get('redirect_uri')!,
      challenge: form.get('code_challenge')!,
      expiresAt: issued + CODE_SECONDS,
    });
    const target = new URL(form.get('redirect_uri')!);
    target.searchParams.set('code', code);
    const state = form.get('state');
    if (state) target.searchParams.set('state', state);
    res.writeHead(302, { Location: target.toString(), 'Cache-Control': 'no-store' });
    res.end();
  }

  function exchangeCode(res: ServerResponse, form: URLSearchParams): void {
    const code = form.get('code') ?? '';
    const pending = codes.get(code);
    // Single use, whatever happens next: a failed exchange burns the code.
    codes.delete(code);
    if (!pending || pending.expiresAt < now()) {
      oauthError(res, 'invalid_grant');
      return;
    }
    if (form.get('client_id') !== pending.clientId) {
      oauthError(res, 'invalid_client');
      return;
    }
    if (form.get('redirect_uri') !== pending.redirectUri) {
      oauthError(res, 'invalid_grant');
      return;
    }
    const verifier = form.get('code_verifier') ?? '';
    const challenge = createHash('sha256').update(verifier).digest('base64url');
    if (!verifier || challenge !== pending.challenge) {
      oauthError(res, 'invalid_grant');
      return;
    }
    const grant = {
      clientId: pending.clientId,
      email: pending.email,
      authTime: pending.authTime,
    };
    sendJson(res, 200, tokenResponse(grant, mintRefreshToken(grant)));
  }

  function refresh(res: ServerResponse, form: URLSearchParams): void {
    const token = form.get('refresh_token') ?? '';
    const record = refreshTokens.get(token);
    const expired =
      record?.retiredAt !== undefined && now() > record.retiredAt + REFRESH_GRACE_SECONDS;
    if (!record || expired || form.get('client_id') !== record.clientId) {
      oauthError(res, 'invalid_grant');
      return;
    }
    record.retiredAt ??= now();
    // A refresh keeps auth_time: it is not a new sign-in.
    const grant = { clientId: record.clientId, email: record.email, authTime: record.authTime };
    sendJson(res, 200, tokenResponse(grant, mintRefreshToken(grant)));
  }

  return createServer((req, res) => {
    const url = new URL(req.url ?? '/', publicUrl);
    const origin = req.headers.origin;
    if (origin === APP_ORIGIN || origin === INSPECTOR_ORIGIN) {
      res.setHeader('Access-Control-Allow-Origin', origin);
      res.setHeader('Vary', 'Origin');
    }
    void (async () => {
      const route = `${req.method} ${url.pathname}`;
      switch (route) {
        case 'GET /health':
          sendJson(res, 200, { status: 'ok' });
          return;
        case `GET ${ISSUER_PATH}/.well-known/jwks.json`:
          sendJson(res, 200, jwks);
          return;
        case `GET ${ISSUER_PATH}/.well-known/openid-configuration`:
          sendJson(res, 200, {
            issuer,
            authorization_endpoint: `${publicUrl}/oauth2/authorize`,
            token_endpoint: `${publicUrl}/oauth2/token`,
            jwks_uri: `${issuer}/.well-known/jwks.json`,
            end_session_endpoint: `${publicUrl}/logout`,
            response_types_supported: ['code'],
            grant_types_supported: ['authorization_code', 'refresh_token'],
            code_challenge_methods_supported: ['S256'],
            token_endpoint_auth_methods_supported: ['none'],
            scopes_supported: SCOPE.split(' '),
            subject_types_supported: ['public'],
            id_token_signing_alg_values_supported: ['RS256'],
          });
          return;
        case 'OPTIONS /oauth2/token':
          res.setHeader('Access-Control-Allow-Methods', 'POST');
          res.setHeader('Access-Control-Allow-Headers', 'content-type, accept');
          res.writeHead(204);
          res.end();
          return;
        case 'GET /oauth2/authorize': {
          const request = authorizeRequest(url.searchParams);
          if ('refused' in request) {
            refusal(res, request.refused);
            return;
          }
          const offered = request.client.people === 'clients' ? people.clients : people.staff;
          send(res, 200, page(url.searchParams, offered), 'text/html; charset=utf-8');
          return;
        }
        case 'POST /login':
          login(res, new URLSearchParams(await readBody(req)));
          return;
        case 'POST /oauth2/token': {
          const form = new URLSearchParams(await readBody(req));
          const grantType = form.get('grant_type');
          if (grantType === 'authorization_code') exchangeCode(res, form);
          else if (grantType === 'refresh_token') refresh(res, form);
          else oauthError(res, 'unsupported_grant_type');
          return;
        }
        case 'GET /logout': {
          const client = CLIENTS[url.searchParams.get('client_id') ?? ''];
          const logoutUri = url.searchParams.get('logout_uri') ?? '';
          if (!client?.logouts.includes(logoutUri)) {
            refusal(res, 'logout_uri is not registered for this client.');
            return;
          }
          res.writeHead(302, { Location: logoutUri });
          res.end();
          return;
        }
        default:
          send(res, 404, 'not found', 'text/plain');
      }
    })().catch((error: unknown) => {
      console.error(error);
      if (!res.headersSent) send(res, 500, 'error', 'text/plain');
    });
  });
}

/** The seeded people in seeds/dev.json, for the page's one-click choices. */
export function seededPeople(path: string): { staff: string[]; clients: string[] } {
  if (!existsSync(path)) return { staff: [], clients: [] };
  const seed = JSON.parse(readFileSync(path, 'utf8')) as {
    firms?: { users?: { email: string }[] }[];
    cases?: { clients?: { email: string }[] }[];
  };
  return {
    staff: (seed.firms ?? []).flatMap((firm) => (firm.users ?? []).map((user) => user.email)),
    clients: [
      ...new Set(
        (seed.cases ?? []).flatMap((item) => (item.clients ?? []).map((client) => client.email)),
      ),
    ],
  };
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const port = Number(process.env.LOCAL_AUTH_PORT ?? '8071');
  const server = createLocalAuth({
    publicUrl: process.env.LOCAL_AUTH_PUBLIC_URL ?? `http://localhost:${port}`,
    subjects: JSON.parse(process.env.LOCAL_AUTH_SUBJECTS?.trim() || '{}') as Record<string, string>,
    people: seededPeople(process.env.LOCAL_AUTH_SEED_FILE ?? ''),
  });
  // 127.0.0.1 unless told otherwise. Inside a container the published port
  // needs 0.0.0.0, and compose's 127.0.0.1 port binding keeps it on this machine.
  const host = process.env.LOCAL_AUTH_LISTEN_HOST?.trim() || '127.0.0.1';
  server.listen(port, host, () => console.log(`local auth on ${host}:${port}`));
}
