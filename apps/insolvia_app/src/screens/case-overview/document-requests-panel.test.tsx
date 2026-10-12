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

function me(documents: string) {
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
        documents,
        extraction_review: 'add_edit',
        creditor_library: 'add_edit',
        tasks: 'hidden',
        firm_administration: 'hidden',
      },
    },
  };
}

/** One `document_requests.request_json` row. */
function request(title: string, status: string, overrides: Record<string, unknown> = {}) {
  return {
    id: `00000000-0000-4000-8000-${title.length.toString().padStart(12, '0')}`,
    caseId: CASE_ID,
    title,
    kind: 'bank_statement',
    description: null,
    status,
    documentIds: status === 'received' ? ['00000000-0000-4000-8000-0000000000d1'] : [],
    createdAt: '2099-01-01T00:00:00.000000Z',
    updatedAt: '2099-01-01T00:00:00.000000Z',
    receivedAt: status === 'received' ? '2099-01-02T00:00:00.000000Z' : null,
    ...overrides,
  };
}

const LISTED = {
  requests: [
    request('Bank statements', 'received'),
    request('Pay stubs for the last six months', 'requested', { kind: 'pay_stub' }),
    request('Vehicle titles', 'waived', { kind: 'other' }),
  ],
  progress: { total: 2, received: 1, outstanding: 1, waived: 1 },
};

const SUMMARY = {
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
};

function respond(
  documents: string,
  requests: () => Response,
  url: string,
  init?: RequestInit,
): Response {
  const method = (init?.method ?? 'GET').toUpperCase();
  if (url.includes('/oauth2/token')) return tokenEndpointResponse();
  if (url.includes('/v1/me')) return jsonResponse(200, me(documents));
  if (url.includes('/v1/firm/directory')) return jsonResponse(200, { people: [] });
  if (url.includes(`/v1/cases/${CASE_ID}/document-requests/from-checklist`))
    return jsonResponse(200, { ...LISTED, added: 0 });
  if (url.includes(`/v1/cases/${CASE_ID}/document-requests/`) && method === 'PATCH')
    return jsonResponse(200, request('Pay stubs for the last six months', 'waived'));
  if (url.includes(`/v1/cases/${CASE_ID}/document-requests`)) return requests();
  for (const [segment, body] of [
    ['debtors', { debtors: [] }],
    ['documents', { documents: [] }],
    ['creditors', { creditors: [] }],
    ['packets', { packets: [] }],
    ['assignees', { assignees: [] }],
    ['extraction/candidates', { candidates: [] }],
    ['notes', { notes: [] }],
    ['events', { events: [] }],
    ['summary', SUMMARY],
  ] as const) {
    if (url.includes(`/v1/cases/${CASE_ID}/${segment}`)) return jsonResponse(200, body);
  }
  if (url.includes(`/v1/cases/${CASE_ID}`)) return jsonResponse(200, caseBody(CASE_ID));
  return jsonResponse(404, { error: 'Not Found', message: 'not in this test' });
}

/**
 * The case overview's requested-documents panel (ADR 0023 PR 5 / #364),
 * through the real `/cases/[id]` route, as tasks-panel.test.tsx does.
 */
describe('the requested-documents panel', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  function render(documents: string, requests: () => Response = () => jsonResponse(200, LISTED)) {
    const fetchMock = jest.fn((url: string, init?: RequestInit) =>
      Promise.resolve(respond(documents, requests, url, init)),
    );
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

  it('shows arrived against outstanding, and each request’s status', async () => {
    render('view_only');

    expect(await screen.findByText('1 of 2 requested documents arrived')).toBeTruthy();
    expect(screen.getByText('1 outstanding. 1 not needed.')).toBeTruthy();
    expect(screen.getByText('Arrived')).toBeTruthy();
    expect(screen.getByText('Outstanding')).toBeTruthy();
    expect(screen.getByText('Not needed')).toBeTruthy();
    // View-only: nothing to press.
    expect(screen.queryByRole('button', { name: 'Request the checklist' })).toBeNull();
  });

  it('is absent when the caller cannot view documents', async () => {
    render('hidden');
    await screen.findByText('Filing readiness');
    expect(screen.queryByText('Requested documents')).toBeNull();
  });

  it('requests the firm’s checklist as an explicit act', async () => {
    const user = userEvent.setup();
    const fetchMock = render('add_edit', () =>
      jsonResponse(200, {
        requests: [],
        progress: { total: 0, received: 0, outstanding: 0, waived: 0 },
      }),
    );
    await screen.findByText(/Nothing requested yet/);

    await user.press(screen.getByRole('button', { name: 'Request the checklist' }));

    await waitFor(() => {
      expect(
        fetchMock.mock.calls.some(
          ([url, init]) =>
            String(url).endsWith(`/v1/cases/${CASE_ID}/document-requests/from-checklist`) &&
            init?.method === 'POST',
        ),
      ).toBe(true);
    });
  });

  it('waives an outstanding request', async () => {
    const user = userEvent.setup();
    const fetchMock = render('add_edit');
    await screen.findByText('1 of 2 requested documents arrived');

    await user.press(
      screen.getByRole('button', { name: 'Mark Pay stubs for the last six months not needed' }),
    );

    await waitFor(() => {
      const patch = fetchMock.mock.calls.find(([, init]) => init?.method === 'PATCH');
      expect(patch).toBeDefined();
      expect(JSON.parse(String(patch?.[1]?.body))).toEqual({ status: 'waived' });
    });
  });
});
