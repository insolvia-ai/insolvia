import { screen, userEvent, waitFor } from '@testing-library/react-native';
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
const CASE_ID = '00000000-0000-4000-8000-0000000000c1';
const DIGEST = 'a'.repeat(64);
const FILE_DIGEST = 'c'.repeat(64);

function membership(electronicFiling: 'hidden' | 'view_only' | 'add_edit') {
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
      permissions: { cases: 'add_edit', electronic_filing: electronicFiling },
    },
  };
}

/** `basis_json`'s shape — what the screen shows and approves. */
function basis(overrides: Record<string, unknown> = {}) {
  return {
    scheme: 'insolvia-filing-approval/1',
    digest: DIGEST,
    ready: true,
    blockers: [],
    court: { code: 'flmb', name: 'Middle District of Florida', divisionName: 'Tampa Division' },
    registryRelease: 'courts/us-bankruptcy@2026-09-24',
    packet: {
      id: 'p1',
      createdAt: '2099-01-15T12:00:00.000Z',
      sha256: 'b'.repeat(64),
      byteSize: 2000000,
    },
    documents: [
      {
        position: 1,
        key: 'form/b101',
        title: 'B101 — Voluntary Petition',
        fileName: '01-b101.pdf',
        source: 'packet',
        handling: 'file',
        file: { byteSize: 204800, pageCount: 9, sha256: FILE_DIGEST },
      },
      {
        position: 2,
        key: 'signature_instrument',
        title: 'Declaration for E-Filing',
        fileName: 'signature-instrument.pdf',
        source: 'outside',
        handling: 'own_event',
      },
    ],
    checklist: [
      { id: 'court', status: 'ready', title: 'Court and division', detail: 'Tampa.' },
      { id: 'fee', status: 'action', title: 'Filing fee', detail: 'Paid at filing.' },
    ],
    fee: {
      handling: 'hand_back_at_payment',
      verified: false,
      detail: 'Insolvia stores and enters no card details: the filing is handed back to you.',
      deadline: 'Paid at filing.',
    },
    signInMaxAgeSeconds: 300,
    approvalTtlSeconds: 3600,
    ...overrides,
  };
}

const PENDING = {
  id: 'e1',
  filingId: 'f1',
  status: 'pending',
  digest: DIGEST,
  approvedBy: ALICE,
  approvedAt: '2099-01-15T12:00:00.000Z',
  expiresAt: '2099-01-15T13:00:00.000Z',
  credentialId: 'cred-1',
  court: 'flmb',
  division: 'tampa',
  packetId: 'p1',
};

/**
 * The approval on the packet screen (ADR 0024, guardrail 1): it shows the
 * court, the documents with sizes and digests, the checklist and the fee
 * handling; approving needs a fresh sign-in and posts back the digest it
 * showed; a pending approval can be cancelled; a voided one says why.
 */
