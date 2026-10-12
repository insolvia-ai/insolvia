import { screen, userEvent, waitFor } from '@testing-library/react-native';
import { renderRouter } from 'expo-router/testing-library';

import type { AuthConfig } from '@/config/environment';
import { writeRefreshToken } from '@/session';
import {
  caseBody,
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

const CASE_ID = '00000000-0000-4000-8000-0000000000c1';
const ALICE = '00000000-0000-4000-8000-00000000a11c';
const DEADLINE_ID = '00000000-0000-4000-8000-00000000d0d0';
const HEARING_ID = '00000000-0000-4000-8000-00000000e0e0';

function me(events: string) {
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
      accessAllCases: true,
      permissions: {
        cases: 'add_edit',
        intake: 'add_edit',
        documents: 'add_edit',
        extraction_review: 'hidden',
        events,
        firm_administration: 'hidden',
      },
    },
  };
}

const DEADLINE = {
  id: DEADLINE_ID,
  case_id: CASE_ID,
  title: 'Schedules, statements and other documents due',
  description: 'Rule 1007(c)(1): …',
  start: '2026-03-16',
  end: '2026-03-16',
  all_day: true,
  attendees: [ALICE],
  generated: true,
  rule_id: 'frbp-1007c1-schedules',
  rule_citation: 'Fed. R. Bankr. P. 1007(c)(1)',
  dismissed: false,
  created_by: ALICE,
  created_at: '2026-03-02T10:00:00.000000Z',
  updated_at: '2026-03-02T10:00:00.000000Z',
};

const HEARING = {
  id: HEARING_ID,
  case_id: CASE_ID,
  title: 'Hearing on the motion',
  start: '2026-04-02',
  end: '2026-04-02',
  all_day: true,
  location: 'Courtroom 4',
  attendees: [],
  generated: false,
  dismissed: false,
  created_by: ALICE,
  created_at: '2026-03-02T10:00:00.000000Z',
  updated_at: '2026-03-02T10:00:00.000000Z',
};

/** One stubbed route: method, URL fragment, answer. */
interface Route {
  readonly method: string;
  readonly fragment: string;
  readonly respond: () => Response;
}

/**
 * The events panel on the case overview (issue 14.6 / #358), through the real
 * router so it renders inside the case layout it depends on for `useCase()`.
 *
 * Routes match by METHOD as well as fragment — a GET and a PATCH on the same
 * event URL need different answers in one test. The overview's other reads
 * are given empty answers so the page renders; the bare case URL is declared
 * LAST because it is a prefix of every other.
 */
