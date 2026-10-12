import { screen, userEvent, waitFor } from '@testing-library/react-native';
import { renderRouter } from 'expo-router/testing-library';

import type { AuthConfig } from '@/config/environment';
import { writeRefreshToken } from '@/session';
import { installFakeBrowser, jsonResponse, TEST_AUTH_CONFIG } from '@/session/testing';
import type { FakeBrowser } from '@/session/testing';

import { bodiesOf, caseBody, clientFetch, firmClient, member } from './testing';
import type { Respond } from './testing';

let mockAuthConfig: AuthConfig | null = null;

jest.mock('@/config/environment', () => ({
  ...jest.requireActual('@/config/environment'),
  resolveAuthConfig: () => mockAuthConfig,
}));

const ALIAS = { id: 'alias-1', given: 'Jordan', surname: 'Maiden' };

const JORDAN = firmClient('00000000-0000-4000-8000-0000000c11a0', 'Jordan', 'Example', {
  email: 'jordan@example.test',
  phone: '555-0100',
  residence_address: { line1: '1 Main St', city: 'Tampa', state: 'FL', county: 'Hillsborough' },
  other_names_used: [ALIAS],
  lead_source: 'Referral',
  tax_id_last_four: '1234',
});

const JOINT = caseBody('00000000-0000-4000-8000-0000000000c1', { chapter: 13 });

/**
 * The record, its cases, and the writes — each answered from `state`, so a
 * PUT's response is what the next render shows.
 */
function record(
  overrides: {
    client?: Record<string, unknown> | null;
    cases?: unknown[];
    put?: (body: Record<string, unknown>) => Response;
  } = {},
): Respond {
  let current = overrides.client === undefined ? JORDAN : overrides.client;
  return (url, init) => {
    if (url.endsWith('/cases')) {
      return jsonResponse(200, {
        cases: overrides.cases ?? [{ filing_role: 'debtor_2', case: JOINT }],
      });
    }
    if (url.endsWith('/prospect-stage') && init?.method === 'PUT') {
      const { prospect_stage } = JSON.parse(String(init.body)) as {
        prospect_stage: string | null;
      };
      current = { ...(current ?? {}), prospect_stage: prospect_stage ?? undefined };
      return jsonResponse(200, current);
    }
    if (url.endsWith('/status') && init?.method === 'PUT') {
      const { status } = JSON.parse(String(init.body)) as { status: string };
      current = { ...(current ?? {}), status };
      return jsonResponse(200, current);
    }
    if (url.includes('/v1/firm/clients/')) {
      if (current === null) return jsonResponse(404, { error: 'NotFound' });
      if (init?.method === 'PUT') {
        const body = JSON.parse(String(init.body)) as Record<string, unknown>;
        if (overrides.put !== undefined) return overrides.put(body);
        current = { ...JORDAN, ...body, tax_id_last_four: '1234' };
        return jsonResponse(200, current);
      }
      return jsonResponse(200, current);
    }
    return undefined;
  };
}

/**
 * `/clients/<id>` — one client's record (ADR 0022 / #354), through the real
 * router, signed in.
 *
 * Asserted: the whole-record PUT keeps what the form does not show; archive
 * is a confirmed status write and restore undoes it; the tax ID is its last
 * four; the cases are the reachable ones, each saying which debtor this
 * client is; and the `clients` and `cases` gates.
 */
