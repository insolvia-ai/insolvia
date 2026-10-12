import { screen, userEvent, waitFor } from '@testing-library/react-native';
import { renderRouter } from 'expo-router/testing-library';

import type { AuthConfig } from '@/config/environment';
import { writeRefreshToken } from '@/session';
import { installFakeBrowser, jsonResponse, TEST_AUTH_CONFIG } from '@/session/testing';
import type { FakeBrowser } from '@/session/testing';

import { caseBody, clientFetch, firmClient, member } from './testing';
import type { Respond } from './testing';

let mockAuthConfig: AuthConfig | null = null;

jest.mock('@/config/environment', () => ({
  ...jest.requireActual('@/config/environment'),
  resolveAuthConfig: () => mockAuthConfig,
}));

const JORDAN = firmClient('00000000-0000-4000-8000-0000000c11a0', 'Jordan', 'Example', {
  residence_address: { city: 'Tampa', state: 'FL' },
  first_retained_at: '2026-07-20',
});
const RILEY = firmClient('00000000-0000-4000-8000-0000000c11a1', 'Riley', 'Example', {
  first_retained_at: '2026-07-20',
});
// A prospect (issue #355): never retained, in the funnel.
const SAM = firmClient('00000000-0000-4000-8000-0000000c11a2', 'Sam', 'Sample', {
  residence_address: { city: 'Orlando', state: 'FL' },
  prospect_stage: 'consultation_scheduled',
});
const CASEY = firmClient('00000000-0000-4000-8000-0000000c11a3', 'Casey', 'Gone', {
  status: 'archived',
});
const DIRECTORY = [JORDAN, RILEY, SAM, CASEY];

/** The joint case: Jordan is Debtor 1 and Riley Debtor 2 of the same matter. */
const JOINT = caseBody('00000000-0000-4000-8000-0000000000c1', { chapter: 13 });
const SOLO = caseBody('00000000-0000-4000-8000-0000000000c2', { status: 'filed' });

const CLIENT_CASES: Record<string, unknown[]> = {
  [JORDAN.id]: [
    { filing_role: 'debtor_1', case: JOINT },
    { filing_role: 'debtor_1', case: SOLO },
  ],
  [RILEY.id]: [{ filing_role: 'debtor_2', case: JOINT }],
  [SAM.id]: [],
  [CASEY.id]: [],
};

/** The directory, and each client's reachable cases, by URL. */
const directory =
  (clients: readonly unknown[] = DIRECTORY): Respond =>
  (url) => {
    const cases = /\/v1\/firm\/clients\/([^/]+)\/cases/.exec(url);
    if (cases !== null) return jsonResponse(200, { cases: CLIENT_CASES[cases[1] ?? ''] ?? [] });
    if (url.endsWith('/v1/firm/clients')) return jsonResponse(200, { clients });
    return undefined;
  };

/**
 * `/clients` — the firm's client directory, the front door (ADR 0022 /
 * #354). Through the real router, signed in.
 *
 * The claims the ADR makes about this screen are the ones asserted: one row
 * per client, a joint case on BOTH clients' rows, only reachable cases and
 * never a count of the rest, and the `clients` gate at each of its levels.
 */
