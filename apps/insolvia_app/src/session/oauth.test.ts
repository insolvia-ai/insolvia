import {
  authorizeUrl,
  callbackUrlFor,
  exchangeCodeForTokens,
  logoutUrl,
  OAuthError,
  refreshTokens,
} from '@/session';
import type { FetchLike } from '@/session';
import {
  TEST_AUTH_CONFIG,
  TEST_ORIGIN,
  tokenEndpointError,
  tokenEndpointResponse,
} from '@/session/testing';

/** The query string of a built URL, parsed into a plain map. */
function queryOf(url: string): Record<string, string> {
  const [, query = ''] = url.split('?');
  const params: Record<string, string> = {};
  for (const pair of query.split('&')) {
    const [key = '', value = ''] = pair.split('=');
    params[decodeURIComponent(key)] = decodeURIComponent(value);
  }
  return params;
}

/** The form-encoded body of a recorded `fetch` call, parsed the same way. */
function bodyOf(init: RequestInit | undefined): Record<string, string> {
  return queryOf(`?${String(init?.body ?? '')}`);
}

describe('the authorize URL', () => {
  const built = authorizeUrl(TEST_AUTH_CONFIG, {
    redirectUri: callbackUrlFor(TEST_ORIGIN),
    state: 'test-state-value',
    codeChallenge: 'test-code-challenge',
  });

  /**
   * ADR 0007 asks for this test by name, and the reason is worth restating
   * where it will be read: **Cognito has no server-side "require PKCE" toggle.**
   * It honours a `code_challenge` when one arrives and cannot insist that one
   * does, so no infrastructure test can catch these parameters going missing.
   * This assertion is the only thing standing between a refactor and a sign-in
   * flow that silently downgrades to a bare authorization-code grant.
   */
  it('carries the PKCE challenge and names S256 as the method', () => {
    expect(queryOf(built).code_challenge).toBe('test-code-challenge');
    expect(queryOf(built).code_challenge_method).toBe('S256');
  });

  it('never downgrades to the plain challenge method', () => {
    // `plain` sends the verifier itself, which defeats the point of PKCE.
    expect(built).not.toContain('plain');
  });

  it('points at the hosted domain and asks for an authorization code', () => {
    expect(built.startsWith(`${TEST_AUTH_CONFIG.domain}/oauth2/authorize?`)).toBe(true);
    expect(queryOf(built)).toMatchObject({
      response_type: 'code',
      client_id: TEST_AUTH_CONFIG.clientId,
      redirect_uri: `${TEST_ORIGIN}/auth/callback`,
      scope: 'openid email profile',
      state: 'test-state-value',
    });
  });

  it('requests the redirect path infra registers, exactly', () => {
    // `web_callback_urls` is "${origin}/auth/callback" and Cognito matches it
    // with no wildcards at all — no host, path or port pattern.
    expect(callbackUrlFor(TEST_ORIGIN)).toBe(`${TEST_ORIGIN}/auth/callback`);
  });
});

describe('the logout URL', () => {
  it('sends logout_uri as the bare origin', () => {
    // NOT `redirect_uri`, and NOT an origin with a path appended:
    // `web_logout_urls = var.web_origins` registers the origins themselves, and
    // Cognito matches exactly. Getting this wrong yields a generic Cognito
    // error page instead of a sign-out.
    const built = logoutUrl(TEST_AUTH_CONFIG, TEST_ORIGIN);

    expect(built.startsWith(`${TEST_AUTH_CONFIG.domain}/logout?`)).toBe(true);
    expect(queryOf(built)).toEqual({
      client_id: TEST_AUTH_CONFIG.clientId,
      logout_uri: TEST_ORIGIN,
    });
  });
});

