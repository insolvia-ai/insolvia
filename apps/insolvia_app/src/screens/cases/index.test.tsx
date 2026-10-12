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
  // Who OPENED the matter. A subject, not a name — the firm directory is what
  // resolves it, and a case list that rendered this raw would show a UUID.
  createdBy: '00000000-0000-4000-8000-00000000a11c',
  chapter: 7,
  // The printed name, derived on the server from the pair below (#360).
  district: 'Middle District of Florida',
  court: 'flmb',
  division: 'tampa',
  status: 'intake',
  createdAt: '2026-08-04T10:00:00.000000Z',
  updatedAt: '2026-08-04T10:00:00.000000Z',
};

/** A `/v1/me` body whose firm grants `cases` at the given level. */
function member(cases: string) {
  return {
    subject: CASE.createdBy,
    username: null,
    clientId: 'exampleappclientid000000',
    scopes: [],
    expiresAt: null,
    firm: {
      id: '00000000-0000-4000-8000-00000000f18a',
      name: 'Example & Partners',
      role: 'staff',
      firstName: 'Alice',
      lastName: 'Attorney',
      displayName: 'Alice Attorney',
      isAdmin: false,
      accessAllCases: true,
      permissions: { cases },
      defaultCourt: null,
      defaultDivision: null,
      defaultChapter: null,
      letterhead: null,
      signatureBlock: null,
    },
  };
}

/**
 * `/cases` — the case list (issue 8.3), and the way to `/cases/new`.
 *
 * Rendered through the **real router**, so a route file that moved or stopped
 * compiling fails here. `/cases` is protected, so every test signs in first,
 * exactly as the home screen's suite does.
 *
 * The form that opens a case moved to `/cases/new` (ADR 0022 / #354); its
 * suite is `new.test.tsx`. What is asserted here is that the list is
 * announced rather than silently swapped in, that each row is one well-named
 * link, and that "New case" goes where the form now lives.
 */
describe('the cases screen', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  function signedIn(handlers: Readonly<Record<string, () => Response>>) {
    const route = routeFetch({ '/oauth2/token': tokenEndpointResponse, ...handlers });
    const fetchMock = jest.fn((url: string, _init?: RequestInit) => route(url));
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    const router = renderRouter('src/app', { initialUrl: '/cases' });
    return { fetchMock, router };
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

  it('sends "New case" to the page that opens one for a client', async () => {
    const { router } = signedIn({ '/v1/cases': () => jsonResponse(200, { cases: [] }) });
    await screen.findByText(/No cases yet/);

    await userEvent.setup().press(screen.getByRole('button', { name: 'New case' }));

    await waitFor(() => {
      expect(router.getPathname()).toBe('/cases/new');
    });
  });

  it('offers no "New case" to a colleague who may only view cases', async () => {
    signedIn({
      '/v1/me': () => jsonResponse(200, member('view_only')),
      '/v1/cases': () => jsonResponse(200, { cases: [] }),
    });
    await screen.findByText(/No cases yet/);
    // Settled on the membership: the avatar's name only renders once /v1/me answered.
    await screen.findByText('Alice Attorney');

    expect(screen.queryByRole('button', { name: 'New case' })).toBeNull();
  });

  it('lists the cases the API returns', async () => {
    signedIn({ '/v1/cases': () => jsonResponse(200, { cases: [CASE] }) });

    expect(await screen.findByText(/Chapter 7 · Middle District of Florida/)).toBeTruthy();
  });

  it('switches to the archive, which asks the API for archived cases', async () => {
    // Fragment order matters: the archive's query first, the bare list after.
    const { fetchMock } = signedIn({
      '/v1/cases?archived=true': () => jsonResponse(200, { cases: [] }),
      '/v1/cases': () => jsonResponse(200, { cases: [CASE] }),
    });
    await screen.findByText(/Chapter 7 · Middle District of Florida/);

    await userEvent.setup().press(screen.getByRole('button', { name: 'Archived' }));

    expect(await screen.findByText(/No archived cases/)).toBeTruthy();
    expect(
      fetchMock.mock.calls.some(([url]) => String(url).includes('/v1/cases?archived=true')),
    ).toBe(true);
  });

  it('says so plainly when there are none', async () => {
    signedIn({ '/v1/cases': () => jsonResponse(200, { cases: [] }) });

    expect(await screen.findByText(/No cases yet/)).toBeTruthy();
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
  it('gives each case ONE link, to the case itself', async () => {
    // A row used to carry six — intake, team, documents, packet, matrix,
    // review — because `/cases/<id>` did not exist for it to point at. They now
    // live in the case's own rail, so the list is a list again.
    signedIn({ '/v1/cases': () => jsonResponse(200, { cases: [CASE] }) });
    // Waits on the row's own link rather than on the table: `Table`'s native
    // leaf asserts `table`/`row`/`cell` through a web-only prop that the native
    // testing renderer does not surface as a role, and the claim here is about
    // links anyway.
    await screen.findByText('Chapter 7 · Middle District of Florida');

    const links = screen.getAllByRole('link').filter((node) => {
      const href: unknown = node.props.href;
      return typeof href === 'string' && href.startsWith('/cases/');
    });
    expect(links).toHaveLength(1);
    expect(links[0]?.props.href).toBe(`/cases/${CASE.id}`);
  });

  it("names that link by the case, not by the word 'open'", async () => {
    // WCAG 2.4.4: a link has to make sense read out of its row. Nine rows of
    // "Open case" are nine links with one name between them.
    signedIn({ '/v1/cases': () => jsonResponse(200, { cases: [CASE] }) });

    const link = await screen.findByLabelText(
      `Chapter 7 case in Middle District of Florida, opened 2026-08-04 by ${CASE.createdBy}`,
    );
    // WCAG 2.5.3: the visible text is where the accessible name starts.
    expect(link.props.href).toBe(`/cases/${CASE.id}`);
  });
});
