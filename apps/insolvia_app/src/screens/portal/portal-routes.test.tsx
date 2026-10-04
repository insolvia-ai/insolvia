import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';

import { screen, userEvent, waitFor } from '@testing-library/react-native';
import { renderRouter } from 'expo-router/testing-library';

import type { AuthConfig } from '@/config/environment';
import { writePortalPendingAuthorization } from '@/session';
import {
  fakeJwt,
  installFakeBrowser,
  jsonResponse,
  routeFetch,
  TEST_ORIGIN,
  tokenEndpointResponse,
} from '@/session/testing';
import type { FakeBrowser } from '@/session/testing';

/**
 * The client portal's routes (ADR 0023 PR 2), mounted through the real router.
 *
 * What is held down here: the paths infra pins; that the portal signs in
 * through the PORTAL client and lands on `/portal`; that the landing screen
 * shows the firm's name and the case's chapter and stage; that a 403 is its
 * own, explained state; and that no portal screen draws the staff frame.
 */

const PORTAL_CONFIG: AuthConfig = {
  domain: 'https://insolvia-test.auth.example.test',
  clientId: 'test-portal-client-id-000',
};
let mockPortalConfig: AuthConfig | null = PORTAL_CONFIG;

jest.mock('@/config/environment', () => ({
  ...jest.requireActual('@/config/environment'),
  resolvePortalAuthConfig: () => mockPortalConfig,
}));

const APP_DIR = path.join(__dirname, '..', '..', 'app');

const PORTAL_ME = {
  subject: '00000000-0000-4000-8000-0000000000a1',
  displayName: 'Pat Example',
  roles: ['debtor_1'],
  firm: { name: 'Example & Partners' },
  case: { chapter: 13, stage: 'intake' },
};

// Shaped as core/questionnaire.py::portal_questionnaire_json answers for a
// firm that switched Expenses off and wrote its own Debts instructions.
const PORTAL_QUESTIONNAIRE = {
  sections: [
    {
      id: 'personal_information',
      title: 'Personal information',
      instructions: 'Tell us who you are and where you live.',
    },
    {
      id: 'debts',
      title: 'Debts',
      instructions: 'Bring your last statement from each lender.',
    },
  ],
};

