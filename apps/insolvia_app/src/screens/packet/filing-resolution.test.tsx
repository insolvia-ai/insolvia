import { screen, userEvent } from '@testing-library/react-native';
import { renderRouter } from 'expo-router/testing-library';

import type { AuthConfig } from '@/config/environment';
import { writeRefreshToken } from '@/session';
import {
  caseBody,
  fakeJwt,
  installFakeBrowser,
  jsonResponse,
  TEST_AUTH_CONFIG,
  TEST_EMAIL,
  tokenEndpointResponse,
} from '@/session/testing';
import type { FakeBrowser } from '@/session/testing';

let mockAuthConfig: AuthConfig | null = null;

jest.mock('@/config/environment', () => ({
  ...jest.requireActual('@/config/environment'),
  resolveAuthConfig: () => mockAuthConfig,
}));

const ALICE = '00000000-0000-4000-8000-00000000a11c';
const BOB = '00000000-0000-4000-8000-0000000000b0';
const CASE_ID = '00000000-0000-4000-8000-0000000000c1';
const DIGEST = 'a'.repeat(64);

function membership() {
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
      permissions: { cases: 'add_edit', electronic_filing: 'add_edit' },
    },
  };
}

const BASIS = {
  scheme: 'insolvia-filing-approval/1',
  digest: DIGEST,
  ready: true,
  blockers: [],
  court: { code: 'flmb', name: 'Middle District of Florida' },
  registryRelease: 'courts/us-bankruptcy@2026-09-24',
  documents: [],
  checklist: [],
  fee: { handling: 'hand_back_at_payment', verified: false, detail: 'Handed back.' },
  signInMaxAgeSeconds: 300,
  approvalTtlSeconds: 3600,
};

const CONSUMED = {
  id: 'e1',
  filingId: 'f1',
  status: 'consumed',
  digest: DIGEST,
  approvedBy: ALICE,
  approvedAt: '2099-01-15T12:00:00.000Z',
  expiresAt: '2099-01-15T13:00:00.000Z',
  consumedAt: '2099-01-15T12:00:05.000Z',
  credentialId: 'cred-1',
  court: 'flmb',
  division: 'tampa',
  packetId: 'p1',
};

function filingRecord(overrides: Record<string, unknown> = {}) {
  return {
    filingId: 'f1',
    approvalId: 'e1',
    attorneyId: ALICE,
    state: 'handed_back',
    court: 'flmb',
    claimedAt: '2099-01-15T12:00:05.000Z',
    updatedAt: '2099-01-15T12:01:00.000Z',
    history: [
      { state: 'claimed', at: '2099-01-15T12:00:05.000Z' },
      { state: 'handed_back', at: '2099-01-15T12:01:00.000Z' },
    ],
    resolvable: true,
    handBack: {
      reason: 'court_not_verified',
      stage: 'claimed',
      title: 'Insolvia does not file in this court yet',
      action: 'File the case from the checklist in your own CM/ECF session.',
      link: 'packet',
    },
    ...overrides,
  };
}

