import { screen, userEvent } from '@testing-library/react-native';
import { renderRouter } from 'expo-router/testing-library';

import type { AuthConfig } from '@/config/environment';
import { writeRefreshToken } from '@/session';
import {
  fakeJwt,
  installFakeBrowser,
  jsonResponse,
  routeFetch,
  TEST_AUTH_CONFIG,
  TEST_EMAIL,
  tokenEndpointResponse,
} from '@/session/testing';
import type { FakeBrowser } from '@/session/testing';

let mockAuthConfig: AuthConfig | null = null;

jest.mock('@/config/environment', () => ({
  ...jest.requireActual('@/config/environment'),
  resolveAuthConfig: () => mockAuthConfig,
}));

const ALICE = '00000000-0000-4000-8000-00000000a11c';
const VERSION = '2026-10-07-draft';
const DIGEST = 'c526ac3fdfc34b9b2a03a42818b0e94eebad131472387fc516f0282b15fe3ce5';
const TEXT = {
  version: VERSION,
  digest: DIGEST,
  text: 'DRAFT, pending review.\n\nI allow Insolvia to file under my login.\n',
};
const UNSIGNED = { text: TEXT, current_version: VERSION, current: false, signature: null };
const SIGNATURE = {
  id: 'a0700000-0000-4000-8000-000000000001',
  text_version: VERSION,
  text_digest: DIGEST,
  signed_at: '2100-01-01T00:01:00.000000Z',
};
const SIGNED = { text: TEXT, current_version: VERSION, current: true, signature: SIGNATURE };

function membership(electronicFiling: 'view_only' | 'add_edit') {
  return {
    subject: ALICE,
    username: null,
    clientId: 'exampleappclientid000000',
    scopes: [],
    expiresAt: null,
    firm: {
      id: '00000000-0000-4000-8000-00000000f18a',
      name: 'Example & Partners',
      role: 'attorney',
      firstName: 'Alice',
      lastName: 'Attorney',
      displayName: 'Alice Attorney',
      isAdmin: false,
      accessAllCases: false,
      permissions: { cases: 'add_edit', electronic_filing: electronicFiling },
    },
  };
}

/**
 * The filing authorization on `/account` (ADR 0024, guardrail 2): read it,
 * sign it only after a fresh sign-in, see what was signed, withdraw it with
 * a confirmation. The API is the judge of freshness; these tests pin what
 * the screen offers and what it sends.
 */