describe('the authorization-code exchange', () => {
  it('posts a form-encoded grant with the verifier, and no client secret', async () => {
    const fetchMock: jest.MockedFunction<FetchLike> = jest
      .fn()
      .mockResolvedValue(tokenEndpointResponse());

    await exchangeCodeForTokens(
      TEST_AUTH_CONFIG,
      {
        code: 'test-authorization-code',
        redirectUri: callbackUrlFor(TEST_ORIGIN),
        codeVerifier: 'test-code-verifier',
      },
      fetchMock,
    );

    const [url, init] = fetchMock.mock.calls[0] ?? [];
    expect(url).toBe(`${TEST_AUTH_CONFIG.domain}/oauth2/token`);
    expect(init?.method).toBe('POST');
    expect(init?.headers).toMatchObject({
      'Content-Type': 'application/x-www-form-urlencoded',
    });

    expect(bodyOf(init)).toEqual({
      grant_type: 'authorization_code',
      client_id: TEST_AUTH_CONFIG.clientId,
      code: 'test-authorization-code',
      redirect_uri: `${TEST_ORIGIN}/auth/callback`,
      code_verifier: 'test-code-verifier',
    });

    // A public client (`generate_secret = false`) has no secret to present,
    // and Cognito rejects one it never issued.
    expect(bodyOf(init).client_secret).toBeUndefined();
    expect(init?.headers).not.toHaveProperty('Authorization');
  });

  it('turns expires_in into an absolute deadline', async () => {
    const fetchMock: jest.MockedFunction<FetchLike> = jest
      .fn()
      .mockResolvedValue(tokenEndpointResponse({ expiresIn: 3600 }));

    const before = Date.now();
    const tokens = await exchangeCodeForTokens(
      TEST_AUTH_CONFIG,
      { code: 'c', redirectUri: callbackUrlFor(TEST_ORIGIN), codeVerifier: 'v' },
      fetchMock,
    );

    expect(tokens.expiresAt).toBeGreaterThanOrEqual(before + 3_600_000);
    expect(tokens.expiresAt).toBeLessThan(before + 3_601_000);
  });

  it('falls back to a conservative lifetime when expires_in is missing', async () => {
    // Erring short costs one extra refresh; erring long sends expired tokens
    // to the API.
    const fetchMock: jest.MockedFunction<FetchLike> = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      text: () => Promise.resolve(JSON.stringify({ access_token: 'a' })),
    } as unknown as Response);

    const tokens = await exchangeCodeForTokens(
      TEST_AUTH_CONFIG,
      { code: 'c', redirectUri: callbackUrlFor(TEST_ORIGIN), codeVerifier: 'v' },
      fetchMock,
    );

    expect(tokens.expiresAt).toBeLessThanOrEqual(Date.now() + 60_000);
  });
});

describe('the refresh-token grant', () => {
  /**
   * The flow that must NOT be used here is Cognito's `REFRESH_TOKEN_AUTH` SDK
   * flow: `explicit_auth_flows` on the web client is `["ALLOW_USER_SRP_AUTH"]`
   * only, because Cognito rejects `ALLOW_REFRESH_TOKEN_AUTH` outright when
   * refresh-token rotation is enabled — and rotation is enabled. The hosted
   * domain's OAuth token endpoint is the only refresh path available.
   */
  it('posts an OAuth refresh_token grant to the hosted token endpoint', async () => {
    const fetchMock: jest.MockedFunction<FetchLike> = jest
      .fn()
      .mockResolvedValue(tokenEndpointResponse());

    await refreshTokens(TEST_AUTH_CONFIG, 'stored-refresh-token', fetchMock);

    const [url, init] = fetchMock.mock.calls[0] ?? [];
    expect(url).toBe(`${TEST_AUTH_CONFIG.domain}/oauth2/token`);
    expect(bodyOf(init)).toEqual({
      grant_type: 'refresh_token',
      client_id: TEST_AUTH_CONFIG.clientId,
      refresh_token: 'stored-refresh-token',
    });
  });

  it('surfaces the rotated replacement token', async () => {
    // Rotation retires the token just used, so the replacement has to reach
    // the caller — dropping it signs the user out at the next reload.
    const fetchMock: jest.MockedFunction<FetchLike> = jest
      .fn()
      .mockResolvedValue(tokenEndpointResponse({ refreshToken: 'rotated-refresh-token' }));

    const tokens = await refreshTokens(TEST_AUTH_CONFIG, 'old-refresh-token', fetchMock);

    expect(tokens.refreshToken).toBe('rotated-refresh-token');
  });

  it('reports null when a response does not rotate', async () => {
    const fetchMock: jest.MockedFunction<FetchLike> = jest
      .fn()
      .mockResolvedValue(tokenEndpointResponse({ refreshToken: null }));

    const tokens = await refreshTokens(TEST_AUTH_CONFIG, 'old-refresh-token', fetchMock);

    expect(tokens.refreshToken).toBeNull();
  });

  it('raises the OAuth error code for a retired or revoked token', async () => {
    const fetchMock: jest.MockedFunction<FetchLike> = jest
      .fn()
      .mockResolvedValue(tokenEndpointError('invalid_grant', 400));

    await expect(refreshTokens(TEST_AUTH_CONFIG, 'retired', fetchMock)).rejects.toMatchObject({
      name: 'OAuthError',
      code: 'invalid_grant',
      statusCode: 400,
    });
  });

  it('never puts token material in the error it raises', async () => {
    // A message is a thing that gets logged.
    const fetchMock: jest.MockedFunction<FetchLike> = jest.fn().mockResolvedValue(
      // A server that echoed the token back would be the worst case; the client
      // must not propagate it regardless.
      {
        ok: false,
        status: 400,
        text: () =>
          Promise.resolve(
            JSON.stringify({ error: 'invalid_grant', error_description: 'secret-token-value' }),
          ),
      } as unknown as Response,
    );

    const error = await refreshTokens(TEST_AUTH_CONFIG, 'secret-token-value', fetchMock).catch(
      (cause: unknown) => cause,
    );

    expect(error).toBeInstanceOf(OAuthError);
    expect(JSON.stringify(error)).not.toContain('secret-token-value');
    expect((error as OAuthError).message).not.toContain('secret-token-value');
  });
});
