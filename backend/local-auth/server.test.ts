// Run with: node --test backend/local-auth/server.test.ts
import assert from 'node:assert/strict';
import { createHash, createPublicKey, verify, type JsonWebKey } from 'node:crypto';
import type { AddressInfo } from 'node:net';
import type { Server } from 'node:http';
import { after, before, describe, it } from 'node:test';

import { createLocalAuth, subjectFor } from './server.ts';

const REDIRECT = 'http://localhost:3000/auth/callback';
const VERIFIER = 'a-verifier-long-enough-to-be-a-real-pkce-code-verifier-value';
const CHALLENGE = createHash('sha256').update(VERIFIER).digest('base64url');

let server: Server;
let base: string;
let clock = 1_800_000_000;

function authorizeParams(overrides: Record<string, string> = {}): URLSearchParams {
  return new URLSearchParams({
    client_id: 'local-web',
    redirect_uri: REDIRECT,
    response_type: 'code',
    state: 'the-state',
    code_challenge: CHALLENGE,
    code_challenge_method: 'S256',
    ...overrides,
  });
}

async function signIn(email: string, overrides: Record<string, string> = {}): Promise<URL> {
  const body = authorizeParams({ username: email, ...overrides });
  const response = await fetch(`${base}/login`, { method: 'POST', body, redirect: 'manual' });
  assert.equal(response.status, 302);
  return new URL(response.headers.get('location')!);
}

async function token(params: Record<string, string>): Promise<Response> {
  return fetch(`${base}/oauth2/token`, { method: 'POST', body: new URLSearchParams(params) });
}

function exchange(code: string, overrides: Record<string, string> = {}): Promise<Response> {
  return token({
    grant_type: 'authorization_code',
    client_id: 'local-web',
    code,
    redirect_uri: REDIRECT,
    code_verifier: VERIFIER,
    ...overrides,
  });
}

function claims(jwt: string): Record<string, unknown> {
  return JSON.parse(Buffer.from(jwt.split('.')[1]!, 'base64url').toString()) as Record<
    string,
    unknown
  >;
}

async function tokens(email: string): Promise<Record<string, string>> {
  const code = (await signIn(email)).searchParams.get('code')!;
  return (await (await exchange(code)).json()) as Record<string, string>;
}

