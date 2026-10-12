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
const COPY_ID = '00000000-0000-4000-8000-0000000000c2';
const ALICE = '00000000-0000-4000-8000-00000000a11c';

function me({ isAdmin = false }: { isAdmin?: boolean } = {}) {
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
      isAdmin,
      accessAllCases: true,
      permissions: {
        cases: 'add_edit',
        clients: 'view_only',
        intake: 'add_edit',
        documents: 'add_edit',
        extraction_review: 'hidden',
        events: 'hidden',
        firm_administration: 'hidden',
      },
    },
  };
}

interface Route {
  readonly method: string;
  readonly fragment: string;
  readonly respond: () => Response;
}

/**
 * The lifecycle panel on the case overview (issue 14.3 / #355), through the
 * real router so it renders inside the case layout `useCase()` needs. Routes
 * match by method and fragment; the bare case URL is LAST because it is a
 * prefix of every other.
 */
describe('the lifecycle panel', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  function signedIn(
    matter: Record<string, unknown>,
    routes: readonly Route[] = [],
    { isAdmin = false, history = [] as unknown[] } = {},
  ) {
    const all: readonly Route[] = [
      { method: 'GET', fragment: '/v1/me', respond: () => jsonResponse(200, me({ isAdmin })) },
      {
        method: 'GET',
        fragment: '/v1/firm/directory',
        respond: () =>
          jsonResponse(200, {
            people: [
              {
                subject: ALICE,
                email: 'alice@example.test',
                firstName: 'Alice',
                lastName: 'Attorney',
                displayName: 'Alice Attorney',
                role: 'attorney',
                status: 'active',
              },
            ],
          }),
      },
      ...routes,
      {
        method: 'GET',
        fragment: `/v1/cases/${CASE_ID}/status-history`,
        respond: () => jsonResponse(200, { history }),
      },
      {
        method: 'GET',
        fragment: `/v1/cases/${CASE_ID}/debtors`,
        respond: () => jsonResponse(200, { debtors: [] }),
      },
      {
        method: 'GET',
        fragment: `/v1/cases/${CASE_ID}/summary`,
        respond: () => jsonResponse(500, { message: 'not under test' }),
      },
      {
        method: 'GET',
        fragment: `/v1/cases/${CASE_ID}`,
        respond: () => jsonResponse(200, matter),
      },
    ];
    const fetchMock = jest.fn((url: string, init?: RequestInit) => {
      if (url.includes('/oauth2/token')) return Promise.resolve(tokenEndpointResponse());
      const method = init?.method ?? 'GET';
      const match = all.find((route) => route.method === method && url.includes(route.fragment));
      if (match === undefined) {
        // Every other overview read: empty is enough for the page to render.
        return Promise.resolve(jsonResponse(200, {}));
      }
      return Promise.resolve(match.respond());
    });
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    renderRouter('src/app', { initialUrl: `/cases/${CASE_ID}` });
    return fetchMock;
  }

  const sent = (fetchMock: jest.Mock, method: string, fragment: string) =>
    fetchMock.mock.calls.find(
      ([url, init]: [string, RequestInit?]) =>
        init?.method === method && String(url).includes(fragment),
    ) as [string, RequestInit?] | undefined;

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

  it('offers an intake case only the moves the server allows, and sends the move', async () => {
    const fetchMock = signedIn(caseBody(CASE_ID), [
      {
        method: 'PATCH',
        fragment: `/v1/cases/${CASE_ID}`,
        respond: () => jsonResponse(200, caseBody(CASE_ID, { status: 'ready_to_file' })),
      },
    ]);

    const user = userEvent.setup();
    await user.press(await screen.findByRole('button', { name: 'Mark ready to file' }));

    await waitFor(() => {
      expect(sent(fetchMock, 'PATCH', `/v1/cases/${CASE_ID}`)).toBeDefined();
    });
    const patch = sent(fetchMock, 'PATCH', `/v1/cases/${CASE_ID}`);
    expect(JSON.parse(String(patch?.[1]?.body))).toEqual({ status: 'ready_to_file' });
    // A case starts retained: there is no funnel on it to show.
    expect(screen.queryByLabelText('Funnel stage')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Record discharge' })).toBeNull();
  });

  it('files with the case number typed here, and shows the field the server names', async () => {
    const fetchMock = signedIn(caseBody(CASE_ID, { status: 'ready_to_file' }), [
      {
        method: 'PATCH',
        fragment: `/v1/cases/${CASE_ID}`,
        respond: () =>
          jsonResponse(400, {
            error: 'ValidationError',
            message: 'validation failed: filed_at',
            fields: { filed_at: 'A filed case needs its filed date.' },
          }),
      },
    ]);

    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('Case number'), '8:26-bk-01234');
    await user.press(screen.getByRole('button', { name: 'Mark filed' }));

    expect(await screen.findByText(/A filed case needs its filed date\./)).toBeTruthy();
    const patch = sent(fetchMock, 'PATCH', `/v1/cases/${CASE_ID}`);
    expect(JSON.parse(String(patch?.[1]?.body))).toEqual({
      status: 'filed',
      case_number: '8:26-bk-01234',
    });
  });

  it('lists the history with who made each move', async () => {
    signedIn(caseBody(CASE_ID), [], {
      history: [
        {
          changedAt: '2026-08-05T10:00:00.000000Z',
          changedBy: ALICE,
          fromStatus: 'intake',
          toStatus: 'ready_to_file',
        },
      ],
    });

    expect(await screen.findByText('In intake → Ready to file')).toBeTruthy();
    expect(await screen.findByText('2026-08-05 · Alice Attorney')).toBeTruthy();
  });

  it('a case the filing service filed shows its number, the receipt and who filed it', async () => {
    signedIn(
      {
        ...caseBody(CASE_ID, { status: 'filed' }),
        caseNumber: '6:99-bk-10000',
        filedAt: '2099-01-15',
      },
      [
        {
          method: 'GET',
          fragment: `/v1/cases/${CASE_ID}/documents`,
          respond: () =>
            jsonResponse(200, {
              documents: [
                {
                  id: 'd1',
                  caseId: CASE_ID,
                  kind: 'court_notice',
                  fileName: 'court-filing-receipt.pdf',
                  contentType: 'application/pdf',
                  byteSize: 4096,
                  uploadedAt: '2099-01-15T12:03:00.000Z',
                  status: 'stored',
                  channel: 'staff',
                  requestId: null,
                },
              ],
            }),
        },
      ],
      {
        history: [
          {
            changedAt: '2099-01-15T12:03:00.000000Z',
            changedBy: 'filing-worker',
            fromStatus: 'ready_to_file',
            toStatus: 'filed',
            filingId: 'f1',
          },
        ],
      },
    );

    expect(await screen.findByText('Case 6:99-bk-10000, filed 2099-01-15')).toBeTruthy();
    expect(await screen.findByRole('button', { name: 'View the receipt' })).toBeTruthy();
    expect(
      await screen.findByText('2099-01-15 · Insolvia’s filing service · electronic filing'),
    ).toBeTruthy();
  });

  it('archives the case with a PUT', async () => {
    const fetchMock = signedIn(caseBody(CASE_ID), [
      {
        method: 'PUT',
        fragment: `/v1/cases/${CASE_ID}/archived`,
        respond: () =>
          jsonResponse(200, {
            ...caseBody(CASE_ID),
            archivedAt: '2026-09-30T10:00:00.000000Z',
            archivedBy: ALICE,
          }),
      },
    ]);

    const user = userEvent.setup();
    await user.press(await screen.findByRole('button', { name: 'Archive case' }));

    await waitFor(() => {
      expect(sent(fetchMock, 'PUT', '/archived')).toBeDefined();
    });
    expect(JSON.parse(String(sent(fetchMock, 'PUT', '/archived')?.[1]?.body))).toEqual({
      archived: true,
    });
  });

  it('offers delete only to a firm admin, and only before filing', async () => {
    signedIn(caseBody(CASE_ID));
    await screen.findByRole('button', { name: 'Archive case' });
    expect(screen.queryByRole('button', { name: 'Delete case' })).toBeNull();
  });

  it('deletes after the admin confirms, and leaves for the case list', async () => {
    const fetchMock = signedIn(
      caseBody(CASE_ID),
      [
        {
          method: 'DELETE',
          fragment: `/v1/cases/${CASE_ID}`,
          respond: () => new Response(null, { status: 204 }),
        },
        { method: 'GET', fragment: '/v1/cases?', respond: () => jsonResponse(200, { cases: [] }) },
      ],
      { isAdmin: true },
    );

    const user = userEvent.setup();
    await user.press(await screen.findByRole('button', { name: 'Delete case' }));
    await user.press(await screen.findByRole('button', { name: 'Delete' }));

    await waitFor(() => {
      expect(sent(fetchMock, 'DELETE', `/v1/cases/${CASE_ID}`)).toBeDefined();
    });
  });

  it('copies the case with a POST', async () => {
    const fetchMock = signedIn(caseBody(CASE_ID), [
      {
        method: 'POST',
        fragment: `/v1/cases/${CASE_ID}/copy`,
        respond: () => jsonResponse(201, caseBody(COPY_ID)),
      },
    ]);

    const user = userEvent.setup();
    await user.press(await screen.findByRole('button', { name: 'Copy to a new case' }));

    await waitFor(() => {
      expect(sent(fetchMock, 'POST', '/copy')).toBeDefined();
    });
  });
});
