import { screen, userEvent, waitFor, within } from '@testing-library/react-native';
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

/** A two-court slice of `GET /v1/courts`, in the API's wire shape. */
const COURTS = {
  releaseId: 'courts/us-bankruptcy@2026-09-24',
  effectiveDate: '2026-09-24',
  districts: [
    {
      code: 'flmb',
      courtId: 'FLMBK',
      name: 'Middle District of Florida',
      state: 'FL',
      circuit: 11,
      website: 'https://www.flmb.uscourts.gov/',
      divisions: [
        {
          code: 'tampa',
          name: 'Tampa Division',
          officeCode: '8',
          officeCodeVerified: true,
          courthouse: null,
          counties: [{ name: 'Hillsborough', fips: '12057' }],
        },
        {
          code: 'orlando',
          name: 'Orlando Division',
          officeCode: '6',
          officeCodeVerified: true,
          courthouse: null,
          counties: [{ name: 'Orange', fips: '12095' }],
        },
      ],
      caseUpload: { status: 'unverified', verifiedAt: null },
    },
    {
      code: 'txsb',
      courtId: 'TXSBK',
      name: 'Southern District of Texas',
      state: 'TX',
      circuit: 5,
      website: 'https://www.txs.uscourts.gov/',
      divisions: [
        {
          code: 'houston',
          name: 'Houston Division',
          officeCode: null,
          officeCodeVerified: false,
          courthouse: null,
          counties: [{ name: 'Harris', fips: '48201' }],
        },
      ],
      caseUpload: { status: 'unverified', verifiedAt: null },
    },
  ],
};

/** A `/v1/me` body whose firm has set its defaults (#360). */
function memberWithDefaults() {
  return {
    subject: CASE.createdBy,
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
      isAdmin: true,
      accessAllCases: true,
      permissions: { cases: 'add_edit' },
      defaultCourt: 'txsb',
      defaultDivision: 'houston',
      defaultChapter: 13,
      letterhead: null,
      signatureBlock: null,
    },
  };
}

