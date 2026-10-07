import { screen, userEvent, waitFor } from '@testing-library/react-native';
import { renderRouter } from 'expo-router/testing-library';

import type { AuthConfig } from '@/config/environment';
import { writeRefreshToken } from '@/session';
import {
  installFakeBrowser,
  jsonResponse,
  routeFetch,
  TEST_AUTH_CONFIG,
  tokenEndpointResponse,
} from '@/session/testing';
import type { FakeBrowser } from '@/session/testing';

let mockAuthConfig: AuthConfig | null = null;

jest.mock('@/config/environment', () => ({
  ...jest.requireActual('@/config/environment'),
  resolveAuthConfig: () => mockAuthConfig,
}));

const ALICE = '00000000-0000-4000-8000-00000000a11c';
// Fake values only (ADR 0024 PR 4): nothing here is anybody's credential.
const LOGIN = 'FAKE-ECF-USER';
const PASSWORD = 'FAKE-ECF-PASSWORD';
const SEED = 'FAKEFAKEFAKEFAKE';
const CREDENTIAL = {
  id: 'c0ffee00-0000-4000-8000-000000000001',
  login: LOGIN,
  courts: [],
  status: 'active',
  created_at: '2026-07-23T09:15:00.123Z',
  updated_at: '2026-07-23T09:15:00.123Z',
};

function membership(electronicFiling: 'hidden' | 'view_only' | 'add_edit') {
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
 * The court filing login on `/account` (ADR 0024, guardrail 3): shown only
 * with `electronic_filing`, the secret sent once and cleared, never shown
 * back; revoke removes it.
 */
describe('the court filing login', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  function signedIn(
    level: 'hidden' | 'view_only' | 'add_edit',
    credentials: readonly unknown[],
    write?: (url: string, init: RequestInit) => Response,
  ) {
    const route = routeFetch({
      '/oauth2/token': tokenEndpointResponse,
      '/v1/me/filing-credentials': () => jsonResponse(200, { credentials }),
      '/v1/me': () => jsonResponse(200, membership(level)),
    });
    const fetchMock = jest.fn((url: string, init?: RequestInit) =>
      write !== undefined &&
      url.includes('/v1/me/filing-credentials') &&
      init?.method !== undefined &&
      init.method !== 'GET'
        ? Promise.resolve(write(url, init))
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

  it('is absent without the feature', async () => {
    signedIn('hidden', []);
    await screen.findByDisplayValue('Alice');
    expect(screen.queryByText('Court filing login')).toBeNull();
  });

  it('view_only lists the login and offers neither the form nor revoke', async () => {
    signedIn('view_only', [CREDENTIAL]);
    expect(await screen.findByText(LOGIN)).toBeTruthy();
    expect(screen.queryByLabelText('PACER password')).toBeNull();
    expect(screen.queryByRole('button', { name: `Revoke ${LOGIN}` })).toBeNull();
  });

  it('enrols once, sends exactly the three fields, and clears the secret', async () => {
    const fetchMock = signedIn('add_edit', [], () => jsonResponse(201, CREDENTIAL));
    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('PACER username'), LOGIN);
    await user.type(screen.getByLabelText('PACER password'), PASSWORD);
    await user.type(screen.getByLabelText('Authenticator key'), SEED);
    await user.press(screen.getByRole('button', { name: 'Save filing login' }));

    expect(await screen.findByText('Your filing login is sealed and stored.')).toBeTruthy();
    const post = fetchMock.mock.calls.find(
      ([url, init]) => init?.method === 'POST' && url.includes('/v1/me/filing-credentials'),
    );
    expect(post?.[0]).toContain('/v1/me/filing-credentials');
    expect(JSON.parse(post?.[1]?.body as string)).toEqual({
      login: LOGIN,
      password: PASSWORD,
      totp_seed: SEED,
    });
    // The secret's last moment on the device was the request.
    expect(screen.queryByDisplayValue(PASSWORD)).toBeNull();
    expect(screen.queryByDisplayValue(SEED)).toBeNull();
    expect(screen.getByText(LOGIN)).toBeTruthy();
  });

  it('revoke removes it from the list', async () => {
    const fetchMock = signedIn(
      'add_edit',
      [CREDENTIAL],
      () =>
        ({
          ok: true,
          status: 204,
          text: () => Promise.resolve(''),
        }) as unknown as Response,
    );
    const user = userEvent.setup();
    await user.press(await screen.findByRole('button', { name: `Revoke ${LOGIN}` }));

    expect(await screen.findByText(/is revoked/)).toBeTruthy();
    await waitFor(() =>
      expect(screen.queryByRole('button', { name: `Revoke ${LOGIN}` })).toBeNull(),
    );
    const del = fetchMock.mock.calls.find(([, init]) => init?.method === 'DELETE');
    expect(del?.[0]).toContain(`/v1/me/filing-credentials/${CREDENTIAL.id}`);
  });
});