before(async () => {
  server = createLocalAuth({
    publicUrl: 'http://localhost:8071',
    subjects: { 'kept@insolvia.test': '1468c4a8-f001-707f-4254-7f494aaa0f64' },
    people: { staff: ['dev@insolvia.test'], clients: ['dev-client@insolvia.test'] },
    now: () => clock,
  });
  await new Promise<void>((done) => server.listen(0, '127.0.0.1', done));
  base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`;
});

after(() => {
  server.close();
});

describe('subjects', () => {
  it('derives the vector LocalSubjects.For pins in Insolvia.Core.Tests', () => {
    assert.equal(subjectFor('dev@insolvia.test'), 'a035a66c-67ed-5d1c-bf27-3a8b3405d7a3');
  });

  it('treats addresses case-insensitively, as the pool does', () => {
    assert.equal(subjectFor('Dev@Insolvia.Test'), subjectFor('dev@insolvia.test'));
  });

  it('prefers a kept subject over a derived one', async () => {
    const { access_token: access } = await tokens('kept@insolvia.test');
    assert.equal(claims(access!).sub, '1468c4a8-f001-707f-4254-7f494aaa0f64');
  });
});

describe('authorize', () => {
  it('shows the sign-in form with the seeded people', async () => {
    const response = await fetch(`${base}/oauth2/authorize?${authorizeParams()}`);
    const html = await response.text();
    assert.equal(response.status, 200);
    assert.match(html, /id="primary-form"/);
    assert.match(html, /dev@insolvia\.test/);
    assert.doesNotMatch(html, /dev-client@insolvia\.test/);
  });

  it('offers the portal client its own people', async () => {
    const params = authorizeParams({
      client_id: 'local-portal',
      redirect_uri: 'http://localhost:3000/portal/auth/callback',
    });
    const html = await (await fetch(`${base}/oauth2/authorize?${params}`)).text();
    assert.match(html, /dev-client@insolvia\.test/);
  });

  it('escapes what it echoes', async () => {
    const params = authorizeParams({ state: '"><script>alert(1)</script>' });
    const html = await (await fetch(`${base}/oauth2/authorize?${params}`)).text();
    assert.doesNotMatch(html, /<script>alert/);
  });

  for (const [name, overrides] of [
    ['an unknown client', { client_id: 'nobody' }],
    ['an unregistered redirect', { redirect_uri: 'http://localhost:3000/elsewhere' }],
    ['another client’s redirect', { redirect_uri: 'http://localhost:3000/portal/auth/callback' }],
    ['no PKCE', { code_challenge: '' }],
    ['plain PKCE', { code_challenge_method: 'plain' }],
    ['the implicit flow', { response_type: 'token' }],
  ] as const) {
    it(`refuses ${name}`, async () => {
      const params = authorizeParams(overrides);
      const response = await fetch(`${base}/oauth2/authorize?${params}`, { redirect: 'manual' });
      assert.equal(response.status, 400);
      const posted = await fetch(`${base}/login`, {
        method: 'POST',
        body: authorizeParams({ ...overrides, username: 'dev@insolvia.test' }),
        redirect: 'manual',
      });
      assert.equal(posted.status, 400);
    });
  }
});

describe('tokens', () => {
  it('redirects back with a code and the state', async () => {
    const target = await signIn('dev@insolvia.test');
    assert.equal(`${target.origin}${target.pathname}`, REDIRECT);
    assert.equal(target.searchParams.get('state'), 'the-state');
    assert.ok(target.searchParams.get('code'));
  });

  it('mints what TokenVerification requires, signed by the published key', async () => {
    const {
      access_token: access,
      id_token: id,
      expires_in: expiresIn,
    } = await tokens('dev@insolvia.test');
    const body = claims(access!);
    assert.equal(body.iss, 'http://localhost:8071/local-pool');
    assert.equal(body.token_use, 'access');
    assert.equal(body.client_id, 'local-web');
    assert.equal(body.sub, subjectFor('dev@insolvia.test'));
    assert.equal(body.aud, undefined);
    assert.equal(body.exp, clock + 3600);
    assert.equal(expiresIn, 3600);
    assert.equal(claims(id!).email, 'dev@insolvia.test');
    assert.equal(claims(id!).token_use, 'id');

    const [header, payload, signature] = access!.split('.') as [string, string, string];
    const { kid, alg } = JSON.parse(Buffer.from(header, 'base64url').toString()) as {
      kid: string;
      alg: string;
    };
    const published = (await (await fetch(`${base}/local-pool/.well-known/jwks.json`)).json()) as {
      keys: (JsonWebKey & { kid: string })[];
    };
    const jwk = published.keys.find((key) => key.kid === kid)!;
    assert.equal(alg, 'RS256');
    assert.ok(
      verify(
        'sha256',
        Buffer.from(`${header}.${payload}`),
        createPublicKey({ key: jwk, format: 'jwk' }),
        Buffer.from(signature, 'base64url'),
      ),
    );
  });

  it('refuses a wrong verifier, and burns the code', async () => {
    const code = (await signIn('dev@insolvia.test')).searchParams.get('code')!;
    const wrong = await exchange(code, { code_verifier: 'not-the-verifier' });
    assert.equal(wrong.status, 400);
    assert.deepEqual(await wrong.json(), { error: 'invalid_grant' });
    assert.equal((await exchange(code)).status, 400);
  });

  it('accepts a code once', async () => {
    const code = (await signIn('dev@insolvia.test')).searchParams.get('code')!;
    assert.equal((await exchange(code)).status, 200);
    assert.equal((await exchange(code)).status, 400);
  });

  it('refuses an expired code', async () => {
    const code = (await signIn('dev@insolvia.test')).searchParams.get('code')!;
    clock += 301;
    assert.equal((await exchange(code)).status, 400);
  });

  it('refuses a code for another client', async () => {
    const code = (await signIn('dev@insolvia.test')).searchParams.get('code')!;
    const response = await exchange(code, { client_id: 'local-portal' });
    assert.deepEqual(await response.json(), { error: 'invalid_client' });
  });

  it('rotates refresh tokens, keeps auth_time, and allows the 30-second grace', async () => {
    const first = await tokens('dev@insolvia.test');
    const signedInAt = claims(first.access_token!).auth_time;
    const refresh = { grant_type: 'refresh_token', client_id: 'local-web' };
    clock += 600;

    const rotated = (await (
      await token({ ...refresh, refresh_token: first.refresh_token! })
    ).json()) as Record<string, string>;
    assert.notEqual(rotated.refresh_token, first.refresh_token);
    assert.equal(claims(rotated.access_token!).auth_time, signedInAt);

    clock += 30;
    assert.equal((await token({ ...refresh, refresh_token: first.refresh_token! })).status, 200);
    clock += 1;
    const late = await token({ ...refresh, refresh_token: first.refresh_token! });
    assert.deepEqual(await late.json(), { error: 'invalid_grant' });
    assert.equal((await token({ ...refresh, refresh_token: rotated.refresh_token! })).status, 200);
  });
});

describe('logout', () => {
  it('returns to a registered URI', async () => {
    const params = new URLSearchParams({
      client_id: 'local-web',
      logout_uri: 'http://localhost:3000',
    });
    const response = await fetch(`${base}/logout?${params}`, { redirect: 'manual' });
    assert.equal(response.status, 302);
    assert.equal(response.headers.get('location'), 'http://localhost:3000');
  });

  it('refuses any other URI', async () => {
    const params = new URLSearchParams({ client_id: 'local-web', logout_uri: 'https://evil.test' });
    const response = await fetch(`${base}/logout?${params}`, { redirect: 'manual' });
    assert.equal(response.status, 400);
  });
});

describe('discovery and CORS', () => {
  it('publishes its endpoints under the issuer', async () => {
    const response = await fetch(`${base}/local-pool/.well-known/openid-configuration`);
    const document = (await response.json()) as Record<string, unknown>;
    assert.equal(document.issuer, 'http://localhost:8071/local-pool');
    assert.equal(document.token_endpoint, 'http://localhost:8071/oauth2/token');
  });

  it('allows the app origin only', async () => {
    const allowed = await fetch(`${base}/oauth2/token`, {
      method: 'OPTIONS',
      headers: { Origin: 'http://localhost:3000' },
    });
    assert.equal(allowed.headers.get('access-control-allow-origin'), 'http://localhost:3000');
    const refused = await fetch(`${base}/oauth2/token`, {
      method: 'OPTIONS',
      headers: { Origin: 'https://evil.test' },
    });
    assert.equal(refused.headers.get('access-control-allow-origin'), null);
  });
});
