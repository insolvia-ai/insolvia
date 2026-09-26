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
const PETITION_ID = '00000000-0000-4000-8000-0000000000p1';

const SAVED_PETITION = {
  id: PETITION_ID,
  case_id: CASE_ID,
  created_at: '2026-09-01T10:00:00.000000Z',
  updated_at: '2026-09-01T10:00:00.000000Z',
  provenance: { fee_handling: { source: 'staff_typed' } },
  fee_handling: 'waiver',
};

/** One stubbed API route: a method, a URL fragment, and its answer. */
interface Route {
  readonly method: string;
  readonly fragment: string;
  readonly respond: () => Response;
}

/**
 * `/cases/<id>/petition` (issue #342), rendered through the real case route:
 * sign in, load the petition body, its two related lists' sections, and the
 * signer block, then save.
 *
 * `routeFetch` from `@/session/testing` dispatches on the URL alone; this
 * suite carries its own dispatcher, the same shape `collection-editor.test.tsx`
 * uses, because several of these endpoints answer differently per method on
 * one URL.
 */
describe('the petition screen', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  function signedIn(routes: readonly Route[]) {
    const fetchMock = jest.fn((url: string, init?: RequestInit) => {
      if (url.includes('/oauth2/token')) return Promise.resolve(tokenEndpointResponse());
      const method = init?.method ?? 'GET';
      const match = routes.find((route) => route.method === method && url.includes(route.fragment));
      if (match === undefined) {
        if (method === 'GET' && url.endsWith(`/v1/cases/${CASE_ID}`)) {
          return Promise.resolve(jsonResponse(200, caseBody(CASE_ID)));
        }
        return Promise.reject(new Error(`unexpected ${method} ${url}`));
      }
      return Promise.resolve(match.respond());
    });
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    renderRouter('src/app', { initialUrl: `/cases/${CASE_ID}/petition` });
    return fetchMock;
  }

  /** The body of the last request matching `method` + `fragment`, parsed. */
  function lastBody(
    fetchMock: ReturnType<typeof signedIn>,
    method: string,
    fragment: string,
  ): Record<string, unknown> {
    const calls = fetchMock.mock.calls.filter(
      ([url, init]) => url.includes(fragment) && (init?.method ?? 'GET') === method,
    );
    return JSON.parse(String(calls[calls.length - 1]?.[1]?.body ?? '{}')) as Record<
      string,
      unknown
    >;
  }

  const emptyList = (collection: string): Route => ({
    method: 'GET',
    fragment: `/v1/cases/${CASE_ID}/${collection}`,
    respond: () => jsonResponse(200, { [collection]: [] }),
  });

  /** Every read the screen makes on mount, empty by default so a test states
   * only the endpoint it is about. */
  function baseRoutes(over: readonly Route[] = []): readonly Route[] {
    const defaults = [
      emptyList('debtors'),
      emptyList('petitions'),
      emptyList('creditors'),
      emptyList('prior_cases'),
      emptyList('related_cases'),
      emptyList('sole_proprietorships'),
      emptyList('filing_professionals'),
      {
        method: 'GET',
        fragment: `/v1/cases/${CASE_ID}/summary`,
        respond: () =>
          jsonResponse(200, {
            readyToFile: false,
            problems: [],
            totals: {
              realEstate: '0.00',
              personalProperty: '0.00',
              assets: '0.00',
              totalExempt: '0.00',
              totalNonExempt: '0.00',
              secured: '0.00',
              priorityUnsecured: '0.00',
              nonpriorityUnsecured: '0.00',
              liabilities: '0.00',
              monthlyIncome: '0.00',
              monthlyExpenses: '0.00',
              monthlyExcess: '0.00',
            },
            liens: { claims: [], assets: [] },
          }),
      },
    ];
    // A test's own route replaces the default for the same METHOD and
    // fragment, not merely the same fragment — the POST route this suite
    // adds to answer a save must not shadow the GET default that loads the
    // list in the first place.
    return [
      ...over,
      ...defaults.filter(
        (base) =>
          !over.some((route) => route.fragment === base.fragment && route.method === base.method),
      ),
    ];
  }

  beforeEach(() => {
    mockAuthConfig = TEST_AUTH_CONFIG;
    browser = installFakeBrowser();
    writeRefreshToken('stored-refresh-token');
  });

  afterEach(() => {
    globalThis.fetch = realFetch;
    browser.restore();
  });

  it('renders the petition, its repeating lists, and the signer block', async () => {
    signedIn(baseRoutes());

    expect(await screen.findByRole('heading', { name: 'Petition (B101)' })).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Prior bankruptcy cases' })).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Related cases' })).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Sole proprietorships' })).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Who signs' })).toBeTruthy();
  });

  it('saves the petition body whole, with staff_typed provenance for what is filled in', async () => {
    const fetchMock = signedIn(
      baseRoutes([
        {
          method: 'POST',
          fragment: `/v1/cases/${CASE_ID}/petitions`,
          respond: () => jsonResponse(201, SAVED_PETITION),
        },
      ]),
    );
    const user = userEvent.setup();

    await screen.findByRole('heading', { name: 'Petition (B101)' });
    await user.press(screen.getByRole('combobox', { name: 'How will the filing fee be handled?' }));
    await user.press(screen.getByRole('option', { name: 'Waiver' }));
    await user.press(screen.getByRole('button', { name: 'Save petition' }));

    // The two dollar estimates derive to "0_50000" from the summary's zero
    // totals and are saved along with the fee choice — unlike the creditor
    // count, whose derivation is undefined at zero (the form's lowest
    // bracket starts at 1), so it is absent here.
    await waitFor(() =>
      expect(lastBody(fetchMock, 'POST', '/petitions')).toEqual({
        fee_handling: 'waiver',
        estimated_assets: '0_50000',
        estimated_liabilities: '0_50000',
        provenance: {
          fee_handling: { source: 'staff_typed' },
          estimated_assets: { source: 'staff_typed' },
          estimated_liabilities: { source: 'staff_typed' },
        },
      }),
    );
  });

  it('prefills the signer block from a colleague’s firm signature block, and saves it as staff-typed', async () => {
    // Issue #360: the directory carries each attorney's standing block; the
    // button copies it onto the form and the preparer still saves — with the
    // same provenance as anything they typed, because they confirmed it.
    const fetchMock = signedIn(
      baseRoutes([
        {
          method: 'GET',
          fragment: '/v1/firm/directory',
          respond: () =>
            jsonResponse(200, {
              people: [
                {
                  subject: '00000000-0000-4000-8000-00000000a11c',
                  firstName: 'Alice',
                  lastName: 'Attorney',
                  displayName: 'Alice Attorney',
                  role: 'attorney',
                  signatureBlock: {
                    bar_number: '0123456',
                    bar_state: 'FL',
                    firm_name: 'Example & Partners',
                    address: { line1: '1 Main St', city: 'Tampa', state: 'FL' },
                    phone: '813-555-0100',
                    email: 'alice@example.test',
                  },
                },
                {
                  subject: '00000000-0000-4000-8000-00000000b0b0',
                  firstName: 'Bob',
                  lastName: 'Paralegal',
                  displayName: 'Bob Paralegal',
                  role: 'paralegal',
                  signatureBlock: null,
                },
              ],
            }),
        },
        {
          method: 'POST',
          fragment: `/v1/cases/${CASE_ID}/filing_professionals`,
          respond: () =>
            jsonResponse(201, {
              id: 'fp-1',
              case_id: CASE_ID,
              created_at: '2026-09-01T10:00:00.000000Z',
              updated_at: '2026-09-01T10:00:00.000000Z',
              provenance: {},
              role: 'attorney',
              bar_number: '0123456',
            }),
        },
      ]),
    );
    const user = userEvent.setup();

    await screen.findByRole('heading', { name: 'Who signs' });
    // Only Alice has a block, so she is the one option and the default.
    await screen.findByText('Alice Attorney');
    await user.press(screen.getByRole('button', { name: 'Use firm default' }));

    expect(screen.getByDisplayValue('0123456')).toBeTruthy();
    expect(screen.getByDisplayValue('Example & Partners')).toBeTruthy();
    expect(screen.getByDisplayValue('Tampa')).toBeTruthy();
    expect(screen.getByText(/Prefilled from Alice Attorney/)).toBeTruthy();

    await user.press(screen.getByRole('button', { name: 'Save signer' }));

    await waitFor(() => {
      const body = lastBody(fetchMock, 'POST', '/filing_professionals');
      expect(body.bar_number).toBe('0123456');
      expect(body.bar_state).toBe('FL');
      expect(body.name).toEqual({ given: 'Alice', surname: 'Attorney' });
      expect(body.address).toEqual({ line1: '1 Main St', city: 'Tampa', state: 'FL' });
      expect((body.provenance as Record<string, unknown>).bar_number).toEqual({
        source: 'staff_typed',
      });
    });
  });

  it('derives the estimated-creditors band from the case’s own creditor count', async () => {
    signedIn(
      baseRoutes([
        {
          method: 'GET',
          fragment: `/v1/cases/${CASE_ID}/creditors`,
          respond: () =>
            jsonResponse(200, {
              creditors: Array.from({ length: 60 }, (_unused, index) => ({
                id: `creditor-${index}`,
                case_id: CASE_ID,
                created_at: '2026-09-01T10:00:00.000000Z',
                updated_at: '2026-09-01T10:00:00.000000Z',
                provenance: {},
              })),
            }),
        },
      ]),
    );

    await screen.findByRole('heading', { name: 'Petition (B101)' });
    // 60 creditors falls in the "50 99" bracket (`labelize('50_99')`); shown
    // disabled until the attorney checks "Enter manually" — see
    // PetitionFields' EstimateField.
    await waitFor(() => expect(screen.getByText('50 99')).toBeTruthy());
  });

  it('lets the estimated-creditors band be overridden by hand', async () => {
    signedIn(baseRoutes());
    const user = userEvent.setup();

    await screen.findByRole('heading', { name: 'Petition (B101)' });
    await user.press(
      screen.getByRole('checkbox', { name: 'Enter estimated number of creditors manually' }),
    );
    await user.press(screen.getByRole('combobox', { name: 'Estimated number of creditors' }));
    await user.press(screen.getByRole('option', { name: '1 49' }));

    expect(screen.getByText('1 49')).toBeTruthy();
  });
});
