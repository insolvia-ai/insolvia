import { screen, userEvent } from '@testing-library/react-native';
import { renderRouter } from 'expo-router/testing-library';

import type { AuthConfig } from '@/config/environment';
import { writeRefreshToken } from '@/session';
import {
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

function me(firmAdministration: string | null) {
  return {
    subject: ALICE,
    username: null,
    clientId: 'exampleappclientid000000',
    scopes: [],
    expiresAt: null,
    ...(firmAdministration === null
      ? {}
      : {
          firm: {
            id: '00000000-0000-4000-8000-00000000f18a',
            name: 'Example & Partners',
            role: 'attorney',
            firstName: 'Alice',
            lastName: 'Attorney',
            displayName: 'Alice Attorney',
            isAdmin: false,
            accessAllCases: false,
            permissions: {
              cases: 'view_only',
              intake: 'view_only',
              documents: 'view_only',
              extraction_review: 'hidden',
              firm_administration: firmAdministration,
            },
          },
        }),
  };
}

/**
 * The shell's navigation — specifically the Firm entry (issue #218), which
 * shows when the caller's `firm_administration` permits at least `view_only`.
 *
 * The gating is a courtesy: the value comes from `MembershipProvider`'s
 * session-lifetime `/v1/me`, and the `/firm` screen's own fallback panel plus
 * the API remain the real answer for anyone who reaches it anyway.
 */
describe('the shell navigation', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  function signedIn(handlers: Readonly<Record<string, () => Response>>) {
    const route = routeFetch({ '/oauth2/token': tokenEndpointResponse, ...handlers });
    const fetchMock = jest.fn((url: string, _init?: RequestInit) => route(url));
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    renderRouter('src/app', { initialUrl: '/' });
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

  it('shows the Firm entry to somebody who may administer, and it navigates', async () => {
    signedIn({
      // MORE SPECIFIC FIRST — routeFetch matches by substring in insertion
      // order, so `/v1/firm` any earlier would swallow `/v1/firm/users`.
      '/v1/firm/users': () => jsonResponse(200, { users: [] }),
      '/v1/me': () => jsonResponse(200, me('view_only')),
      '/v1/firm': () =>
        jsonResponse(200, {
          id: '00000000-0000-4000-8000-00000000f18a',
          name: 'Example & Partners',
          status: 'active',
          createdAt: '2026-01-05T09:00:00.000Z',
          updatedAt: '2026-08-01T12:00:00.000Z',
        }),
    });

    await userEvent.setup().press(await screen.findByRole('link', { name: 'Firm' }));

    expect(await screen.findByRole('heading', { name: 'Example & Partners' })).toBeTruthy();
  });

  it('keeps the entry from somebody whose firm has not granted administration', async () => {
    signedIn({ '/v1/me': () => jsonResponse(200, me('hidden')) });
    // Settle: MePanel's rows mean its /v1/me round trip resolved, and the
    // provider's parallel one — same handler, same microtask queue — with it.
    await screen.findByText(TEST_EMAIL);
    await screen.findByText(/Cognito subject/);

    expect(screen.getByRole('link', { name: 'Home' })).toBeTruthy();
    expect(screen.queryByRole('link', { name: 'Firm' })).toBeNull();
  });

  it('shows no entry to somebody in no firm at all', async () => {
    signedIn({ '/v1/me': () => jsonResponse(200, me(null)) });
    await screen.findByText(TEST_EMAIL);
    await screen.findByText(/Cognito subject/);

    expect(screen.queryByRole('link', { name: 'Firm' })).toBeNull();
  });
});