describe('resolving a handed-back filing', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  function render(view: unknown, resolved?: unknown) {
    const idToken = fakeJwt({
      email: TEST_EMAIL,
      sub: ALICE,
      auth_time: Math.floor(Date.now() / 1000) - 3600,
    });
    const fetchMock = jest.fn(async (url: string, init?: RequestInit) => {
      const method = init?.method ?? 'GET';
      if (url.includes('/oauth2/token')) return tokenEndpointResponse({ idToken });
      if (url.endsWith('/filing-approval')) return jsonResponse(200, view);
      if (url.endsWith('/resolution') && method === 'POST') return jsonResponse(200, resolved);
      if (url.endsWith('/v1/me')) return jsonResponse(200, membership());
      if (url.endsWith('/documents')) return jsonResponse(200, { documents: [] });
      if (url.endsWith('/packets')) return jsonResponse(200, { packets: [] });
      if (url.endsWith('/forms')) return jsonResponse(200, { forms: [] });
      if (url.endsWith('/filing-set')) {
        return jsonResponse(200, {
          registryRelease: 'courts/us-bankruptcy@2026-09-24',
          filingMethod: 'hand_off',
          orderBasis: 'default',
          namesBasis: 'default',
          maxBytes: 52428800,
          maxBytesBasis: 'default',
          documents: [],
          checklist: [],
        });
      }
      if (url.endsWith('/debtors')) return jsonResponse(200, { debtors: [] });
      if (url.endsWith(CASE_ID)) return jsonResponse(200, caseBody(CASE_ID));
      throw new Error(`unexpected ${method} ${url}`);
    });
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    renderRouter('src/app', { initialUrl: `/cases/${CASE_ID}/packet` });
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

  it('shows why it was handed back, and offers no approval until it is resolved', async () => {
    render({ basis: BASIS, approval: CONSUMED, filing: filingRecord() });

    expect(await screen.findByText('Handed back to you')).toBeTruthy();
    expect(screen.getByText('Insolvia does not file in this court yet')).toBeTruthy();
    expect(
      screen.getByRole('heading', { name: 'Record what the court’s docket shows' }),
    ).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Sign in again to approve' })).toBeNull();
  });

  it('records "not filed" only after the docket check is confirmed', async () => {
    const fetchMock = render(
      { basis: BASIS, approval: CONSUMED, filing: filingRecord() },
      {
        basis: BASIS,
        approval: CONSUMED,
        filing: filingRecord({
          resolvable: false,
          resolution: {
            outcome: 'not_filed',
            resolvedBy: ALICE,
            resolvedAt: '2099-01-16T09:00:00.000Z',
            docketCheckedAt: '2099-01-16T09:00:00.000Z',
          },
        }),
      },
    );
    const user = userEvent.setup();

    await user.press(await screen.findByRole('radio', { name: 'The case is not on the docket' }));
    const record = screen.getByRole('button', { name: 'Record as not filed' });
    expect(record).toBeDisabled();
    await user.press(
      screen.getByRole('checkbox', { name: "I checked the court's own docket for this debtor" }),
    );
    await user.press(screen.getByRole('button', { name: 'Record as not filed' }));

    expect(await screen.findByText('Recorded. You can approve a new filing.')).toBeTruthy();
    const post = fetchMock.mock.calls.find(([url]) => String(url).endsWith('/resolution'));
    expect(post?.[0]).toContain(`/v1/cases/${CASE_ID}/filings/f1/resolution`);
    expect(JSON.parse(post?.[1]?.body as string)).toEqual({
      outcome: 'not_filed',
      docket_checked: true,
    });
    // Freed: the approval is offered again (behind a fresh sign-in).
    expect(screen.getByRole('button', { name: 'Sign in again to approve' })).toBeTruthy();
  });

  it('a filing the court confirmed can only be recorded as filed, under its number', async () => {
    render({
      basis: BASIS,
      approval: CONSUMED,
      filing: filingRecord({
        state: 'outcome_unknown',
        confirmation: {
          caseNumber: '6:99-bk-10000',
          filedAt: '2099-01-15T12:02:00Z',
          docketEntries: [],
        },
      }),
    });

    expect(await screen.findByText(/can only be recorded as filed/)).toBeTruthy();
    expect(screen.queryByRole('radio', { name: 'The case is not on the docket' })).toBeNull();
    expect(screen.getByDisplayValue('6:99-bk-10000')).toBeTruthy();
  });

  it('only the filing’s own attorney is offered the form', async () => {
    render({ basis: BASIS, approval: CONSUMED, filing: filingRecord({ attorneyId: BOB }) });

    expect(await screen.findByText(/Only the attorney whose court login/)).toBeTruthy();
    expect(
      screen.queryByRole('heading', { name: 'Record what the court’s docket shows' }),
    ).toBeNull();
  });

  it('a filed filing shows the court’s confirmation and no form', async () => {
    render({
      basis: { ...BASIS, ready: false, blockers: ['filed'] },
      approval: CONSUMED,
      filing: filingRecord({
        state: 'filed',
        resolvable: false,
        confirmation: {
          caseNumber: '6:99-bk-10000',
          filedAt: '2099-01-15T12:02:00Z',
          docketEntries: ['1 Voluntary Petition'],
        },
        receiptDocumentId: 'd1',
      }),
    });

    expect(await screen.findByText('Filed')).toBeTruthy();
    expect(
      screen.getByText('The court confirmed case 6:99-bk-10000, filed 2099-01-15T12:02:00Z.'),
    ).toBeTruthy();
    expect(
      screen.queryByRole('heading', { name: 'Record what the court’s docket shows' }),
    ).toBeNull();
  });
});