describe('the portal routes', () => {
  it('are declared at exactly the paths infra registers', () => {
    // `portal_callback_urls` and `portal_logout_urls` in
    // infra/modules/auth/main.tf: <origin>/portal/auth/callback and
    // <origin>/portal. Under file-based routing the path IS the file.
    expect(existsSync(path.join(APP_DIR, 'portal', 'auth', 'callback.tsx'))).toBe(true);
    expect(existsSync(path.join(APP_DIR, 'portal', 'index.tsx'))).toBe(true);
    const infra = readFileSync(
      path.join(__dirname, '..', '..', '..', '..', '..', 'infra', 'modules', 'auth', 'main.tf'),
      'utf8',
    );
    expect(infra).toContain('"${o}/portal/auth/callback"');
    expect(infra).toContain('"${o}/portal"');
  });

  it('never reach for the staff session, API hook or frame', () => {
    // The structural half of "a second SessionProvider": every file under the
    // portal's routes and screens reads the PORTAL session only.
    const files = [
      ...['_layout.tsx', 'index.tsx', 'sign-in.tsx', path.join('auth', 'callback.tsx')].map((f) =>
        path.join(APP_DIR, 'portal', f),
      ),
      ...['home.tsx', 'sign-in.tsx', 'auth-callback.tsx', 'questionnaire-overview.tsx'].map((f) =>
        path.join(__dirname, f),
      ),
    ];
    for (const file of files) {
      const source = readFileSync(file, 'utf8');
      // Imports and calls, not prose: the files' comments name what they avoid.
      expect(source).not.toMatch(/\buseSession\(/);
      expect(source).not.toMatch(/\buseApi\(/);
      expect(source).not.toMatch(/from '@\/components\/app-shell'/);
      expect(source).not.toMatch(/from '@\/components\/require-session'/);
      expect(source).not.toMatch(/from '@\/api\/me'/);
    }
  });
});

describe('signing in to the portal', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  beforeEach(() => {
    mockPortalConfig = PORTAL_CONFIG;
    browser = installFakeBrowser();
  });

  afterEach(() => {
    browser.restore();
    globalThis.fetch = realFetch;
  });

  it('sends a signed-out visitor to the portal sign-in, not the staff one', async () => {
    const router = renderRouter('src/app', { initialUrl: '/portal' });

    expect(
      await screen.findByRole('heading', { name: 'Sign in to your client portal' }),
    ).toBeTruthy();
    expect(router.getPathname()).toBe('/portal/sign-in');
    // The portal frame, not the firm's: no rail, no Cases link.
    expect(screen.getByText('Client portal')).toBeTruthy();
    expect(screen.queryByRole('link', { name: 'Cases' })).toBeNull();
  });

  it('leaves for the managed login through the portal client', async () => {
    renderRouter('src/app', { initialUrl: '/portal/sign-in' });

    await userEvent.press(await screen.findByRole('button', { name: 'Sign in' }));

    await waitFor(() => {
      expect(browser.navigations.at(-1) ?? '').toContain('/oauth2/authorize?');
    });
    const authorize = new URL(browser.navigations.at(-1) ?? '');
    expect(authorize.searchParams.get('client_id')).toBe(PORTAL_CONFIG.clientId);
    expect(authorize.searchParams.get('redirect_uri')).toBe(`${TEST_ORIGIN}/portal/auth/callback`);
  });

  it('says so when this build has no portal client', async () => {
    mockPortalConfig = null;

    renderRouter('src/app', { initialUrl: '/portal/sign-in' });

    expect(await screen.findByRole('heading', { name: 'Sign-in is not configured' })).toBeTruthy();
  });

  it('completes the callback and lands on the firm name and the case stage', async () => {
    writePortalPendingAuthorization({
      state: 'test-state',
      codeVerifier: 'test-code-verifier',
      returnTo: null,
    });
    const fetchMock = jest.fn(
      routeFetch({
        '/oauth2/token': () =>
          tokenEndpointResponse({ idToken: fakeJwt({ email: 'client@example.test' }) }),
        '/v1/portal/me': () => jsonResponse(200, PORTAL_ME),
        '/v1/portal/questionnaire': () => jsonResponse(200, PORTAL_QUESTIONNAIRE),
      }),
    );
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    const router = renderRouter('src/app', {
      initialUrl: '/portal/auth/callback?code=test-code&state=test-state',
    });

    expect(await screen.findByRole('heading', { name: 'Welcome, Pat Example' })).toBeTruthy();
    expect(router.getPathname()).toBe('/portal');
    expect(screen.getAllByText('Example & Partners').length).toBeGreaterThan(0);
    expect(screen.getByText('Chapter 13')).toBeTruthy();
    expect(screen.getByText('Preparing your case')).toBeTruthy();
    // The questionnaire's sections, as the firm configured them: what the
    // server sent and nothing else — a section switched off never arrives.
    expect(await screen.findByRole('heading', { name: 'Your questionnaire' })).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Debts' })).toBeTruthy();
    expect(screen.getByText('Bring your last statement from each lender.')).toBeTruthy();
    expect(screen.queryByRole('heading', { name: 'Expenses' })).toBeNull();
    // The portal token went to the portal route, and to nothing staff-side.
    const urls = fetchMock.mock.calls.map((call) => String(call[0]));
    expect(urls.some((url) => url.endsWith('/v1/portal/me'))).toBe(true);
    expect(urls.some((url) => url.endsWith('/v1/me'))).toBe(false);
    // Memory-only: nothing written to localStorage by the whole round trip.
    expect([...browser.localStorage.entries.keys()]).toEqual([]);
  });

  it('explains a signed-in person with no live case, and offers the way out', async () => {
    writePortalPendingAuthorization({
      state: 'test-state',
      codeVerifier: 'test-code-verifier',
      returnTo: null,
    });
    globalThis.fetch = jest.fn(
      routeFetch({
        '/oauth2/token': () => tokenEndpointResponse(),
        '/v1/portal/me': () =>
          jsonResponse(403, {
            error: 'Forbidden',
            message: 'you do not have access to a case through the client portal',
          }),
      }),
    ) as unknown as typeof fetch;

    renderRouter('src/app', {
      initialUrl: '/portal/auth/callback?code=test-code&state=test-state',
    });

    const heading = await screen.findByRole('heading', {
      name: 'No case is linked to this sign-in',
    });
    expect(heading).toBeTruthy();
    expect(screen.getByText(/contact your law firm/).props['aria-live']).toBe('assertive');
    await userEvent.press(screen.getAllByRole('button', { name: 'Sign out' }).at(-1)!);
    await waitFor(() => {
      expect(browser.navigations.at(-1) ?? '').toContain('/logout?');
    });
    expect(new URL(browser.navigations.at(-1) ?? '').searchParams.get('logout_uri')).toBe(
      `${TEST_ORIGIN}/portal`,
    );
  });
});
