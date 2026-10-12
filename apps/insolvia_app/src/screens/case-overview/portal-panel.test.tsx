import { screen, userEvent, waitFor } from '@testing-library/react-native';
import { renderRouter } from 'expo-router/testing-library';

import type { AuthConfig } from '@/config/environment';
import { writeRefreshToken } from '@/session';
import {
  caseBody,
  installFakeBrowser,
  jsonResponse,
  routeFetch,
  TEST_AUTH_CONFIG,
  tokenEndpointResponse,
} from '@/session/testing';
import type { FakeBrowser } from '@/session/testing';

/**
 * The firm's "Client portal" panel on the case overview (ADR 0023 PR 2),
 * through the real router: gated on `client_portal`, and — on a joint case —
 * offering each debtor or one login for both.
 */

let mockAuthConfig: AuthConfig | null = null;

jest.mock('@/config/environment', () => ({
  ...jest.requireActual('@/config/environment'),
  resolveAuthConfig: () => mockAuthConfig,
}));

const CASE_ID = '00000000-0000-4000-8000-0000000000c1';
const PAT = '00000000-0000-4000-8000-0000000000a1';

function me(clientPortal: string | undefined) {
  return {
    subject: '00000000-0000-4000-8000-00000000a11c',
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
        notes: 'hidden',
        firm_administration: 'hidden',
        ...(clientPortal === undefined ? {} : { client_portal: clientPortal }),
      },
    },
  };
}

function debtor(role: 'debtor_1' | 'debtor_2', given: string, email?: string) {
  return {
    id: role,
    case_id: CASE_ID,
    filing_role: role,
    created_at: '2026-08-04T10:00:00.000000Z',
    updated_at: '2026-08-04T10:00:00.000000Z',
    provenance: {},
    name: { given, surname: 'Example' },
    ...(email === undefined ? {} : { email }),
  };
}

/** `core/clients.binding_json`'s shape. */
function binding(status: 'invited' | 'active' | 'revoked', roles = ['debtor_1']) {
  return {
    subject: PAT,
    email: 'pat@example.test',
    displayName: 'Pat Example',
    roles,
    status,
    invitedBy: '00000000-0000-4000-8000-00000000a11c',
    createdAt: '2026-09-28T10:00:00.000000Z',
    updatedAt: '2026-09-28T10:00:00.000000Z',
  };
}

