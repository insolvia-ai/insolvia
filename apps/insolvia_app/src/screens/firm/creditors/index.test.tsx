import { screen, userEvent, waitFor } from '@testing-library/react-native';
import { renderRouter } from 'expo-router/testing-library';

import type { AuthConfig } from '@/config/environment';
import { writeRefreshToken } from '@/session';
import {
  installFakeBrowser,
  jsonResponse,
  TEST_AUTH_CONFIG,
  tokenEndpointResponse,
} from '@/session/testing';
import type { FakeBrowser } from '@/session/testing';

let mockAuthConfig: AuthConfig | null = null;

jest.mock('@/config/environment', () => ({
  ...jest.requireActual('@/config/environment'),
  resolveAuthConfig: () => mockAuthConfig,
}));

const FIRM_ID = '00000000-0000-4000-8000-00000000f18a';
const ALICE = '00000000-0000-4000-8000-00000000a11c';
const CREDITOR_ID = '00000000-0000-4000-8000-0000000000e2';

const ADD_EDIT_PERMISSIONS = {
  cases: 'add_edit',
  intake: 'add_edit',
  documents: 'add_edit',
  extraction_review: 'add_edit',
  creditor_library: 'add_edit',
  firm_administration: 'hidden',
};

function membership(overrides: Record<string, unknown> = {}) {
  return {
    subject: ALICE,
    username: null,
    clientId: 'exampleappclientid000000',
    scopes: [],
    expiresAt: null,
    firm: {
      id: FIRM_ID,
      name: 'Example & Partners',
      role: 'attorney',
      firstName: 'Alice',
      lastName: 'Attorney',
      displayName: 'Alice Attorney',
      isAdmin: false,
      accessAllCases: false,
      permissions: ADD_EDIT_PERMISSIONS,
      ...overrides,
    },
  };
}

const LIBRARY_CREDITOR = {
  id: CREDITOR_ID,
  name: 'Acme Collections',
  address: { line1: '1 Main St', city: 'Springfield', state: 'IL', postal_code: '62701' },
  additional_notice_parties: [],
  preferred: false,
  created_at: '2026-01-01T00:00:00.000Z',
  updated_at: '2026-01-01T00:00:00.000Z',
};

/** One stubbed API route: a method, a URL fragment, and its answer. */
interface Route {
  readonly method: string;
  readonly fragment: string;
  readonly respond: () => Response;
}

/**
 * `/firm/creditors` — the firm's reusable creditor library manager (issue
 * 13.9 / #350).
 *
 * Rendered through the real router, so a route file that moved or stopped
 * compiling fails here, the same argument `screens/firm/index.test.tsx`
 * makes for `/firm`. Routes are matched by METHOD as well as fragment (the
 * same shape `screens/intake/collection-editor.test.tsx` uses), because GET,
 * POST and DELETE on `/v1/firm/creditors[/…]` all need different answers in
 * one test.
 */
