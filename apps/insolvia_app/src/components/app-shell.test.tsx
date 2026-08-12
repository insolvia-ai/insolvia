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
    // SETTLE FIRST, or the absence below proves nothing — a Firm link missing
    // because `/v1/me` has not answered yet looks identical to one correctly
    // withheld. The avatar's initials are the signal: 'AA' can only come from
    // the membership's display name, so seeing them means the round trip
    // resolved AND a firm came back with it. (It used to be MePanel's claim
    // rows, which have moved off the home screen to /account.)
    await screen.findByText('AA');

    expect(screen.getByRole('link', { name: 'Home' })).toBeTruthy();
    expect(screen.queryByRole('link', { name: 'Firm' })).toBeNull();
  });

  it('shows no entry to somebody in no firm at all', async () => {
    const fetchMock = signedIn({ '/v1/me': () => jsonResponse(200, me(null)) });
    // No membership means no membership-derived UI to wait on — the avatar
    // falls back to the email's initials, which render before the request even
    // starts. So the settle is the request itself: `waitFor` retries across
    // microtasks, so the state update that follows the response has flushed by
    // the time the assertion below runs.
    await waitFor(() => {
      expect(fetchMock.mock.calls.filter(([url]) => String(url).includes('/v1/me'))).toHaveLength(
        1,
      );
    });
    await screen.findByRole('button', { name: 'Account menu' });

    expect(screen.queryByRole('link', { name: 'Firm' })).toBeNull();
  });
});
