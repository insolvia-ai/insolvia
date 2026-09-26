import { screen, userEvent, waitFor } from '@testing-library/react-native';
import { renderRouter } from 'expo-router/testing-library';

import type { AuthConfig } from '@/config/environment';
import { persistentStore, writeTo } from '@/platform/browser';
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

const ALICE = '00000000-0000-4000-8000-00000000a11c';
const CASE_ID = '00000000-0000-4000-8000-0000000000c1';
const OTHER_CASE_ID = '00000000-0000-4000-8000-0000000000c2';

/** A firm membership carrying every permission this screen reads. */
function membership(over: Readonly<Record<string, string>> = {}): Record<string, unknown> {
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
        creditor_library: 'hidden',
        notes: 'add_edit',
        events: 'add_edit',
        tasks: 'add_edit',
        firm_administration: 'hidden',
        ...over,
      },
    },
  };
}

function task(overrides: Record<string, unknown> = {}) {
  return {
    id: '00000000-0000-4000-8000-0000000000ta',
    caseId: CASE_ID,
    subject: 'Get the vehicle payoff',
    done: false,
    overdue: false,
    createdAt: '2026-09-01T00:00:00.000000Z',
    updatedAt: '2026-09-01T00:00:00.000000Z',
    createdBy: ALICE,
    ...overrides,
  };
}

/** A debtor carrying only the parts `caseTitle` reads. */
function debtor(caseId: string, given: string, surname: string) {
  return {
    id: '00000000-0000-4000-8000-0000000000d1',
    case_id: caseId,
    filing_role: 'debtor_1',
    created_at: '2026-08-04T10:00:00.000000Z',
    updated_at: '2026-08-04T10:00:00.000000Z',
    provenance: {},
    name: { given, surname },
  };
}

function caseBody(id: string, overrides: Record<string, unknown> = {}) {
  return {
    id,
    createdBy: ALICE,
    chapter: 7,
    district: 'NDCA',
    status: 'intake',
    createdAt: '2026-08-04T10:00:00.000000Z',
    updatedAt: '2026-08-04T10:00:00.000000Z',
    ...overrides,
  };
}

/**
 * `/` — the dashboard (issue 14.7 / #359), through the real router.
 *
 * A dispatch function rather than `routeFetch`'s fragment map: this screen
 * makes four kinds of read on one mount (cases, tasks, events, and each
 * card's own fallback), and several are POSTs the map's GET-shaped fixtures
 * cannot express. Every endpoint answers something empty and valid by
 * default, so a test states only the one it is about — the same shape
 * `case-overview/index.test.tsx`'s `caseReads` uses.
 */
