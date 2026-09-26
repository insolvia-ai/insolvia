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

const ALICE = '00000000-0000-4000-8000-00000000a11c';

function membership(tasksPermission: string) {
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
      accessAllCases: false,
      permissions: {
        cases: 'view_only',
        intake: 'view_only',
        documents: 'view_only',
        extraction_review: 'hidden',
        creditor_library: 'hidden',
        tasks: tasksPermission,
        firm_administration: 'hidden',
      },
    },
  };
}

function task(overrides: Record<string, unknown> = {}) {
  return {
    id: '00000000-0000-4000-8000-0000000000ta',
    caseId: '00000000-0000-4000-8000-0000000000c1',
    subject: 'Get the vehicle payoff',
    done: false,
    overdue: false,
    createdAt: '2026-09-01T00:00:00.000000Z',
    updatedAt: '2026-09-01T00:00:00.000000Z',
    createdBy: ALICE,
    ...overrides,
  };
}

/**
 * `/my-tasks` — every task assigned to the caller, across every case they can
 * reach (issue #356 / 14.4). A plain screen, deliberately — see the
 * component's own docstring.
 */
describe('the my-tasks screen', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  function signedIn(handlers: Readonly<Record<string, () => Response>>) {
    const route = routeFetch({ '/oauth2/token': tokenEndpointResponse, ...handlers });
    const fetchMock = jest.fn((url: string, _init?: RequestInit) => route(url));
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    renderRouter('src/app', { initialUrl: '/my-tasks' });
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

  it('explains rather than lists when the caller cannot view tasks', async () => {
    signedIn({ '/v1/me': () => jsonResponse(200, membership('hidden')) });

    await screen.findByText(/Your firm has not given you access to tasks\./);
  });

  it('lists tasks assigned to the caller, sorted the way the server sent them', async () => {
    signedIn({
      '/v1/me/tasks': () =>
        jsonResponse(200, { tasks: [task({ dueDate: '2000-01-01', overdue: true })] }),
      '/v1/me': () => jsonResponse(200, membership('view_only')),
    });

    expect(await screen.findByText('Get the vehicle payoff')).toBeTruthy();
    expect(screen.getByText('Overdue')).toBeTruthy();
    expect(screen.getByText('Due 2000-01-01')).toBeTruthy();
  });

  it('says so plainly when nothing is assigned', async () => {
    signedIn({
      '/v1/me/tasks': () => jsonResponse(200, { tasks: [] }),
      '/v1/me': () => jsonResponse(200, membership('view_only')),
    });

    await screen.findByText('Nothing assigned to you right now.');
  });

  it('completing a task from this screen drops it off the list', async () => {
    const user = userEvent.setup();
    const fetchMock = jest.fn((url: string, init?: RequestInit) => {
      if (url.includes('/oauth2/token')) return Promise.resolve(tokenEndpointResponse());
      if (url.includes('/v1/me') && !url.includes('tasks'))
        return Promise.resolve(jsonResponse(200, membership('add_edit')));
      if (url.includes('/v1/me/tasks') && (init?.method ?? 'GET') === 'GET') {
        return Promise.resolve(jsonResponse(200, { tasks: [task()] }));
      }
      if (url.includes('/tasks/') && init?.method === 'PATCH') {
        return Promise.resolve(jsonResponse(200, task({ done: true })));
      }
      throw new Error(`unhandled: ${String(init?.method ?? 'GET')} ${url}`);
    });
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    renderRouter('src/app', { initialUrl: '/my-tasks' });

    await screen.findByText('Get the vehicle payoff');
    await user.press(screen.getByRole('checkbox', { name: 'Mark Get the vehicle payoff done' }));

    await waitFor(() => expect(screen.queryByText('Get the vehicle payoff')).toBeNull());
  });

  it('gives the page exactly one level-1 heading', async () => {
    signedIn({
      '/v1/me/tasks': () => jsonResponse(200, { tasks: [] }),
      '/v1/me': () => jsonResponse(200, membership('view_only')),
    });
    await screen.findByText('Nothing assigned to you right now.');

    const level1 = screen.getAllByRole('heading').filter((node) => node.props['aria-level'] === 1);
    expect(level1).toHaveLength(1);
  });
});