describe('the client portal panel', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  interface Setup {
    readonly clientPortal?: string;
    readonly debtors?: readonly unknown[];
    readonly clients?: readonly unknown[];
    readonly invite?: () => Response;
  }

  function open({ clientPortal, debtors = [], clients = [], invite }: Setup) {
    const route = routeFetch({
      '/oauth2/token': tokenEndpointResponse,
      '/v1/me': () => jsonResponse(200, me(clientPortal)),
      '/v1/firm/directory': () => jsonResponse(200, { people: [] }),
      [`/v1/cases/${CASE_ID}/portal/clients/${PAT}/resend`]: () =>
        new Response(null, { status: 204 }),
      [`/v1/cases/${CASE_ID}/portal/clients/${PAT}`]: () => jsonResponse(200, binding('revoked')),
      [`/v1/cases/${CASE_ID}/portal/clients`]: () => jsonResponse(200, { clients }),
      [`/v1/cases/${CASE_ID}/portal/invitation`]:
        invite ?? (() => jsonResponse(201, binding('invited'))),
      [`/v1/cases/${CASE_ID}/debtors`]: () => jsonResponse(200, { debtors }),
      [`/v1/cases/${CASE_ID}/documents`]: () => jsonResponse(200, { documents: [] }),
      [`/v1/cases/${CASE_ID}/creditors`]: () => jsonResponse(200, { creditors: [] }),
      [`/v1/cases/${CASE_ID}/packets`]: () => jsonResponse(200, { packets: [] }),
      [`/v1/cases/${CASE_ID}/assignees`]: () => jsonResponse(200, { assignees: [] }),
      [`/v1/cases/${CASE_ID}/events`]: () => jsonResponse(200, { events: [] }),
      [`/v1/cases/${CASE_ID}/tasks`]: () => jsonResponse(200, { tasks: [] }),
      [`/v1/cases/${CASE_ID}/summary`]: () => jsonResponse(500, {}),
      [`/v1/cases/${CASE_ID}`]: () => jsonResponse(200, caseBody(CASE_ID)),
    });
    const fetchMock = jest.fn((url: string, _init?: RequestInit) => route(url));
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    renderRouter('src/app', { initialUrl: `/cases/${CASE_ID}` });
    return fetchMock;
  }

  function requests(fetchMock: jest.Mock, fragment: string) {
    return fetchMock.mock.calls
      .filter((call) => String(call[0]).includes(fragment))
      .map((call) => ({
        method: (call[1] as RequestInit | undefined)?.method ?? 'GET',
        body: (call[1] as RequestInit | undefined)?.body,
      }));
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

  it('is absent for a firm user without client_portal — hidden by default', async () => {
    const fetchMock = open({});

    expect(await screen.findByRole('heading', { name: 'Notes' })).toBeTruthy();
    expect(screen.queryByRole('heading', { name: 'Client portal' })).toBeNull();
    expect(requests(fetchMock, '/portal/')).toEqual([]);
  });

  it('lists invited clients read-only at view_only', async () => {
    open({ clientPortal: 'view_only', clients: [binding('active')] });

    expect(await screen.findByText('Pat Example')).toBeTruthy();
    expect(screen.getByText('Active')).toBeTruthy();
    expect(screen.queryByRole('button', { name: /Revoke portal access/ })).toBeNull();
    expect(screen.queryByRole('heading', { name: 'Invite a client' })).toBeNull();
  });

  it('invites the single debtor of a one-debtor case without asking whom for', async () => {
    const fetchMock = open({
      clientPortal: 'add_edit',
      debtors: [debtor('debtor_1', 'Pat', 'pat@example.test')],
    });

    await screen.findByRole('heading', { name: 'Invite a client' });
    expect(screen.queryByRole('radiogroup')).toBeNull();
    // Prefilled from the debtor record.
    expect(screen.getByDisplayValue('Pat Example')).toBeTruthy();
    await userEvent.press(screen.getByRole('button', { name: 'Send invitation' }));

    expect(await screen.findByText(/Invited Pat Example/)).toBeTruthy();
    const [sent] = requests(fetchMock, '/portal/invitation');
    expect(sent?.method).toBe('POST');
    expect(JSON.parse(String(sent?.body))).toEqual({
      email: 'pat@example.test',
      displayName: 'Pat Example',
    });
  });

  it('on a joint case offers each debtor, or one login for both', async () => {
    const fetchMock = open({
      clientPortal: 'add_edit',
      debtors: [debtor('debtor_1', 'Pat'), debtor('debtor_2', 'Sam', 'sam@example.test')],
    });

    await screen.findByRole('heading', { name: 'Invite a client' });
    expect(screen.getByRole('radio', { name: 'Pat Example' })).toBeTruthy();
    expect(screen.getByRole('radio', { name: 'Sam Example' })).toBeTruthy();
    await userEvent.press(screen.getByRole('radio', { name: 'Both debtors, one login' }));
    await userEvent.type(screen.getByLabelText('Email address'), 'pat@example.test');
    await userEvent.press(screen.getByRole('button', { name: 'Send invitation' }));

    await screen.findByText(/Invited Pat Example/);
    const [sent] = requests(fetchMock, '/portal/invitation');
    expect(JSON.parse(String(sent?.body))).toEqual({
      email: 'pat@example.test',
      displayName: 'Pat Example',
      roles: ['debtor_1', 'debtor_2'],
    });
  });

  it('prefills the second debtor when the login is for them', async () => {
    const fetchMock = open({
      clientPortal: 'add_edit',
      debtors: [debtor('debtor_1', 'Pat'), debtor('debtor_2', 'Sam', 'sam@example.test')],
    });

    await userEvent.press(await screen.findByRole('radio', { name: 'Sam Example' }));
    await userEvent.press(screen.getByRole('button', { name: 'Send invitation' }));

    await screen.findByText(/Invited/);
    const [sent] = requests(fetchMock, '/portal/invitation');
    expect(JSON.parse(String(sent?.body))).toEqual({
      email: 'sam@example.test',
      displayName: 'Sam Example',
      roles: ['debtor_2'],
    });
  });

  it("shows the server's own sentence for a conflict", async () => {
    open({
      clientPortal: 'add_edit',
      debtors: [debtor('debtor_1', 'Pat', 'pat@example.test')],
      invite: () =>
        jsonResponse(409, {
          error: 'Conflict',
          message:
            "that email address already has an Insolvia account that is not one of your firm's clients",
        }),
    });

    await userEvent.press(await screen.findByRole('button', { name: 'Send invitation' }));

    const message = await screen.findByText(
      "That email address already has an Insolvia account that is not one of your firm's clients.",
    );
    expect(message.props['aria-live']).toBe('assertive');
  });

  it('re-sends an unused invitation', async () => {
    const fetchMock = open({ clientPortal: 'add_edit', clients: [binding('invited')] });

    await userEvent.press(
      await screen.findByRole('button', { name: 'Re-send invitation to Pat Example' }),
    );

    expect(await screen.findByText('Invitation re-sent to pat@example.test.')).toBeTruthy();
    expect(requests(fetchMock, '/resend')).toEqual([{ method: 'POST', body: undefined }]);
  });

  it('revokes only after an explicit confirmation', async () => {
    const fetchMock = open({ clientPortal: 'add_edit', clients: [binding('active')] });

    await userEvent.press(
      await screen.findByRole('button', { name: 'Revoke portal access for Pat Example' }),
    );
    expect(await screen.findByText('Revoke portal access for Pat Example?')).toBeTruthy();
    expect(
      requests(fetchMock, `/portal/clients/${PAT}`).filter((r) => r.method === 'DELETE'),
    ).toEqual([]);

    await userEvent.press(screen.getByRole('button', { name: 'Revoke access' }));

    await waitFor(() => {
      expect(
        requests(fetchMock, `/portal/clients/${PAT}`).filter((r) => r.method === 'DELETE'),
      ).toHaveLength(1);
    });
    expect(await screen.findByText('Pat Example no longer has portal access.')).toBeTruthy();
  });
});