describe('the events panel', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  function signedIn(routes: readonly Route[], events: string = 'add_edit') {
    const all: readonly Route[] = [
      { method: 'GET', fragment: '/v1/me', respond: () => jsonResponse(200, me(events)) },
      {
        method: 'GET',
        fragment: '/v1/firm/directory',
        respond: () => jsonResponse(200, { people: [] }),
      },
      ...routes,
      {
        method: 'GET',
        fragment: `/v1/cases/${CASE_ID}/debtors`,
        respond: () => jsonResponse(200, { debtors: [] }),
      },
      {
        method: 'GET',
        fragment: `/v1/cases/${CASE_ID}/documents`,
        respond: () => jsonResponse(200, { documents: [] }),
      },
      {
        method: 'GET',
        fragment: `/v1/cases/${CASE_ID}/creditors`,
        respond: () => jsonResponse(200, { creditors: [] }),
      },
      {
        method: 'GET',
        fragment: `/v1/cases/${CASE_ID}/packets`,
        respond: () => jsonResponse(200, { packets: [] }),
      },
      {
        method: 'GET',
        fragment: `/v1/cases/${CASE_ID}/assignees`,
        respond: () => jsonResponse(200, { assignees: [] }),
      },
      {
        method: 'GET',
        fragment: `/v1/cases/${CASE_ID}/summary`,
        respond: () => jsonResponse(500, { message: 'not under test' }),
      },
      {
        method: 'GET',
        fragment: `/v1/cases/${CASE_ID}`,
        respond: () => jsonResponse(200, caseBody(CASE_ID)),
      },
    ];
    const fetchMock = jest.fn((url: string, init?: RequestInit) => {
      // The refresh-token exchange POSTs, and it is not under test here.
      if (url.includes('/oauth2/token')) return Promise.resolve(tokenEndpointResponse());
      const method = init?.method ?? 'GET';
      const match = all.find((route) => route.method === method && url.includes(route.fragment));
      if (match === undefined) {
        return Promise.reject(new Error(`unexpected ${method} ${url}`));
      }
      return Promise.resolve(match.respond());
    });
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    renderRouter('src/app', { initialUrl: `/cases/${CASE_ID}` });
    return fetchMock;
  }

  const listing = (events: unknown[]): Route => ({
    method: 'GET',
    fragment: `/v1/cases/${CASE_ID}/events`,
    respond: () => jsonResponse(200, { events }),
  });

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

  it('lists a generated deadline with its rule, offering only to dismiss it', async () => {
    signedIn([listing([DEADLINE, HEARING])]);

    expect(await screen.findByText('Schedules, statements and other documents due')).toBeTruthy();
    expect(screen.getByText(/Fed\. R\. Bankr\. P\. 1007\(c\)\(1\)/)).toBeTruthy();
    expect(
      screen.getByRole('button', {
        name: 'Dismiss Schedules, statements and other documents due',
      }),
    ).toBeTruthy();
    // No edit and no remove on the generated one; both on the hand-made one.
    expect(
      screen.queryByRole('button', { name: 'Edit Schedules, statements and other documents due' }),
    ).toBeNull();
    expect(screen.getByRole('button', { name: 'Edit Hearing on the motion' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Remove Hearing on the motion' })).toBeTruthy();
  });

  it('dismisses a deadline with a PATCH and shows it struck through', async () => {
    const fetchMock = signedIn([
      listing([DEADLINE]),
      {
        method: 'PATCH',
        fragment: `/v1/cases/${CASE_ID}/events/${DEADLINE_ID}`,
        respond: () => jsonResponse(200, { ...DEADLINE, dismissed: true }),
      },
    ]);

    const user = userEvent.setup();
    await user.press(
      await screen.findByRole('button', {
        name: 'Dismiss Schedules, statements and other documents due',
      }),
    );

    await screen.findByRole('button', {
      name: 'Restore Schedules, statements and other documents due',
    });
    const patch = fetchMock.mock.calls.find(([, init]) => init?.method === 'PATCH');
    expect(patch).toBeDefined();
    expect(JSON.parse(String(patch?.[1]?.body))).toEqual({ dismissed: true });
    expect(screen.getByText(/· dismissed$/)).toBeTruthy();
  });

  it('hides every control at view_only and still lists the deadlines', async () => {
    signedIn([listing([DEADLINE])], 'view_only');

    expect(await screen.findByText('Schedules, statements and other documents due')).toBeTruthy();
    expect(screen.queryByRole('button', { name: /^Dismiss / })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Add event' })).toBeNull();
  });

  it('is absent entirely when the firm has not granted events', async () => {
    const fetchMock = signedIn([], 'hidden');

    await screen.findByText('Filing readiness');
    expect(screen.queryByText('Events and deadlines')).toBeNull();
    expect(fetchMock.mock.calls.some(([url]) => String(url).includes('/events'))).toBe(false);
  });

  it('says deadlines wait on the petition date until it is recorded', async () => {
    signedIn([listing([])]);

    expect(
      await screen.findByText('deadlines appear once the petition date is recorded'),
    ).toBeTruthy();
    expect(screen.getByLabelText('Petition filed on')).toBeTruthy();
    expect(screen.getByLabelText('§ 341 meeting first set for')).toBeTruthy();
  });

  it('adds a hand-made event and returns to the list', async () => {
    const fetchMock = signedIn([
      listing([]),
      {
        method: 'POST',
        fragment: `/v1/cases/${CASE_ID}/events`,
        respond: () => jsonResponse(201, HEARING),
      },
    ]);

    const user = userEvent.setup();
    await user.press(await screen.findByRole('button', { name: 'Add event' }));
    await user.type(await screen.findByLabelText('Title'), 'Hearing on the motion');
    await user.press(screen.getByRole('button', { name: 'Save event' }));

    const isAdd = ([url, init]: [string, RequestInit?]) =>
      init?.method === 'POST' && String(url).endsWith('/events');
    await waitFor(() => {
      expect(fetchMock.mock.calls.some(isAdd)).toBe(true);
    });
    const post = fetchMock.mock.calls.find(isAdd);
    expect(JSON.parse(String(post?.[1]?.body))).toMatchObject({
      title: 'Hearing on the motion',
      all_day: true,
    });
  });
});