describe('the client record', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  function signedIn(respond: Respond = record(), me: unknown = member()) {
    const fetchMock = clientFetch(respond, me);
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    const router = renderRouter('src/app', { initialUrl: `/clients/${JORDAN.id}` });
    return { fetchMock, router };
  }

  const loaded = () => screen.findByRole('heading', { name: 'Jordan Example' });

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

  it('shows who the client is, and only the last four of their tax ID', async () => {
    signedIn();
    await loaded();

    expect(screen.getByText('jordan@example.test')).toBeTruthy();
    expect(screen.getByText(/1 Main St/)).toBeTruthy();
    expect(screen.getByText('Jordan Maiden')).toBeTruthy();
    expect(screen.getByText('Ending 1234')).toBeTruthy();
  });

  it('says a tax ID is entered on a case when the client has none', async () => {
    signedIn(record({ client: firmClient(JORDAN.id, 'Jordan', 'Example') }));
    await loaded();

    expect(screen.getByText('Not on file — it is entered on a case')).toBeTruthy();
  });

  it('lists the cases the caller may open, with which debtor this client is on each', async () => {
    signedIn();

    const link = await screen.findByRole('link', {
      name: 'Chapter 13 case in Middle District of Florida, opened 2026-08-04',
    });
    expect(link.props.href).toBe(`/cases/${JOINT.id}`);
    expect(screen.getByText('Debtor 2 · opened 2026-08-04')).toBeTruthy();
  });

  it('says "none you can open" — never how many were left out', async () => {
    signedIn(record({ cases: [] }));

    expect(await screen.findByText('No cases you can open for this client.')).toBeTruthy();
  });

  it('starts a case for this client on the new-case page, with them chosen', async () => {
    const { router } = signedIn();
    await loaded();

    await userEvent
      .setup()
      .press(screen.getByRole('button', { name: 'Start a case for this client' }));

    await waitFor(() => {
      expect(router.getPathname()).toBe('/cases/new');
    });
    expect(router.getSearchParams()).toMatchObject({ client: JORDAN.id });
  });

  it('saves an edit as a WHOLE record — keeping the aliases the form does not show', async () => {
    const { fetchMock } = signedIn();
    await loaded();
    const user = userEvent.setup();

    await user.press(screen.getByRole('button', { name: 'Edit details' }));
    const email = screen.getByLabelText('Email');
    await user.clear(email);
    await user.type(email, 'jordan.new@example.test');
    await user.clear(screen.getByLabelText('Lead source'));
    await user.press(screen.getByRole('button', { name: 'Save changes' }));

    expect(await screen.findByText('jordan.new@example.test')).toBeTruthy();
    const [body] = bodiesOf(fetchMock, 'PUT', `/v1/firm/clients/${JORDAN.id}`);
    expect(body).toEqual({
      name: { given: 'Jordan', surname: 'Example' },
      other_names_used: [ALIAS],
      residence_address: {
        line1: '1 Main St',
        city: 'Tampa',
        state: 'FL',
        county: 'Hillsborough',
      },
      phone: '555-0100',
      email: 'jordan.new@example.test',
      // `lead_source` cleared: omitted from a whole-record PUT is cleared.
    });
    // Never the status, and never a tax ID — neither is this form's to write.
    expect(body).not.toHaveProperty('status');
    expect(body).not.toHaveProperty('tax_id');
  });

  it("puts the server's per-field message on the field it names", async () => {
    signedIn(
      record({
        put: () =>
          jsonResponse(400, {
            error: 'ValidationError',
            fields: { date_of_birth: 'A date of birth cannot be in the future.' },
          }),
      }),
    );
    await loaded();
    const user = userEvent.setup();

    await user.press(screen.getByRole('button', { name: 'Edit details' }));
    await user.press(screen.getByRole('button', { name: 'Save changes' }));

    expect(await screen.findByText('A date of birth cannot be in the future.')).toBeTruthy();
    expect(screen.getByText('Some answers need attention.')).toBeTruthy();
  });

  it('shows a prospect the funnel and stages them with a PUT (issue #355)', async () => {
    const { fetchMock } = signedIn();
    await loaded();
    expect(screen.getByText('Prospect')).toBeTruthy();

    const user = userEvent.setup();
    await user.press(screen.getByRole('combobox', { name: 'Funnel stage' }));
    await user.press(await screen.findByRole('option', { name: 'Consultation scheduled' }));

    await waitFor(() => {
      expect(bodiesOf(fetchMock, 'PUT', '/prospect-stage')).toEqual([
        { prospect_stage: 'consultation_scheduled' },
      ]);
    });
  });

  it('shows no funnel for a client already retained', async () => {
    signedIn(record({ client: { ...JORDAN, first_retained_at: '2026-07-20' } }));
    await loaded();
    expect(screen.queryByRole('combobox', { name: 'Funnel stage' })).toBeNull();
    expect(screen.queryByText('Prospect')).toBeNull();
  });

  it('archives only after asking, and restores without', async () => {
    const { fetchMock } = signedIn();
    await loaded();
    const user = userEvent.setup();

    await user.press(screen.getByRole('button', { name: 'Archive' }));
    expect(await screen.findByText('Archive Jordan Example?')).toBeTruthy();
    expect(bodiesOf(fetchMock, 'PUT', '/status')).toEqual([]);

    // The dialog's own Archive — the second of two with that name.
    const archives = screen.getAllByRole('button', { name: 'Archive' });
    await user.press(archives[archives.length - 1]!);

    expect(await screen.findByRole('button', { name: 'Restore' })).toBeTruthy();
    // No new case for an archived client: the server refuses, so it is not offered.
    expect(screen.queryByRole('button', { name: 'Start a case for this client' })).toBeNull();

    await user.press(screen.getByRole('button', { name: 'Restore' }));
    expect(await screen.findByRole('button', { name: 'Archive' })).toBeTruthy();
    expect(bodiesOf(fetchMock, 'PUT', '/status')).toEqual([
      { status: 'archived' },
      { status: 'active' },
    ]);
  });

  it('is read-only at `view_only`: no edit, no archive — but a case can still be started', async () => {
    signedIn(record(), member({ clients: 'view_only' }));
    await loaded();

    expect(screen.queryByRole('button', { name: 'Edit details' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Archive' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Start a case for this client' })).toBeTruthy();
  });

  it('shows no cases and offers none without `cases` access', async () => {
    const { fetchMock } = signedIn(record(), member({ cases: 'hidden' }));
    await loaded();

    expect(screen.queryByRole('heading', { name: 'Cases' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Start a case for this client' })).toBeNull();
    const urls = fetchMock.mock.calls.map(([url]) => String(url));
    expect(urls.some((url) => url.endsWith('/cases'))).toBe(false);
  });

  it('answers another firm’s client, or none, as not found', async () => {
    signedIn(record({ client: null }));

    expect(await screen.findByRole('heading', { name: 'Client not found' })).toBeTruthy();
  });

  it('explains itself to a colleague whose firm has not granted `clients`', async () => {
    signedIn(record(), member({ clients: 'hidden' }));

    expect(await screen.findByText(/not given you access to its client list/)).toBeTruthy();
  });
});
