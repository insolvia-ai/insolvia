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

const CASE_ID = '00000000-0000-4000-8000-0000000000c1';
const ALICE = '00000000-0000-4000-8000-00000000a11c';

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

/** A deadline on the 16th of the current month, so it lands in the default view. */
function thisMonth(day: string): string {
  const now = new Date();
  const month = String(now.getMonth() + 1).padStart(2, '0');
  return `${now.getFullYear()}-${month}-${day}`;
}

const DEADLINE = {
  id: '00000000-0000-4000-8000-00000000d0d0',
  case_id: CASE_ID,
  title: 'Proofs of claim due (non-governmental creditors)',
  start: thisMonth('16'),
  end: thisMonth('16'),
  all_day: true,
  attendees: [ALICE],
  generated: true,
  rule_id: 'frbp-3002c-proof-of-claim',
  rule_citation: 'Fed. R. Bankr. P. 3002(c)',
  dismissed: false,
  created_by: ALICE,
  created_at: '2026-03-02T10:00:00.000000Z',
  updated_at: '2026-03-02T10:00:00.000000Z',
};

const OFFICE = {
  id: '00000000-0000-4000-8000-00000000e0e0',
  title: 'Office closed',
  start: thisMonth('20'),
  end: thisMonth('20'),
  all_day: true,
  attendees: [],
  generated: false,
  dismissed: false,
  created_by: ALICE,
  created_at: '2026-03-02T10:00:00.000000Z',
  updated_at: '2026-03-02T10:00:00.000000Z',
};

/**
 * `/calendar` (issue 14.6 / #358), through the real router.
 *
 * What is pinned is what the server cannot check: that the month grid shows
 * what the window answered, that "Mine" reaches the server as a narrowing
 * rather than a client-side filter, that the four views exist as tabs, and
 * that a firm without the grant gets an explanation and no request.
 */
describe('the calendar screen', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  function signedIn(
    events: string = 'add_edit',
    respondCalendar: (url: string) => Response = () =>
      jsonResponse(200, { from: '', to: '', events: [DEADLINE, OFFICE] }),
  ) {
    const fetchMock = jest.fn((url: string, init?: RequestInit) => {
      const method = init?.method ?? 'GET';
      if (url.includes('/oauth2/token')) return Promise.resolve(tokenEndpointResponse());
      if (url.includes('/v1/me')) return Promise.resolve(jsonResponse(200, me(events)));
      if (url.includes('/v1/firm/directory')) {
        return Promise.resolve(jsonResponse(200, { people: [] }));
      }
      if (url.includes('/v1/cases?')) return Promise.resolve(jsonResponse(200, { cases: [] }));
      if (url.includes('/v1/calendar?') && method === 'GET') {
        return Promise.resolve(respondCalendar(url));
      }
      if (url.includes('/v1/firm/events') && method === 'POST') {
        return Promise.resolve(jsonResponse(201, OFFICE));
      }
      return Promise.reject(new Error(`unexpected ${method} ${url}`));
    });
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    renderRouter('src/app', { initialUrl: '/calendar' });
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

  it('shows the month the window answered, deadlines and office events alike', async () => {
    const fetchMock = signedIn();

    expect(
      await screen.findByText('Proofs of claim due (non-governmental creditors)'),
    ).toBeTruthy();
    expect(screen.getByText('Office closed')).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Calendar' })).toBeTruthy();
    for (const label of ['Day', 'Week', 'Month', 'Agenda']) {
      expect(screen.getByRole('tab', { name: label })).toBeTruthy();
    }
    // One window read, for a whole month.
    const windows = fetchMock.mock.calls.filter(([url]) => String(url).includes('/v1/calendar?'));
    expect(windows).toHaveLength(1);
    expect(String(windows[0]?.[0])).toMatch(/from=\d{4}-\d{2}-01&to=\d{4}-\d{2}-\d{2}$/);
  });

  it('a deadline on a case is a link to that case', async () => {
    signedIn();

    const link = await screen.findByRole('link', {
      name: 'Proofs of claim due (non-governmental creditors) — Case',
    });
    expect(link).toBeTruthy();
  });

  it('narrows to "Mine" by asking the server, never by filtering here', async () => {
    const fetchMock = signedIn();
    await screen.findByText('Office closed');

    const user = userEvent.setup();
    await user.press(screen.getByRole('combobox', { name: 'Whose events' }));
    await user.press(await screen.findByRole('option', { name: 'Mine' }));

    await waitFor(() => {
      const urls = fetchMock.mock.calls.map(([url]) => String(url));
      expect(urls.some((url) => url.includes('/v1/calendar?') && url.includes('attendee=me'))).toBe(
        true,
      );
    });
  });

  it('switching to the agenda re-reads a 60-day window', async () => {
    const fetchMock = signedIn();
    await screen.findByText('Office closed');

    await userEvent.setup().press(screen.getByRole('tab', { name: 'Agenda' }));

    await waitFor(() => {
      expect(
        fetchMock.mock.calls.filter(([url]) => String(url).includes('/v1/calendar?')),
      ).toHaveLength(2);
    });
  });

  it('adds an office event through the firm-scoped route', async () => {
    const fetchMock = signedIn();
    await screen.findByText('Office closed');

    const user = userEvent.setup();
    await user.press(screen.getByRole('button', { name: 'Add office event' }));
    await user.type(await screen.findByLabelText('Title'), 'Office closed');
    await user.press(screen.getByRole('button', { name: 'Save event' }));

    await waitFor(() => {
      const post = fetchMock.mock.calls.find(
        ([url, init]) => init?.method === 'POST' && String(url).includes('/v1/firm/events'),
      );
      expect(post).toBeDefined();
      expect(JSON.parse(String(post?.[1]?.body))).toMatchObject({ title: 'Office closed' });
    });
  });

  it('explains rather than asks when the firm has not granted events', async () => {
    const fetchMock = signedIn('hidden');

    expect(await screen.findByText(/has not given you access to the calendar/)).toBeTruthy();
    expect(fetchMock.mock.calls.some(([url]) => String(url).includes('/v1/calendar'))).toBe(false);
    expect(screen.queryByRole('link', { name: 'Calendar' })).toBeNull();
  });
});