describe('the filing approval panel', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  function render(options: {
    readonly view: unknown;
    readonly signedInSecondsAgo: number;
    readonly level?: 'hidden' | 'view_only' | 'add_edit';
    readonly write?: (init: RequestInit) => Response;
  }) {
    const idToken = fakeJwt({
      email: TEST_EMAIL,
      sub: ALICE,
      auth_time: Math.floor(Date.now() / 1000) - options.signedInSecondsAgo,
    });
    const fetchMock = jest.fn(async (url: string, init?: RequestInit) => {
      const method = init?.method ?? 'GET';
      if (url.includes('/oauth2/token')) return tokenEndpointResponse({ idToken });
      if (url.endsWith('/filing-approval')) {
        return method === 'GET' || options.write === undefined
          ? jsonResponse(200, options.view)
          : options.write(init ?? {});
      }
      if (url.endsWith('/v1/me')) return jsonResponse(200, membership(options.level ?? 'add_edit'));
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

  it('shows exactly what would be approved: court, documents with sizes and digests, fee', async () => {
    render({ view: { basis: basis() }, signedInSecondsAgo: 3600 });

    expect(await screen.findByRole('heading', { name: 'Approve this filing' })).toBeTruthy();
    expect(screen.getByText('Middle District of Florida, Tampa Division')).toBeTruthy();
    expect(screen.getByText('1. 01-b101.pdf')).toBeTruthy();
    expect(screen.getByText('200.0 KB, 9 pages')).toBeTruthy();
    expect(screen.getByText(`SHA-256 ${FILE_DIGEST}`)).toBeTruthy();
    expect(screen.getByText(/Prepared outside Insolvia/)).toBeTruthy();
    expect(screen.getByText(/stores and enters no card details/)).toBeTruthy();
    expect(screen.getByText(`Approval fingerprint ${DIGEST}`)).toBeTruthy();
  });

  it('after an old sign-in, asks for a fresh one with prompt=login back to this page', async () => {
    render({ view: { basis: basis() }, signedInSecondsAgo: 3600 });
    const user = userEvent.setup();

    await user.press(await screen.findByRole('button', { name: 'Sign in again to approve' }));

    expect(screen.queryByRole('button', { name: 'Approve filing' })).toBeNull();
    await waitFor(() => {
      expect(browser.navigations.at(-1) ?? '').toContain('/oauth2/authorize?');
    });
    const authorize = browser.navigations.at(-1) ?? '';
    expect(authorize.startsWith(`${TEST_AUTH_CONFIG.domain}/oauth2/authorize?`)).toBe(true);
    expect(authorize).toContain('prompt=login');
  });

  it('after a fresh sign-in, approves the digest it showed, once agreed', async () => {
    const fetchMock = render({
      view: { basis: basis() },
      signedInSecondsAgo: 10,
      write: () => jsonResponse(201, { basis: basis(), approval: PENDING }),
    });
    const user = userEvent.setup();

    const approve = await screen.findByRole('button', { name: 'Approve filing' });
    expect(approve).toBeDisabled();
    await user.press(
      screen.getByRole('checkbox', {
        name: 'I have reviewed this filing set and approve filing it under my court login',
      }),
    );
    await user.press(screen.getByRole('button', { name: 'Approve filing' }));

    expect(await screen.findByText(/Approved. The filing is queued/)).toBeTruthy();
    const post = fetchMock.mock.calls.find(
      ([url, init]) => init?.method === 'POST' && url.endsWith('/filing-approval'),
    );
    expect(JSON.parse(post?.[1]?.body as string)).toEqual({ digest: DIGEST });
    expect(screen.getByText('Approved — waiting to be filed')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Cancel approval' })).toBeTruthy();
  });

  it('falls back to signing in again when the API says the sign-in is stale', async () => {
    render({
      view: { basis: basis() },
      signedInSecondsAgo: 10,
      write: () =>
        jsonResponse(403, { error: 'ReauthenticationRequired', message: 'sign in again' }),
    });
    const user = userEvent.setup();

    await user.press(
      await screen.findByRole('checkbox', {
        name: 'I have reviewed this filing set and approve filing it under my court login',
      }),
    );
    await user.press(screen.getByRole('button', { name: 'Approve filing' }));

    expect(await screen.findByText(/no longer recent enough to approve/)).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Sign in again to approve' })).toBeTruthy();
  });

  it('says why an approval was voided, and offers approving again', async () => {
    render({
      view: {
        basis: basis(),
        approval: { ...PENDING, status: 'voided', voidReason: 'changed', voidedAt: 'x' },
      },
      signedInSecondsAgo: 10,
    });

    expect(await screen.findByText('Voided')).toBeTruthy();
    expect(screen.getByText(/changed after it was approved/)).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Approve filing' })).toBeTruthy();
  });

  it('cancels a pending approval', async () => {
    const fetchMock = render({
      view: { basis: basis(), approval: PENDING },
      signedInSecondsAgo: 3600,
      write: () =>
        jsonResponse(200, {
          basis: basis(),
          approval: { ...PENDING, status: 'voided', voidReason: 'cancelled' },
        }),
    });
    const user = userEvent.setup();

    await user.press(await screen.findByRole('button', { name: 'Cancel approval' }));

    expect(await screen.findByText(/Approval cancelled/)).toBeTruthy();
    expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'DELETE')).toBe(true);
  });

  it('names what blocks an approval and offers no button', async () => {
    render({
      view: {
        basis: basis({
          ready: false,
          blockers: ['packet'],
          checklist: [
            { id: 'packet', status: 'missing', title: 'Filing packet', detail: 'None yet.' },
          ],
        }),
      },
      signedInSecondsAgo: 10,
    });

    expect(await screen.findByText('Not ready to approve: Filing packet.')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Approve filing' })).toBeNull();
  });

  it('view_only sees the approval but is not offered approving', async () => {
    render({ view: { basis: basis() }, signedInSecondsAgo: 10, level: 'view_only' });

    expect(await screen.findByText('1. 01-b101.pdf')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Approve filing' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Sign in again to approve' })).toBeNull();
  });

  it('is absent for a member without electronic filing', async () => {
    const fetchMock = render({ view: { basis: basis() }, signedInSecondsAgo: 10, level: 'hidden' });

    expect(await screen.findByRole('heading', { name: 'Filing set and checklist' })).toBeTruthy();
    expect(screen.queryByRole('heading', { name: 'Approve this filing' })).toBeNull();
    expect(fetchMock.mock.calls.some(([url]) => String(url).endsWith('/filing-approval'))).toBe(
      false,
    );
  });
});
