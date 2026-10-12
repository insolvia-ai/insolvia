import { screen, userEvent, waitFor, within } from '@testing-library/react-native';
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

const CASE = {
  id: '00000000-0000-4000-8000-0000000000c1',
  createdBy: '00000000-0000-4000-8000-00000000a11c',
  chapter: 7,
  district: 'Middle District of Florida',
  court: 'flmb',
  division: 'tampa',
  status: 'intake',
  createdAt: '2026-08-04T10:00:00.000000Z',
  updatedAt: '2026-08-04T10:00:00.000000Z',
};

/** Active clients in the firm's directory (ADR 0022), in `firm_client_json`'s shape. */
function client(id: string, given: string, surname: string, status = 'active') {
  return {
    id,
    status,
    name: { given, surname },
    created_at: '2026-09-27T09:00:00.000000Z',
    updated_at: '2026-09-27T09:00:00.000000Z',
    created_by: CASE.createdBy,
  };
}
const JORDAN = client('00000000-0000-4000-8000-0000000c11a0', 'Jordan', 'Example');
const RILEY = client('00000000-0000-4000-8000-0000000c11a1', 'Riley', 'Example');
const GONE = client('00000000-0000-4000-8000-0000000c11a2', 'Casey', 'Archived', 'archived');

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

/** A `/v1/me` body; `withDefaults` gives its firm the #360 case defaults. */
function member({ withDefaults = false, cases = 'add_edit' } = {}) {
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
      permissions: { cases, clients: 'add_edit' },
      defaultCourt: withDefaults ? 'txsb' : null,
      defaultDivision: withDefaults ? 'houston' : null,
      defaultChapter: withDefaults ? 13 : null,
      letterhead: null,
      signatureBlock: null,
    },
  };
}

type Handler = (init: RequestInit | undefined) => Response;

/** Picks an option in one of the `Select`s by its accessible names. */
async function choose(control: string, option: string) {
  await userEvent.press(screen.getByRole('combobox', { name: control }));
  await userEvent.press(await screen.findByRole('option', { name: option }));
}

/**
 * `/cases/new` — a case opened FOR ONE OR TWO CLIENTS (ADR 0022 / #354),
 * which replaced #411's minimal picker on `/cases`.
 *
 * Through the real router, signed in. What is asserted is the wiring the
 * server cannot check for us: the body `POST /v1/cases` receives (the
 * clients in filing-role order, the court as a code pair, never a district
 * string), a new client added BEFORE the case and chosen from then on, the
 * server's per-field words on the field they name, and where the preparer
 * lands afterwards.
 */
