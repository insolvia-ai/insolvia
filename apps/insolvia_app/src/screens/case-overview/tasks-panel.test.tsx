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
const BOB = '00000000-0000-4000-8000-00000000b0b0';

function me(tasksPermission: string = 'add_edit') {
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
        extraction_review: 'add_edit',
        creditor_library: 'add_edit',
        tasks: tasksPermission,
        firm_administration: 'hidden',
      },
    },
  };
}

const DIRECTORY = {
  people: [
    {
      subject: ALICE,
      firstName: 'Alice',
      lastName: 'Attorney',
      displayName: 'Alice Attorney',
      role: 'attorney',
    },
    {
      subject: BOB,
      firstName: 'Bob',
      lastName: 'Paralegal',
      displayName: 'Bob Paralegal',
      role: 'paralegal',
    },
  ],
};

/** One `task_json` row. */
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

interface TaskStub {
  list?: () => Response;
  create?: () => Response;
  patch?: () => Response;
  del?: () => Response;
}

function respond(stub: TaskStub, url: string, init?: RequestInit): Response {
  const method = (init?.method ?? 'GET').toUpperCase();

  if (url.includes('/oauth2/token')) return tokenEndpointResponse();
  if (
    url === `https://api.example.test/v1/me` ||
    (url.includes('/v1/me') && !url.includes('/tasks'))
  )
    return jsonResponse(200, me());
  if (url.includes('/v1/firm/directory')) return jsonResponse(200, DIRECTORY);
  if (url.includes(`/v1/cases/${CASE_ID}/tasks/`)) {
    if (method === 'PATCH') return (stub.patch ?? (() => jsonResponse(200, task())))();
    if (method === 'DELETE') return (stub.del ?? (() => new Response(null, { status: 204 })))();
    return jsonResponse(200, task());
  }
  if (url.includes(`/v1/cases/${CASE_ID}/tasks`)) {
    if (method === 'POST') return (stub.create ?? (() => jsonResponse(201, task())))();
    return (stub.list ?? (() => jsonResponse(200, { tasks: [] })))();
  }
  if (url.includes(`/v1/cases/${CASE_ID}/debtors`)) return jsonResponse(200, { debtors: [] });
  if (url.includes(`/v1/cases/${CASE_ID}/documents`)) return jsonResponse(200, { documents: [] });
  if (url.includes(`/v1/cases/${CASE_ID}/creditors`)) return jsonResponse(200, { creditors: [] });
  if (url.includes(`/v1/cases/${CASE_ID}/packets`)) return jsonResponse(200, { packets: [] });
  if (url.includes(`/v1/cases/${CASE_ID}/assignees`)) return jsonResponse(200, { assignees: [] });
  if (url.includes(`/v1/cases/${CASE_ID}/extraction/candidates`))
    return jsonResponse(200, { candidates: [] });
  if (url.includes(`/v1/cases/${CASE_ID}/summary`))
    return jsonResponse(200, {
      readyToFile: false,
      problems: [],
      totals: {
        realEstate: '0',
        personalProperty: '0',
        assets: '0',
        totalExempt: '0',
        totalNonExempt: '0',
        secured: '0',
        priorityUnsecured: '0',
        nonpriorityUnsecured: '0',
        liabilities: '0',
        monthlyIncome: '0',
        monthlyExpenses: '0',
        monthlyExcess: '0',
      },
      liens: { claims: [], assets: [] },
    });
  if (url.includes(`/v1/cases/${CASE_ID}`)) return jsonResponse(200, caseBody(CASE_ID));

  throw new Error(`unhandled request in tasks-panel test: ${method} ${url}`);
}

/**
 * The case-overview screen's tasks panel (issue #356 / 14.4), tested through
 * the real `/cases/[id]` route the way `case-overview/index.test.tsx` tests
 * the rest of the page — `useCase`/`useMembership`/`useApi` all come from
 * providers only the real route tree supplies.
 */