/** Picks an option in one of the registry `Select`s by its accessible names. */
async function choose(control: string, option: string) {
  await userEvent.press(screen.getByRole('combobox', { name: control }));
  await userEvent.press(await screen.findByRole('option', { name: option }));
}

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
    // The registry LAST: a test's own `/v1/courts` (say, a failing one) wins,
    // and `/v1/cases` is matched by substring so it must not swallow it.
    const route = routeFetch({
      '/oauth2/token': tokenEndpointResponse,
      ...handlers,
      ...('/v1/courts' in handlers ? {} : { '/v1/courts': () => jsonResponse(200, COURTS) }),
    });
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

    expect(await screen.findByText(/Chapter 7 · Middle District of Florida/)).toBeTruthy();
  });

  it('says so plainly when there are none', async () => {
    signedIn({ '/v1/cases': () => jsonResponse(200, { cases: [] }) });

    expect(await screen.findByText(/No cases yet/)).toBeTruthy();
  });

  it('renders one named radio per chapter, with the label outside the circle', async () => {
    // The regression this guards: RadioGroup.Item IS the 20dp circle (its own
    // package tests render it self-closing), so a label nested inside it makes
    // four circles overlap into an unreadable pile. Asserting the accessible
    // NAME rather than the visible text is what catches it — a nested label
    // would still render, just on top of its neighbours.
    signedIn({ '/v1/cases': () => jsonResponse(200, { cases: [] }) });
    await screen.findByText(/No cases yet/);

    const radios = screen.getAllByRole('radio');
    expect(radios).toHaveLength(4);

    for (const label of ['Chapter 7', 'Chapter 13', 'Chapter 11', 'Chapter 12']) {
      const radio = screen.getByRole('radio', { name: label });
      // The discriminating assertion. Checking the accessible NAME alone would
      // pass either way — react-native-web derives it from nested content just
      // as happily as from aria-label. What only the correct structure
      // satisfies is the label being OUTSIDE the circle.
      expect(within(radio).queryByText(label)).toBeNull();
      expect(screen.getByText(label)).toBeTruthy();
    }
  });

  it('sends the chosen chapter, court and division to the API — never a district string', async () => {
    const fetchMock = signedIn({
      '/v1/cases': () => jsonResponse(200, { cases: [] }),
    });
    await screen.findByText(/No cases yet/);

    await userEvent.press(screen.getByRole('radio', { name: /Chapter 13/ }));
    await choose('Court', 'Middle District of Florida');
    await choose('Division', 'Orlando Division');
    await userEvent.press(screen.getByRole('button', { name: 'Open case' }));

    await waitFor(() => {
      // Filtered by URL as well as method: the OAuth token exchange is also a
      // POST, and it is the one that happens first.
      const post = fetchMock.mock.calls.find(
        ([url, init]) => url.includes('/v1/cases') && init?.method === 'POST',
      );
      expect(post).toBeDefined();
      expect(JSON.parse(String(post?.[1]?.body))).toEqual({
        chapter: 13,
        court: 'flmb',
        division: 'orlando',
      });
    });
  });

  it('offers only the chosen court’s divisions, and clears the division when the court changes', async () => {
    signedIn({ '/v1/cases': () => jsonResponse(200, { cases: [] }) });
    await screen.findByText(/No cases yet/);

    await choose('Court', 'Southern District of Texas');
    await userEvent.press(screen.getByRole('combobox', { name: 'Division' }));
    expect(await screen.findByRole('option', { name: 'Houston Division' })).toBeTruthy();
    expect(screen.queryByRole('option', { name: 'Tampa Division' })).toBeNull();
    await userEvent.press(screen.getByRole('option', { name: 'Houston Division' }));

    // A division belongs to its court: picking another court starts over.
    await choose('Court', 'Middle District of Florida');
    expect(screen.queryByText('Houston Division')).toBeNull();
  });

  it('preselects the firm’s default court, division and chapter', async () => {
    const fetchMock = signedIn({
      '/v1/me': () => jsonResponse(200, memberWithDefaults()),
      '/v1/cases': () => jsonResponse(200, { cases: [] }),
    });
    await screen.findByText(/No cases yet/);
    // The defaults have arrived once the trigger shows the division's name.
    await screen.findByText('Houston Division');

    await userEvent.press(screen.getByRole('button', { name: 'Open case' }));

    await waitFor(() => {
      const post = fetchMock.mock.calls.find(
        ([url, init]) => url.includes('/v1/cases') && init?.method === 'POST',
      );
      expect(JSON.parse(String(post?.[1]?.body))).toEqual({
        chapter: 13,
        court: 'txsb',
        division: 'houston',
      });
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
              fields: { court: 'Choose the bankruptcy court this case will be filed in.' },
            });
      },
    });
    await screen.findByText(/No cases yet/);

    await userEvent.press(screen.getByRole('button', { name: 'Open case' }));

    expect(await screen.findByText(/Choose the bankruptcy court/)).toBeTruthy();
  });

  it('cannot open a case while the court registry is unavailable', async () => {
    signedIn({
      '/v1/courts': () => jsonResponse(500, { error: 'InternalError' }),
      '/v1/cases': () => jsonResponse(200, { cases: [] }),
    });
    await screen.findByText(/No cases yet/);

    const message = await screen.findByText(/Could not load the court registry/);
    expect(message.props['aria-live']).toBe('assertive');
    expect(screen.getByRole('button', { name: 'Open case' })).toBeDisabled();
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

    await choose('Court', 'Middle District of Florida');
    await choose('Division', 'Tampa Division');
    await userEvent.press(screen.getByRole('button', { name: 'Open case' }));

    expect(await screen.findByText(/Chapter 7 · Middle District of Florida/)).toBeTruthy();
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