describe('the creditor library screen', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  function signedIn(routes: readonly Route[]) {
    const fetchMock = jest.fn((url: string, init?: RequestInit) => {
      if (url.includes('/oauth2/token')) return Promise.resolve(tokenEndpointResponse());
      const method = init?.method ?? 'GET';
      const match = routes.find((route) => route.method === method && url.includes(route.fragment));
      if (match === undefined) {
        return Promise.reject(new Error(`unexpected ${method} ${url}`));
      }
      return Promise.resolve(match.respond());
    });
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    renderRouter('src/app', { initialUrl: '/firm/creditors' });
    return fetchMock;
  }

  const me: Route = {
    method: 'GET',
    fragment: '/v1/me',
    respond: () => jsonResponse(200, membership()),
  };

  beforeEach(() => {
    mockAuthConfig = TEST_AUTH_CONFIG;
    browser = installFakeBrowser();
    writeRefreshToken('stored-refresh-token');
  });

  afterEach(() => {
    globalThis.fetch = realFetch;
    browser.restore();
  });

  it('lists the firm’s library under the heading', async () => {
    signedIn([
      me,
      {
        method: 'GET',
        fragment: '/v1/firm/creditors',
        respond: () => jsonResponse(200, { creditors: [LIBRARY_CREDITOR] }),
      },
    ]);

    expect(await screen.findByText('Acme Collections — Springfield — IL')).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Your firm’s creditor library' })).toBeTruthy();
  });

  it('hides the add/edit/remove controls at view_only', async () => {
    signedIn([
      {
        method: 'GET',
        fragment: '/v1/me',
        respond: () =>
          jsonResponse(
            200,
            membership({ permissions: { ...ADD_EDIT_PERMISSIONS, creditor_library: 'view_only' } }),
          ),
      },
      {
        method: 'GET',
        fragment: '/v1/firm/creditors',
        respond: () => jsonResponse(200, { creditors: [LIBRARY_CREDITOR] }),
      },
    ]);

    expect(await screen.findByText('Acme Collections — Springfield — IL')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Add creditor' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Edit Acme Collections' })).toBeNull();
  });

  it('tells somebody with no access what to do, without ever listing the library', async () => {
    const fetchMock = signedIn([
      {
        method: 'GET',
        fragment: '/v1/me',
        respond: () =>
          jsonResponse(
            200,
            membership({ permissions: { ...ADD_EDIT_PERMISSIONS, creditor_library: 'hidden' } }),
          ),
      },
    ]);

    expect(
      await screen.findByText(
        'Your firm has not given you access to the creditor library. Ask one of your firm’s administrators if you need it.',
      ),
    ).toBeTruthy();
    expect(fetchMock.mock.calls.some(([url]) => String(url).includes('/v1/firm/creditors'))).toBe(
      false,
    );
  });

  it('adds a creditor and returns to the list', async () => {
    const fetchMock = signedIn([
      me,
      {
        method: 'GET',
        fragment: '/v1/firm/creditors',
        respond: () => jsonResponse(200, { creditors: [] }),
      },
      {
        method: 'POST',
        fragment: '/v1/firm/creditors',
        respond: () => jsonResponse(201, LIBRARY_CREDITOR),
      },
    ]);

    const user = userEvent.setup();
    await user.press(await screen.findByRole('button', { name: 'Add creditor' }));
    await user.type(await screen.findByLabelText('Creditor name'), 'Acme Collections');
    await user.press(screen.getByRole('button', { name: 'Save creditor' }));

    await waitFor(() => {
      const post = fetchMock.mock.calls.find(
        ([url, init]) =>
          (init as RequestInit | undefined)?.method === 'POST' &&
          String(url).includes('/v1/firm/creditors'),
      );
      expect(post).toBeDefined();
      expect(JSON.parse(String((post?.[1] as RequestInit).body))).toEqual({
        name: 'Acme Collections',
        preferred: false,
      });
    });
    // Back on the list, showing what the server stored.
    expect(await screen.findByText('Acme Collections — Springfield — IL')).toBeTruthy();
  });

  it('removes a creditor from the list', async () => {
    const fetchMock = signedIn([
      me,
      {
        method: 'GET',
        fragment: '/v1/firm/creditors',
        respond: () => jsonResponse(200, { creditors: [LIBRARY_CREDITOR] }),
      },
      {
        method: 'DELETE',
        fragment: `/v1/firm/creditors/${CREDITOR_ID}`,
        respond: () => ({ ok: true, status: 204, text: () => Promise.resolve('') }) as Response,
      },
    ]);

    const user = userEvent.setup();
    await user.press(await screen.findByRole('button', { name: 'Remove Acme Collections' }));

    await waitFor(() =>
      expect(
        fetchMock.mock.calls.some(
          ([url, init]) =>
            (init as RequestInit | undefined)?.method === 'DELETE' &&
            String(url).includes(`/v1/firm/creditors/${CREDITOR_ID}`),
        ),
      ).toBe(true),
    );
  });
});