describe('the tasks panel', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  function render(tasksPermission: string, stub: TaskStub = {}) {
    const fetchMock = jest.fn((url: string, init?: RequestInit) => {
      // `me()` needs the permission this test asked for, so it is inlined
      // here rather than routed through `respond`'s fixed default.
      if (url.includes('/v1/me') && !url.includes('/tasks')) {
        return Promise.resolve(jsonResponse(200, me(tasksPermission)));
      }
      return Promise.resolve(respond(stub, url, init));
    });
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    renderRouter('src/app', { initialUrl: `/cases/${CASE_ID}` });
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

  it('is absent entirely when the caller cannot view tasks', async () => {
    render('hidden');
    await screen.findByText('Filing readiness');
    expect(screen.queryByText('Tasks')).toBeNull();
  });

  it('lists a case’s tasks, with the overdue badge for a late one', async () => {
    render('view_only', {
      list: () =>
        jsonResponse(200, {
          tasks: [task({ dueDate: '2000-01-01', overdue: true, assigneeSubject: BOB })],
        }),
    });

    expect(await screen.findByText('Get the vehicle payoff')).toBeTruthy();
    expect(screen.getByText('Overdue')).toBeTruthy();
    expect(screen.getByText('Bob Paralegal')).toBeTruthy();
  });

  it('a view-only caller sees no Add task button', async () => {
    render('view_only', { list: () => jsonResponse(200, { tasks: [task()] }) });
    await screen.findByText('Get the vehicle payoff');
    expect(screen.queryByRole('button', { name: 'Add task' })).toBeNull();
  });

  it('adds a task with only a subject, and shows it in the list', async () => {
    const user = userEvent.setup();
    const fetchMock = render('add_edit', {
      list: () => jsonResponse(200, { tasks: [] }),
      create: () => jsonResponse(201, task({ subject: 'Follow up with client' })),
    });
    await screen.findByText('No tasks yet.');

    await user.press(screen.getByRole('button', { name: 'Add task' }));
    await user.type(screen.getByLabelText('Subject'), 'Follow up with client');
    await user.press(screen.getByRole('button', { name: 'Add task' }));

    expect(await screen.findByText('Follow up with client')).toBeTruthy();
    await waitFor(() => {
      const createCall = fetchMock.mock.calls.find(
        ([url, init]) =>
          String(url).includes('/tasks') && (init as RequestInit | undefined)?.method === 'POST',
      );
      expect(createCall).toBeDefined();
      const body = JSON.parse(String((createCall?.[1] as RequestInit).body));
      expect(body).toEqual({ subject: 'Follow up with client' });
    });
  });

  it('completes a task from its checkbox', async () => {
    const user = userEvent.setup();
    const fetchMock = render('add_edit', {
      list: () => jsonResponse(200, { tasks: [task()] }),
      patch: () => jsonResponse(200, task({ done: true, completedAt: '2026-09-05T00:00:00Z' })),
    });
    await screen.findByText('Get the vehicle payoff');

    await user.press(screen.getByRole('checkbox', { name: 'Mark Get the vehicle payoff done' }));

    await waitFor(() => {
      const patchCall = fetchMock.mock.calls.find(
        ([url, init]) =>
          String(url).includes('/tasks/') && (init as RequestInit | undefined)?.method === 'PATCH',
      );
      expect(patchCall).toBeDefined();
      expect(JSON.parse(String((patchCall?.[1] as RequestInit).body))).toEqual({ done: true });
    });
  });

  it('reassigns a task through the edit form', async () => {
    const user = userEvent.setup();
    const fetchMock = render('add_edit', {
      list: () => jsonResponse(200, { tasks: [task()] }),
      patch: () => jsonResponse(200, task({ assigneeSubject: BOB })),
    });
    await screen.findByText('Get the vehicle payoff');

    await user.press(screen.getByRole('button', { name: 'Edit Get the vehicle payoff' }));
    await user.press(screen.getByRole('combobox', { name: 'Assigned to' }));
    await user.press(screen.getByRole('option', { name: 'Bob Paralegal' }));
    await user.press(screen.getByRole('button', { name: 'Save changes' }));

    await waitFor(() => {
      const patchCall = fetchMock.mock.calls.find(
        ([url, init]) =>
          String(url).includes('/tasks/') && (init as RequestInit | undefined)?.method === 'PATCH',
      );
      expect(patchCall).toBeDefined();
      const body = JSON.parse(String((patchCall?.[1] as RequestInit).body));
      expect(body.assigneeSubject).toBe(BOB);
    });
  });

  it('removes a task', async () => {
    const user = userEvent.setup();
    render('add_edit', { list: () => jsonResponse(200, { tasks: [task()] }) });
    await screen.findByText('Get the vehicle payoff');

    await user.press(screen.getByRole('button', { name: 'Remove Get the vehicle payoff' }));

    await waitFor(() => expect(screen.queryByText('Get the vehicle payoff')).toBeNull());
  });
});