describe('the client list', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  function signedIn(respond: Respond = directory(), me: unknown = member()) {
    const fetchMock = clientFetch(respond, me);
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    const router = renderRouter('src/app', { initialUrl: '/clients' });
    return { fetchMock, router };
  }

  const clientLink = (name: string) => screen.findByRole('link', { name });

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

  it('shows one row per active client, named surname first, linking to the record', async () => {
    signedIn();

    const jordan = await clientLink('Example, Jordan');
    expect(jordan.props.href).toBe(`/clients/${JORDAN.id}`);
    expect(screen.getByRole('link', { name: 'Example, Riley' })).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Sample, Sam' })).toBeTruthy();
    // Archived clients leave the default list.
    expect(screen.queryByRole('link', { name: 'Gone, Casey' })).toBeNull();
    expect(screen.getByText('3 clients')).toBeTruthy();
  });

  it('switches to archived clients, and to everyone', async () => {
    signedIn();
    await clientLink('Example, Jordan');
    const user = userEvent.setup();

    await user.press(screen.getByRole('button', { name: 'Archived' }));
    expect(await clientLink('Gone, Casey')).toBeTruthy();
    expect(screen.queryByRole('link', { name: 'Example, Jordan' })).toBeNull();

    await user.press(screen.getByRole('button', { name: 'All' }));
    expect(await clientLink('Example, Jordan')).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Gone, Casey' })).toBeTruthy();
  });

  it('shows the prospects — clients not yet retained — with their funnel stage', async () => {
    signedIn();
    await clientLink('Example, Jordan');

    await userEvent.setup().press(screen.getByRole('button', { name: 'Prospects' }));

    expect(await clientLink('Sample, Sam')).toBeTruthy();
    expect(screen.queryByRole('link', { name: 'Example, Jordan' })).toBeNull();
    expect(screen.getByText('Consultation scheduled')).toBeTruthy();
  });

  it('finds a client by any part of their name, or their city', async () => {
    signedIn();
    await clientLink('Example, Jordan');
    const user = userEvent.setup();
    const search = screen.getByLabelText('Search clients');

    await user.type(search, 'jor exa');
    await waitFor(() => {
      expect(screen.queryByRole('link', { name: 'Example, Riley' })).toBeNull();
    });
    expect(screen.getByRole('link', { name: 'Example, Jordan' })).toBeTruthy();

    await user.clear(search);
    await user.type(search, 'orlando');
    expect(await clientLink('Sample, Sam')).toBeTruthy();
    expect(screen.queryByRole('link', { name: 'Example, Jordan' })).toBeNull();

    await user.clear(search);
    await user.type(search, 'nobody');
    expect(await screen.findByText('No active clients match “nobody”.')).toBeTruthy();
  });

  it('puts a joint case on BOTH clients’ rows, each linking to the same case', async () => {
    signedIn();

    const name = `Chapter 13 case in Middle District of Florida, opened 2026-08-04`;
    await waitFor(() => {
      expect(screen.getAllByRole('link', { name })).toHaveLength(2);
    });
    for (const link of screen.getAllByRole('link', { name })) {
      expect(link.props.href).toBe(`/cases/${JOINT.id}`);
    }
    // Jordan's own second case is on Jordan's row only.
    expect(
      screen.getAllByRole('link', {
        name: 'Chapter 7 case in Middle District of Florida, opened 2026-08-04',
      }),
    ).toHaveLength(1);
  });

  it('says "No cases" for a client with none the caller can see — and never counts the rest', async () => {
    signedIn();
    expect(await screen.findByText('No cases')).toBeTruthy();
    // A count would be the enumeration ADR 0009's 404 hides.
    expect(screen.queryByText(/\d+ cases?\b/)).toBeNull();
  });

  it('draws no Cases column, and asks for no cases, without `cases` access', async () => {
    const { fetchMock } = signedIn(directory(), member({ cases: 'hidden' }));
    await clientLink('Example, Jordan');

    // Not the header text: the rail has a "Cases" link of its own.
    expect(screen.queryByText('No cases')).toBeNull();
    const urls = fetchMock.mock.calls.map(([url]) => String(url));
    expect(urls.some((url) => url.includes('/cases'))).toBe(false);
  });

  it('offers "Add client" to a colleague who may add, and it goes to the form', async () => {
    const { router } = signedIn();
    await clientLink('Example, Jordan');

    await userEvent.setup().press(screen.getByRole('button', { name: 'Add client' }));

    await waitFor(() => {
      expect(router.getPathname()).toBe('/clients/new');
    });
  });

  it('is read-only at `view_only`: the list, and no "Add client"', async () => {
    signedIn(directory(), member({ clients: 'view_only' }));
    await clientLink('Example, Jordan');

    expect(screen.queryByRole('button', { name: 'Add client' })).toBeNull();
  });

  it('explains itself to a colleague whose firm has not granted `clients`, and asks the API nothing', async () => {
    const { fetchMock } = signedIn(directory(), member({ clients: 'hidden' }));

    expect(await screen.findByText(/not given you access to its client list/)).toBeTruthy();
    const urls = fetchMock.mock.calls.map(([url]) => String(url));
    expect(urls.some((url) => url.includes('/v1/firm/clients'))).toBe(false);
  });

  it('says there are no clients yet in an empty directory', async () => {
    signedIn(directory([]));

    expect(await screen.findByRole('heading', { name: 'No clients yet' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Add client' })).toBeTruthy();
  });

  it('reports a failed load rather than an empty directory', async () => {
    signedIn((url) =>
      url.endsWith('/v1/firm/clients') ? jsonResponse(500, { error: 'InternalError' }) : undefined,
    );

    const message = await screen.findByText('Could not load your client list.');
    expect(message.props['aria-live']).toBe('assertive');
    expect(screen.queryByText('No clients yet')).toBeNull();
  });

  it('gives the page exactly one level-1 heading', async () => {
    signedIn();
    await clientLink('Example, Jordan');

    const levelOnes = screen
      .getAllByRole('heading')
      .filter((node) => node.props['aria-level'] === 1);
    expect(levelOnes).toHaveLength(1);
  });
});