describe('the filing authorization', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  function render(options: {
    readonly status: unknown;
    readonly signedInSecondsAgo: number | null;
    readonly write?: (url: string, init: RequestInit) => Response;
    readonly level?: 'view_only' | 'add_edit';
  }) {
    const authTime =
      options.signedInSecondsAgo === null
        ? {}
        : { auth_time: Math.floor(Date.now() / 1000) - options.signedInSecondsAgo };
    const route = routeFetch({
      '/oauth2/token': () =>
        tokenEndpointResponse({ idToken: fakeJwt({ email: TEST_EMAIL, sub: ALICE, ...authTime }) }),
      '/v1/me/filing-authorization': () => jsonResponse(200, options.status),
      '/v1/me/filing-credentials': () => jsonResponse(200, { credentials: [] }),
      '/v1/me': () => jsonResponse(200, membership(options.level ?? 'add_edit')),
    });
    const fetchMock = jest.fn((url: string, init?: RequestInit) =>
      options.write !== undefined &&
      url.includes('/v1/me/filing-authorization') &&
      init?.method !== undefined &&
      init.method !== 'GET'
        ? Promise.resolve(options.write(url, init))
        : route(url),
    );
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    renderRouter('src/app', { initialUrl: '/account' });
    return fetchMock;
  }

  beforeEach(() => {
    mockAuthConfig = TEST_AUTH_CONFIG;
    browser = installFakeBrowser();
    writeRefreshToken('stored-refresh-token');
  });

  afterEach(() => {
    globalThis.fetch = realFetch;
    browser.restore();
    jest.clearAllMocks();
  });

  it('shows the text and, after an old sign-in, asks for a fresh one with prompt=login', async () => {
    render({ status: UNSIGNED, signedInSecondsAgo: 3600 });
    const user = userEvent.setup();

    expect(await screen.findByText('I allow Insolvia to file under my login.')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Sign authorization' })).toBeNull();
    // No login form until it is signed — only the pointer to it.
    expect(screen.queryByLabelText('PACER password')).toBeNull();
    expect(
      screen.getByText('Sign the filing authorization above before storing your court login.'),
    ).toBeTruthy();

    await user.press(screen.getByRole('button', { name: 'Sign in again to sign' }));

    const authorize = browser.navigations.at(-1) ?? '';
    expect(authorize.startsWith(`${TEST_AUTH_CONFIG.domain}/oauth2/authorize?`)).toBe(true);
    expect(authorize).toContain('prompt=login');
  });

  it('after a fresh sign-in, signs the version and digest it showed, once agreed', async () => {
    const fetchMock = render({
      status: UNSIGNED,
      signedInSecondsAgo: 10,
      write: () => jsonResponse(201, SIGNED),
    });
    const user = userEvent.setup();

    const signButton = await screen.findByRole('button', { name: 'Sign authorization' });
    expect(signButton).toBeDisabled();
    await user.press(
      screen.getByRole('checkbox', { name: 'I have read this authorization and I agree to it' }),
    );
    await user.press(screen.getByRole('button', { name: 'Sign authorization' }));

    expect(await screen.findByText('You have signed the filing authorization.')).toBeTruthy();
    const post = fetchMock.mock.calls.find(
      ([url, init]) => init?.method === 'POST' && url.includes('/v1/me/filing-authorization'),
    );
    expect(JSON.parse(post?.[1]?.body as string)).toEqual({
      text_version: VERSION,
      text_digest: DIGEST,
    });
    expect(screen.getByText(/You signed version 2026-10-07-draft on 2100-01-01/)).toBeTruthy();
    // Signed: the login form is offered now.
    expect(await screen.findByLabelText('PACER password')).toBeTruthy();
  });

  it('falls back to signing in again when the API says the sign-in is stale', async () => {
    render({
      status: UNSIGNED,
      signedInSecondsAgo: 10,
      write: () =>
        jsonResponse(403, {
          error: 'ReauthenticationRequired',
          message: 'sign in again to continue',
        }),
    });
    const user = userEvent.setup();

    await user.press(
      await screen.findByRole('checkbox', {
        name: 'I have read this authorization and I agree to it',
      }),
    );
    await user.press(screen.getByRole('button', { name: 'Sign authorization' }));

    expect(await screen.findByText(/no longer recent enough to sign/)).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Sign in again to sign' })).toBeTruthy();
  });

  it('says so when the text changed since the attorney signed', async () => {
    render({
      status: {
        ...UNSIGNED,
        signature: { ...SIGNATURE, text_version: '2026-01-01-older' },
      },
      signedInSecondsAgo: 3600,
    });

    expect(
      await screen.findByText(/has changed since you signed version 2026-01-01-older/),
    ).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Withdraw authorization' })).toBeTruthy();
  });

  it('withdraws only after confirming, and says the logins were destroyed', async () => {
    const fetchMock = render({
      status: SIGNED,
      signedInSecondsAgo: 3600,
      write: () => jsonResponse(200, { credentials_revoked: 1 }),
    });
    const user = userEvent.setup();

    await user.press(await screen.findByRole('button', { name: 'Withdraw authorization' }));
    expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'DELETE')).toBe(false);
    expect(await screen.findByText('Withdraw your filing authorization?')).toBeTruthy();
    await user.press(screen.getByRole('button', { name: 'Withdraw and destroy logins' }));

    expect(
      await screen.findByText('Withdrawn. Your stored court login was destroyed.'),
    ).toBeTruthy();
    const del = fetchMock.mock.calls.find(([, init]) => init?.method === 'DELETE');
    expect(del?.[0]).toContain('/v1/me/filing-authorization');
  });

  it('view_only can read and withdraw but is not offered signing', async () => {
    render({ status: UNSIGNED, signedInSecondsAgo: 10, level: 'view_only' });

    expect(await screen.findByText('I allow Insolvia to file under my login.')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Sign authorization' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Sign in again to sign' })).toBeNull();
  });
});