describe('the dashboard', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  function signedIn(
    respond: Readonly<{
      me?: Record<string, unknown>;
      tasks?: (url: string) => Response;
      calendar?: (url: string) => Response;
      cases?: (url: string) => Response;
      caseById?: (caseId: string) => Response;
      debtors?: (caseId: string) => Response;
      directory?: () => Response;
      createTask?: (caseId: string, body: unknown) => Response;
      addNote?: (caseId: string, body: unknown) => Response;
      addEvent?: (body: unknown) => Response;
    }> = {},
  ) {
    const jsonBody = (init: RequestInit | undefined): unknown => {
      if (init?.body === undefined) return undefined;
      try {
        return JSON.parse(String(init.body));
      } catch {
        // The token endpoint's body is form-encoded, not JSON — only the
        // JSON-bodied routes below ever read this.
        return undefined;
      }
    };

    const fetchMock = jest.fn((url: string, init?: RequestInit) => {
      const method = init?.method ?? 'GET';
      if (url.includes('/oauth2/token')) return Promise.resolve(tokenEndpointResponse());
      const body = jsonBody(init);
      if (url.includes('/v1/me/tasks')) {
        return Promise.resolve(respond.tasks?.(url) ?? jsonResponse(200, { tasks: [] }));
      }
      if (url.includes('/v1/me')) {
        return Promise.resolve(jsonResponse(200, respond.me ?? membership()));
      }
      if (url.includes('/v1/calendar')) {
        return Promise.resolve(
          respond.calendar?.(url) ?? jsonResponse(200, { from: '', to: '', events: [] }),
        );
      }
      if (url.includes('/v1/firm/directory')) {
        return Promise.resolve(respond.directory?.() ?? jsonResponse(200, { people: [] }));
      }
      const debtorsMatch = /\/v1\/cases\/([^/]+)\/debtors$/u.exec(url);
      if (debtorsMatch?.[1] !== undefined) {
        return Promise.resolve(
          respond.debtors?.(debtorsMatch[1]) ?? jsonResponse(200, { debtors: [] }),
        );
      }
      const tasksPostMatch = /\/v1\/cases\/([^/]+)\/tasks$/u.exec(url);
      if (tasksPostMatch?.[1] !== undefined && method === 'POST') {
        return Promise.resolve(
          respond.createTask?.(tasksPostMatch[1], body) ??
            jsonResponse(201, task({ caseId: tasksPostMatch[1], ...(body as object) })),
        );
      }
      const notesPostMatch = /\/v1\/cases\/([^/]+)\/notes$/u.exec(url);
      if (notesPostMatch?.[1] !== undefined && method === 'POST') {
        return Promise.resolve(
          respond.addNote?.(notesPostMatch[1], body) ??
            jsonResponse(201, {
              id: '00000000-0000-4000-8000-0000000000n1',
              case_id: notesPostMatch[1],
              text: (body as { text?: string }).text ?? '',
              author_subject: ALICE,
              author_name: 'Alice Attorney',
              created_at: '2026-09-01T00:00:00.000000Z',
              updated_at: '2026-09-01T00:00:00.000000Z',
            }),
        );
      }
      if (url.includes('/v1/firm/events') && method === 'POST') {
        return Promise.resolve(
          respond.addEvent?.(body) ??
            jsonResponse(201, {
              id: '00000000-0000-4000-8000-0000000000e1',
              title: (body as { title?: string }).title ?? '',
              start: '2026-09-01',
              end: '2026-09-01',
              all_day: true,
              attendees: [],
              generated: false,
              dismissed: false,
              created_by: ALICE,
              created_at: '2026-09-01T00:00:00.000000Z',
              updated_at: '2026-09-01T00:00:00.000000Z',
            }),
        );
      }
      const caseMatch = /\/v1\/cases\/([^/]+)$/u.exec(url);
      if (caseMatch?.[1] !== undefined && method === 'GET') {
        return Promise.resolve(
          respond.caseById?.(caseMatch[1]) ?? jsonResponse(200, caseBody(caseMatch[1])),
        );
      }
      if (url.includes('/v1/cases') && method === 'GET') {
        return Promise.resolve(respond.cases?.(url) ?? jsonResponse(200, { cases: [] }));
      }
      return Promise.reject(new Error(`unexpected ${method} ${url}`));
    });
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    const router = renderRouter('src/app', { initialUrl: '/' });
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

  it('gives the page exactly one level-1 heading, named Home', async () => {
    signedIn();
    await screen.findByRole('heading', { name: 'Home' });

    const level1 = screen.getAllByRole('heading').filter((node) => node.props['aria-level'] === 1);
    expect(level1).toHaveLength(1);
  });

  it('shows an empty state on every card for a new firm with nothing yet', async () => {
    signedIn();

    expect(
      await screen.findByText('No cases yet. Open one to see it here next time.'),
    ).toBeTruthy();
    expect(screen.getByText('Nothing assigned to you right now.')).toBeTruthy();
    expect(screen.getByText('Nothing scheduled.')).toBeTruthy();
  });

  it('lists tasks assigned to the caller and completes one inline', async () => {
    const { fetchMock } = signedIn({
      tasks: () => jsonResponse(200, { tasks: [task({ dueDate: '2000-01-01', overdue: true })] }),
    });

    expect(await screen.findByText('Get the vehicle payoff')).toBeTruthy();
    expect(screen.getByText('Overdue')).toBeTruthy();

    const user = userEvent.setup();
    await user.press(screen.getByRole('checkbox', { name: 'Mark Get the vehicle payoff done' }));

    await waitFor(() => {
      const patch = fetchMock.mock.calls.find(
        ([url, init]: [string, RequestInit?]) =>
          url.includes(`/v1/cases/${CASE_ID}/tasks/`) && init?.method === 'PATCH',
      );
      expect(patch).toBeTruthy();
    });
  });

  it('asks the server for firm-wide tasks rather than filtering here', async () => {
    const { fetchMock } = signedIn();
    await screen.findByText('Nothing assigned to you right now.');

    const user = userEvent.setup();
    await user.press(screen.getByRole('combobox', { name: 'Whose tasks' }));
    await user.press(await screen.findByRole('option', { name: 'Firm-wide' }));

    await waitFor(() => {
      const urls = fetchMock.mock.calls.map(([url]) => String(url));
      expect(urls.some((url) => url.includes('/v1/me/tasks?scope=firm'))).toBe(true);
    });
  });

  it('shows today, tomorrow and this week as the events window, and reads the server for each', async () => {
    const { fetchMock } = signedIn();
    await screen.findByText('Nothing scheduled.');

    for (const label of ['Today', 'Tomorrow', 'This week']) {
      expect(screen.getByRole('tab', { name: label })).toBeTruthy();
    }

    const user = userEvent.setup();
    await user.press(screen.getByRole('tab', { name: 'This week' }));

    await waitFor(() => {
      const windows = fetchMock.mock.calls.filter(([url]) => String(url).includes('/v1/calendar?'));
      // One call for the initial "Today" tab, one for "This week".
      expect(windows.length).toBeGreaterThanOrEqual(2);
    });
  });

  it('narrows events to "assigned to me" by asking the server', async () => {
    const { fetchMock } = signedIn();
    await screen.findByText('Nothing scheduled.');

    const user = userEvent.setup();
    await user.press(screen.getByRole('combobox', { name: 'Whose events' }));
    await user.press(await screen.findByRole('option', { name: 'Assigned to me' }));

    await waitFor(() => {
      const urls = fetchMock.mock.calls.map(([url]) => String(url));
      expect(urls.some((url) => url.includes('/v1/calendar?') && url.includes('attendee=me'))).toBe(
        true,
      );
    });
  });

  it('falls back to the firm’s most recently opened cases when nothing has been viewed', async () => {
    signedIn({
      cases: () =>
        jsonResponse(200, {
          cases: [caseBody(CASE_ID, { status: 'filed', filedAt: '2026-08-20' })],
        }),
      debtors: (caseId) => jsonResponse(200, { debtors: [debtor(caseId, 'Marisol', 'Reyes')] }),
    });

    expect(await screen.findByText('Marisol Reyes')).toBeTruthy();
    expect(screen.getByText(/Chapter 7 · NDCA/)).toBeTruthy();
    expect(screen.getByText(/Filed 2026-08-20/)).toBeTruthy();
  });

  it('prefers the cases this browser actually viewed over the firm-wide fallback', async () => {
    writeTo(persistentStore(), 'insolvia.recent-cases', JSON.stringify([OTHER_CASE_ID]));

    signedIn({
      caseById: (id) =>
        id === OTHER_CASE_ID
          ? jsonResponse(200, caseBody(OTHER_CASE_ID, { chapter: 13, district: 'CDCA' }))
          : jsonResponse(404, { error: 'NotFound' }),
      cases: () => jsonResponse(200, { cases: [caseBody(CASE_ID)] }),
    });

    expect(await screen.findByRole('link', { name: /Chapter 13 · CDCA/ })).toBeTruthy();
    // The firm-wide fallback's own case never renders — the viewed trail won.
    expect(screen.queryByRole('link', { name: /Chapter 7 · NDCA/ })).toBeNull();
  });

  it('sends "New case" to the existing create-case flow', async () => {
    const { router } = signedIn();
    await screen.findByRole('heading', { name: 'Home' });

    await userEvent.setup().press(screen.getByRole('button', { name: 'New case' }));

    await waitFor(() => {
      expect(router.getPathname()).toBe('/cases');
    });
  });

  it('adds a task through the quick-add sheet, to the chosen case', async () => {
    const { fetchMock } = signedIn({
      cases: () => jsonResponse(200, { cases: [caseBody(CASE_ID)] }),
    });
    await screen.findByRole('heading', { name: 'Home' });

    const user = userEvent.setup();
    await user.press(screen.getByRole('button', { name: 'Add task' }));
    // Dialog.Title is RN's `accessibilityRole="header"`, which react-native-web
    // does not map onto the DOM `heading` role — so this is found by text, the
    // same way `documents/index.test.tsx` finds `AlertDialog.Title`.
    await screen.findByText('Add a task');

    await user.press(screen.getByRole('combobox', { name: 'Case' }));
    await user.press(await screen.findByRole('option', { name: 'Chapter 7 · NDCA' }));
    await user.type(screen.getByLabelText('Subject'), 'Call the lender');
    // Two "Add task" buttons exist once the sheet is open — the quick action
    // that opened it, and the sheet's own submit — in that document order.
    const submit = screen.getAllByRole('button', { name: 'Add task' })[1];
    if (submit === undefined) throw new Error('the sheet’s own "Add task" button did not render');
    await user.press(submit);

    await waitFor(() => {
      const post = fetchMock.mock.calls.find(
        ([url, init]: [string, RequestInit?]) =>
          url === `http://localhost:8080/v1/cases/${CASE_ID}/tasks` && init?.method === 'POST',
      );
      expect(post).toBeTruthy();
      expect(JSON.parse(String(post?.[1]?.body))).toMatchObject({ subject: 'Call the lender' });
    });
  });

  it('adds a firm event through the quick-add sheet, reusing the shared event form', async () => {
    const { fetchMock } = signedIn();
    await screen.findByRole('heading', { name: 'Home' });

    const user = userEvent.setup();
    await user.press(screen.getByRole('button', { name: 'Add event' }));
    await screen.findByText('Add a firm event');

    await user.type(screen.getByLabelText('Title'), 'CLE deadline');
    // Same shape as "Add task" above: the trigger and the sheet's own submit.
    const submit = screen.getAllByRole('button', { name: 'Add event' })[1];
    if (submit === undefined) throw new Error('the sheet’s own "Add event" button did not render');
    await user.press(submit);

    await waitFor(() => {
      const post = fetchMock.mock.calls.find(
        ([url, init]: [string, RequestInit?]) =>
          url === 'http://localhost:8080/v1/firm/events' && init?.method === 'POST',
      );
      expect(post).toBeTruthy();
    });
  });

  it('hides the tasks and events cards from someone the firm has not granted them', async () => {
    signedIn({ me: membership({ tasks: 'hidden', events: 'hidden', notes: 'hidden' }) });
    await screen.findByRole('heading', { name: 'Home' });

    expect(screen.queryByText('Tasks')).toBeNull();
    expect(screen.queryByText('Events')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Add task' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Add note' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Add event' })).toBeNull();
    // Recent cases is not feature-gated — it still renders.
    expect(screen.getByText('Recent cases')).toBeTruthy();
  });
});
