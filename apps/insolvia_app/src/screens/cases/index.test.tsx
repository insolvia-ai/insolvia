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

const CASE = {
  id: '00000000-0000-4000-8000-0000000000c1',
  chapter: 7,
  district: 'NDCA',
  status: 'intake',
  createdAt: '2026-08-04T10:00:00.000000Z',
  updatedAt: '2026-08-04T10:00:00.000000Z',
};

/**
 * `/cases` — the screen that closes issue 8.3's loop.
 *
 * Rendered through the **real router**, so a route file that moved or stopped
 * compiling fails here. `/cases` is protected, so every test signs in first,
 * exactly as the home screen's suite does.
 *
 * What is asserted is mostly the wiring the server cannot check for us: that
 * the form sends what the API expects, that the server's per-field messages
 * reach the field they belong to, and that the list is announced rather than
 * silently swapped in.
 */
describe('the cases screen', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  /**
   * Signs in and renders `/cases`, returning the fetch mock typed the way the
   * tests read it. `routeFetch` declares a single `url` parameter because that
   * is all it dispatches on, but the calls carry an init object too — and the
   * request body is exactly what one of these tests needs to assert.
   */
  function signedIn(handlers: Readonly<Record<string, () => Response>>) {
    const route = routeFetch({ '/oauth2/token': tokenEndpointResponse, ...handlers });
    const fetchMock = jest.fn((url: string, _init?: RequestInit) => route(url));
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    renderRouter('src/app', { initialUrl: '/cases' });
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

  it('lists the cases the API returns', async () => {
    signedIn({ '/v1/cases': () => jsonResponse(200, { cases: [CASE] }) });

    expect(await screen.findByText(/Chapter 7 · NDCA/)).toBeTruthy();
  });

  it('says so plainly when there are none', async () => {
    signedIn({ '/v1/cases': () => jsonResponse(200, { cases: [] }) });

    expect(await screen.findByText(/No cases yet/)).toBeTruthy();
  });

  it('sends the chosen chapter and district to the API', async () => {
    const fetchMock = signedIn({
      '/v1/cases': () => jsonResponse(200, { cases: [] }),
    });
    await screen.findByText(/No cases yet/);

    await userEvent.press(screen.getByRole('radio', { name: /Chapter 13/ }));
    await userEvent.type(screen.getByLabelText('Filing district'), 'CACD');
    await userEvent.press(screen.getByRole('button', { name: 'Open case' }));

    await waitFor(() => {
      // Filtered by URL as well as method: the OAuth token exchange is also a
      // POST, and it is the one that happens first.
      const post = fetchMock.mock.calls.find(
        ([url, init]) => url.includes('/v1/cases') && init?.method === 'POST',
      );
      expect(post).toBeDefined();
      expect(JSON.parse(String(post?.[1]?.body))).toEqual({ chapter: 13, district: 'CACD' });
    });
  });

  it("puts the server's per-field message on the field it belongs to", async () => {
    // The server is the source of truth for validation (ADR 0001). The screen
    // renders what it said rather than restating the rule in a second place
    // that can disagree with it.
    let calls = 0;
    signedIn({
      '/v1/cases': () => {
        calls += 1;
        return calls === 1
          ? jsonResponse(200, { cases: [] })
          : jsonResponse(400, {
              error: 'ValidationError',
              fields: { district: "That doesn't look like a district identifier." },
            });
      },
    });
    await screen.findByText(/No cases yet/);

    await userEvent.type(screen.getByLabelText('Filing district'), '!!');
    await userEvent.press(screen.getByRole('button', { name: 'Open case' }));

    expect(await screen.findByText(/doesn't look like a district identifier/)).toBeTruthy();
  });

  it('reloads the list after opening a case', async () => {
    let listCalls = 0;
    signedIn({
      '/v1/cases': () => {
        // POST answers 201; the two GETs bracket it.
        listCalls += 1;
        if (listCalls === 1) return jsonResponse(200, { cases: [] });
        if (listCalls === 2) return jsonResponse(201, CASE);
        return jsonResponse(200, { cases: [CASE] });
      },
    });
    await screen.findByText(/No cases yet/);

    await userEvent.type(screen.getByLabelText('Filing district'), 'NDCA');
    await userEvent.press(screen.getByRole('button', { name: 'Open case' }));

    expect(await screen.findByText(/Chapter 7 · NDCA/)).toBeTruthy();
  });

  it('reports a failed load without pretending the list is empty', async () => {
    // "No cases yet" on a failed request would tell a firm its cases are gone.
    signedIn({ '/v1/cases': () => jsonResponse(500, { error: 'InternalError' }) });

    const message = await screen.findByText('Could not load your cases.');
    expect(message.props['aria-live']).toBe('assertive');
    expect(screen.queryByText(/No cases yet/)).toBeNull();
  });

  it('gives the page exactly one level-1 heading', async () => {
    signedIn({ '/v1/cases': () => jsonResponse(200, { cases: [] }) });
    await screen.findByText(/No cases yet/);

    const headings = screen.getAllByRole('heading');
    const levelOnes = headings.filter((node) => node.props['aria-level'] === 1);
    expect(levelOnes).toHaveLength(1);
  });
});