describe('the new-case screen', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  /**
   * Signs in at `url`. Handlers match by URL substring, in insertion order,
   * and see the request's init — so one path can answer GET and POST
   * differently. The registry, the directory and `/v1/me` have defaults a
   * test's own handler replaces.
   */
  function signedIn(handlers: Readonly<Record<string, Handler>> = {}, url = '/cases/new') {
    const all: Record<string, Handler> = {
      '/oauth2/token': () => tokenEndpointResponse(),
      ...handlers,
      ...('/v1/me' in handlers ? {} : { '/v1/me': () => jsonResponse(200, member()) }),
      ...('/v1/courts' in handlers ? {} : { '/v1/courts': () => jsonResponse(200, COURTS) }),
      ...('/v1/firm/clients' in handlers
        ? {}
        : {
            '/v1/firm/clients': () => jsonResponse(200, { clients: [JORDAN, RILEY, GONE] }),
          }),
      ...('/v1/cases' in handlers
        ? {}
        : {
            '/v1/cases': (init: RequestInit | undefined) =>
              init?.method === 'POST' ? jsonResponse(201, CASE) : jsonResponse(200, CASE),
          }),
    };
    const fetchMock = jest.fn((target: string, init?: RequestInit) => {
      for (const [fragment, respond] of Object.entries(all)) {
        if (target.includes(fragment)) return Promise.resolve(respond(init));
      }
      return Promise.reject(new Error(`unexpected request to ${target}`));
    });
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    const router = renderRouter('src/app', { initialUrl: url });
    return { fetchMock, router };
  }

  const posted = (fetchMock: jest.Mock, fragment: string) =>
    fetchMock.mock.calls
      .filter(
        ([target, init]: [string, RequestInit | undefined]) =>
          target.includes(fragment) && init?.method === 'POST',
      )
      .map(([, init]: [string, RequestInit | undefined]) => JSON.parse(String(init?.body)));

  const ready = () => screen.findByRole('combobox', { name: 'Court' });

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

  it('sends the chosen client, chapter, court and division — never a district string — and lands on the case', async () => {
    const { fetchMock, router } = signedIn();
    await ready();

    await choose('Client', 'Example, Jordan');
    await userEvent.press(screen.getByRole('radio', { name: /Chapter 13/ }));
    await choose('Court', 'Middle District of Florida');
    await choose('Division', 'Orlando Division');
    await userEvent.press(screen.getByRole('button', { name: 'Open case' }));

    await waitFor(() => {
      expect(posted(fetchMock, '/v1/cases')).toEqual([
        { chapter: 13, court: 'flmb', division: 'orlando', client_ids: [JORDAN.id] },
      ]);
    });
    // On the new case, not back on a list: the next thing anyone does with a
    // case they just opened is work in it.
    await waitFor(() => {
      expect(router.getPathname()).toBe(`/cases/${CASE.id}`);
    });
  });

  it('offers only active clients — an archived one cannot have a case opened', async () => {
    signedIn();
    await ready();

    await userEvent.press(screen.getByRole('combobox', { name: 'Client' }));
    expect(await screen.findByRole('option', { name: 'Example, Jordan' })).toBeTruthy();
    expect(screen.queryByRole('option', { name: 'Archived, Casey' })).toBeNull();
  });

  it('preselects the client a record or "Add client" sent here with ?client=', async () => {
    const { fetchMock } = signedIn({}, `/cases/new?client=${RILEY.id}`);
    await ready();
    // The trigger shows the chosen client's name.
    await screen.findByText('Example, Riley');

    await choose('Court', 'Middle District of Florida');
    await choose('Division', 'Tampa Division');
    await userEvent.press(screen.getByRole('button', { name: 'Open case' }));

    await waitFor(() => {
      expect(posted(fetchMock, '/v1/cases')[0]).toMatchObject({ client_ids: [RILEY.id] });
    });
  });

  it('opens a joint case for two clients, in filing-role order, and never offers one client twice', async () => {
    const { fetchMock } = signedIn();
    await ready();

    await choose('Client', 'Example, Jordan');
    await userEvent.press(screen.getByRole('checkbox', { name: 'Joint filing — two clients' }));
    await userEvent.press(
      await screen.findByRole('combobox', { name: 'Second client (Debtor 2)' }),
    );
    // One client, one role per case: Debtor 1's client is not a Debtor 2 option.
    expect(await screen.findByRole('option', { name: 'Example, Riley' })).toBeTruthy();
    expect(screen.queryByRole('option', { name: 'Example, Jordan' })).toBeNull();
    await userEvent.press(screen.getByRole('option', { name: 'Example, Riley' }));
    await choose('Court', 'Middle District of Florida');
    await choose('Division', 'Tampa Division');
    await userEvent.press(screen.getByRole('button', { name: 'Open case' }));

    await waitFor(() => {
      expect(posted(fetchMock, '/v1/cases')[0]).toMatchObject({
        client_ids: [JORDAN.id, RILEY.id],
      });
    });
  });

  it('asks for the second client rather than opening a joint case for one', async () => {
    const { fetchMock } = signedIn();
    await ready();

    await choose('Client', 'Example, Jordan');
    await userEvent.press(screen.getByRole('checkbox', { name: 'Joint filing — two clients' }));
    await userEvent.press(screen.getByRole('button', { name: 'Open case' }));

    expect(
      await screen.findByText('Choose the second client, or untick joint filing.'),
    ).toBeTruthy();
    expect(posted(fetchMock, '/v1/cases')).toEqual([]);
  });

  it('with an empty directory, adds the named client first and opens the case for them', async () => {
    const { fetchMock } = signedIn({
      '/v1/firm/clients': (init) =>
        init?.method === 'POST'
          ? jsonResponse(201, client(JORDAN.id, 'Sam', 'Sample'))
          : jsonResponse(200, { clients: [] }),
    });
    await ready();

    await userEvent.type(await screen.findByLabelText('Client’s first name'), 'Sam');
    await userEvent.type(screen.getByLabelText('Client’s last name'), 'Sample');
    await choose('Court', 'Middle District of Florida');
    await choose('Division', 'Tampa Division');
    await userEvent.press(screen.getByRole('button', { name: 'Open case' }));

    await waitFor(() => {
      expect(posted(fetchMock, '/v1/firm/clients')).toEqual([
        { name: { given: 'Sam', surname: 'Sample' } },
      ]);
      expect(posted(fetchMock, '/v1/cases')[0]).toMatchObject({ client_ids: [JORDAN.id] });
    });
  });

  it('adds a new client once, even when the case is refused and retried', async () => {
    let casePosts = 0;
    const { fetchMock } = signedIn({
      '/v1/firm/clients': (init) =>
        init?.method === 'POST'
          ? jsonResponse(201, client(JORDAN.id, 'Sam', 'Sample'))
          : jsonResponse(200, { clients: [] }),
      '/v1/cases': (init) => {
        if (init?.method !== 'POST') return jsonResponse(200, CASE);
        casePosts += 1;
        return casePosts === 1
          ? jsonResponse(400, {
              error: 'ValidationError',
              fields: { court: 'Choose the bankruptcy court this case will be filed in.' },
            })
          : jsonResponse(201, CASE);
      },
    });
    await ready();

    await userEvent.type(await screen.findByLabelText('Client’s first name'), 'Sam');
    await userEvent.press(screen.getByRole('button', { name: 'Open case' }));
    expect(await screen.findByText(/Choose the bankruptcy court/)).toBeTruthy();

    await choose('Court', 'Middle District of Florida');
    await choose('Division', 'Tampa Division');
    await userEvent.press(screen.getByRole('button', { name: 'Open case' }));

    await waitFor(() => {
      expect(posted(fetchMock, '/v1/cases')).toHaveLength(2);
    });
    expect(posted(fetchMock, '/v1/firm/clients')).toHaveLength(1);
  });

  it('puts a refused new client’s name error under the name boxes', async () => {
    signedIn({
      '/v1/firm/clients': (init) =>
        init?.method === 'POST'
          ? jsonResponse(400, {
              error: 'ValidationError',
              fields: { name: 'Enter a surname or a first name.' },
            })
          : jsonResponse(200, { clients: [] }),
    });
    await ready();

    await userEvent.press(screen.getByRole('button', { name: 'Open case' }));

    expect(await screen.findByText('Enter a surname or a first name.')).toBeTruthy();
  });

  it('renders one named radio per chapter, with the label outside the circle', async () => {
    // RadioGroup.Item IS the 20dp circle, so a label nested inside it makes
    // four circles overlap into an unreadable pile. Only the label being
    // OUTSIDE the circle satisfies both assertions below.
    signedIn();
    await ready();

    expect(screen.getAllByRole('radio')).toHaveLength(4);
    for (const label of ['Chapter 7', 'Chapter 13', 'Chapter 11', 'Chapter 12']) {
      const radio = screen.getByRole('radio', { name: label });
      expect(within(radio).queryByText(label)).toBeNull();
      expect(screen.getByText(label)).toBeTruthy();
    }
  });

  it('offers only the chosen court’s divisions, and clears the division when the court changes', async () => {
    signedIn();
    await ready();

    await choose('Court', 'Southern District of Texas');
    await userEvent.press(screen.getByRole('combobox', { name: 'Division' }));
    expect(await screen.findByRole('option', { name: 'Houston Division' })).toBeTruthy();
    expect(screen.queryByRole('option', { name: 'Tampa Division' })).toBeNull();
    await userEvent.press(screen.getByRole('option', { name: 'Houston Division' }));

    await choose('Court', 'Middle District of Florida');
    expect(screen.queryByText('Houston Division')).toBeNull();
  });

  it('preselects the firm’s default court, division and chapter', async () => {
    const { fetchMock } = signedIn({
      '/v1/me': () => jsonResponse(200, member({ withDefaults: true })),
    });
    await ready();
    await screen.findByText('Houston Division');

    await choose('Client', 'Example, Jordan');
    await userEvent.press(screen.getByRole('button', { name: 'Open case' }));

    await waitFor(() => {
      expect(posted(fetchMock, '/v1/cases')[0]).toEqual({
        chapter: 13,
        court: 'txsb',
        division: 'houston',
        client_ids: [JORDAN.id],
      });
    });
  });

  it('puts a missing client on the client field, as the server words it', async () => {
    signedIn({
      '/v1/cases': () =>
        jsonResponse(400, {
          error: 'ValidationError',
          fields: { client_ids: 'Choose the client this case is for.' },
        }),
    });
    await ready();

    await userEvent.press(screen.getByRole('button', { name: 'Open case' }));

    expect(await screen.findByText('Choose the client this case is for.')).toBeTruthy();
  });

  it('cannot open a case while the court registry is unavailable', async () => {
    signedIn({ '/v1/courts': () => jsonResponse(500, { error: 'InternalError' }) });

    const message = await screen.findByText(/Could not load the court registry/);
    expect(message.props['aria-live']).toBe('assertive');
    expect(screen.getByRole('button', { name: 'Open case' })).toBeDisabled();
  });

  it('cannot open a case without access to the client directory, and says why', async () => {
    signedIn({ '/v1/firm/clients': () => jsonResponse(403, { error: 'ForbiddenError' }) });

    const message = await screen.findByText(/Could not load your client directory/);
    expect(message.props['aria-live']).toBe('assertive');
    expect(screen.getByRole('button', { name: 'Open case' })).toBeDisabled();
  });

  it('tells a colleague who may not open cases so, instead of a form the API would refuse', async () => {
    signedIn({ '/v1/me': () => jsonResponse(200, member({ cases: 'view_only' })) });

    expect(await screen.findByText(/not given you permission to open cases/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Open case' })).toBeNull();
  });

  it('gives the page exactly one level-1 heading', async () => {
    signedIn();
    await ready();

    const levelOnes = screen
      .getAllByRole('heading')
      .filter((node) => node.props['aria-level'] === 1);
    expect(levelOnes).toHaveLength(1);
  });
});
