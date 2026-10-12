import { screen, userEvent, waitFor } from '@testing-library/react-native';
import { renderRouter } from 'expo-router/testing-library';

import type { AuthConfig } from '@/config/environment';
import { writeRefreshToken } from '@/session';
import {
  withCaseShell,
  installFakeBrowser,
  jsonResponse,
  routeFetch,
  TEST_AUTH_CONFIG,
  tokenEndpointResponse,
} from '@/session/testing';
import type { CaseOverrides, FakeBrowser } from '@/session/testing';

let mockAuthConfig: AuthConfig | null = null;

jest.mock('@/config/environment', () => ({
  ...jest.requireActual('@/config/environment'),
  resolveAuthConfig: () => mockAuthConfig,
}));

const CASE_ID = '00000000-0000-4000-8000-0000000000c1';

const SAVED = {
  id: '00000000-0000-4000-8000-0000000000d1',
  case_id: CASE_ID,
  filing_role: 'debtor_1',
  created_at: '2026-08-05T10:00:00.000000Z',
  updated_at: '2026-08-05T10:00:00.000000Z',
  name: { given: 'Ada', surname: 'Lovelace' },
  provenance: {
    'name.given': { source: 'staff_typed' },
    'name.surname': { source: 'staff_typed' },
  },
};

const CLIENT_ID = '00000000-0000-4000-8000-0000000c11a0';

/**
 * Debtor 1 as a case is opened (ADR 0022): linked to a client and holding
 * nothing yet — a client in the directory with no details copied. Every
 * case has one from the moment it is opened, so it is the starting point
 * for the tests that type into an intake.
 */
const OPENED = {
  id: '00000000-0000-4000-8000-0000000000d1',
  case_id: CASE_ID,
  filing_role: 'debtor_1',
  created_at: '2026-08-05T10:00:00.000000Z',
  updated_at: '2026-08-05T10:00:00.000000Z',
  client_id: CLIENT_ID,
  differs_from_client: [],
  provenance: {},
};

/**
 * `/cases/<id>/intake` — the structured intake (issue 8.5).
 *
 * Rendered through the **real router**, like the cases screen's suite, so a
 * route file that moved or stopped compiling fails here.
 *
 * What is asserted is nearly all about not losing work, because that is the one
 * thing this screen must never do: that a half-finished record comes back when
 * you return to it, that every populated field leaves with provenance attached
 * (the API rejects the request otherwise), and that a server's per-field
 * message reaches the field it belongs to.
 */
describe('the intake screen', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  /** Signs in and renders the intake, returning the fetch mock so a test can
   * read the request bodies — which is what most of these need. */
  function signedIn(
    handlers: Readonly<Record<string, () => Response>>,
    overrides: CaseOverrides = {},
  ) {
    const route = routeFetch(
      withCaseShell(CASE_ID, { '/oauth2/token': tokenEndpointResponse, ...handlers }, overrides),
    );
    const fetchMock = jest.fn((url: string, _init?: RequestInit) => route(url));
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    renderRouter('src/app', { initialUrl: `/cases/${CASE_ID}/intake` });
    return fetchMock;
  }

  /** The body of the last PUT to a debtor, parsed. */
  function lastSave(fetchMock: ReturnType<typeof signedIn>): Record<string, unknown> {
    const puts = fetchMock.mock.calls.filter(
      ([url, init]) => url.includes('/debtors/') && init?.method === 'PUT',
    );
    return JSON.parse(String(puts[puts.length - 1]?.[1]?.body ?? '{}'));
  }

  const justOpened = () => jsonResponse(200, { debtors: [OPENED] });
  const savedOk = () => jsonResponse(201, SAVED);

  beforeEach(() => {
    mockAuthConfig = TEST_AUTH_CONFIG;
    browser = installFakeBrowser();
    writeRefreshToken('stored-refresh-token');
  });

  afterEach(() => {
    globalThis.fetch = realFetch;
    browser.restore();
  });

  it('shows a half-finished record when you come back to it', async () => {
    signedIn({ [`/v1/cases/${CASE_ID}/debtors`]: () => jsonResponse(200, { debtors: [SAVED] }) });

    expect(await screen.findByDisplayValue('Ada')).toBeTruthy();
    expect(screen.getByDisplayValue('Lovelace')).toBeTruthy();
  });

  it('sends provenance for every populated field', async () => {
    // The API rejects a body where a populated field has no provenance entry,
    // so a save that forgot this would 400 on every keystroke.
    const fetchMock = signedIn({
      [`/v1/cases/${CASE_ID}/debtors/debtor_1`]: savedOk,
      [`/v1/cases/${CASE_ID}/debtors`]: justOpened,
    });

    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('First name'), 'Ada');

    await waitFor(() => expect(lastSave(fetchMock).name).toEqual({ given: 'Ada' }));
    expect(lastSave(fetchMock).provenance).toEqual({
      'name.given': { source: 'staff_typed' },
    });
  });

  it('never sends back the client link or the divergence, which the server owns', async () => {
    // ADR 0022: a debtor copied from a firm client carries `client_id` and a
    // computed `differs_from_client`. Neither is an answer on this form; left
    // in the body they would be sent, and walked into "typed" provenance.
    const linked = {
      ...SAVED,
      client_id: '00000000-0000-4000-8000-0000000c11a0',
      differs_from_client: [],
    };
    const fetchMock = signedIn({
      [`/v1/cases/${CASE_ID}/debtors/debtor_1`]: () => jsonResponse(200, linked),
      [`/v1/cases/${CASE_ID}/debtors`]: () => jsonResponse(200, { debtors: [linked] }),
    });

    const user = userEvent.setup();
    const first = await screen.findByDisplayValue('Ada');
    await user.type(first, 'm');

    await waitFor(() =>
      expect(lastSave(fetchMock).name).toEqual({ given: 'Adam', surname: 'Lovelace' }),
    );
    const body = lastSave(fetchMock);
    expect('client_id' in body).toBe(false);
    expect('differs_from_client' in body).toBe(false);
    expect(Object.keys(body.provenance as object).sort()).toEqual(['name.given', 'name.surname']);
  });

  it('puts a server field message on the field it belongs to', async () => {
    signedIn({
      [`/v1/cases/${CASE_ID}/debtors/debtor_1`]: () =>
        jsonResponse(400, {
          error: 'validation failed',
          fields: { 'name.given': 'Must be a single line.' },
        }),
      [`/v1/cases/${CASE_ID}/debtors`]: justOpened,
    });

    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('First name'), 'Ada');

    expect(await screen.findByText('Must be a single line.')).toBeTruthy();
  });

  it('offers each debtor as a separate record, not a second column', async () => {
    // A joint filing is two debtor records, and a non-filing spouse may appear
    // on 106I without filing at all.
    signedIn({ [`/v1/cases/${CASE_ID}/debtors`]: justOpened });

    expect(await screen.findByRole('tab', { name: 'Debtor 1' })).toBeTruthy();
    expect(screen.getByRole('tab', { name: 'Debtor 2' })).toBeTruthy();
    expect(screen.getByRole('tab', { name: 'Non-filing spouse' })).toBeTruthy();
  });

  it('gives a new alias row an id the API will accept', async () => {
    // Without a client-minted id the API refuses the row outright: it cannot be
    // given provenance otherwise, and the request becomes unsatisfiable.
    const fetchMock = signedIn({
      [`/v1/cases/${CASE_ID}/debtors/debtor_1`]: savedOk,
      [`/v1/cases/${CASE_ID}/debtors`]: justOpened,
    });

    const user = userEvent.setup();
    await user.press(await screen.findByText('Add another name'));
    await user.type(await screen.findByLabelText('Business name'), 'Byron and Co');

    await waitFor(() => expect(lastSave(fetchMock).other_names_used).toBeDefined());
    const aliases = lastSave(fetchMock).other_names_used as { id: string }[];
    expect(aliases[0]?.id).toMatch(/^[A-Za-z0-9_-]+$/);
  });

  describe('the tax id (issue 13.12 / #382)', () => {
    // 987-65-4321 is from the SSA's never-issued advertising block — the
    // fixture value the API accepts on purpose. This repo is public.
    const withTaxId = {
      ...SAVED,
      tax_id: { kind: 'ssn', last_four: '4321' },
      provenance: { ...SAVED.provenance, tax_id: { source: 'staff_typed' } },
    };

    it('collects the number masked, with a reveal toggle', async () => {
      signedIn({ [`/v1/cases/${CASE_ID}/debtors`]: justOpened });

      const number = await screen.findByLabelText('Number');
      expect(number.props.secureTextEntry).toBe(true);
      expect(screen.getByRole('button', { name: 'Show number' })).toBeTruthy();
    });

    it('sends a typed number in full, with one provenance entry for the whole field', async () => {
      const fetchMock = signedIn({
        [`/v1/cases/${CASE_ID}/debtors/debtor_1`]: () => jsonResponse(201, withTaxId),
        [`/v1/cases/${CASE_ID}/debtors`]: justOpened,
      });

      const user = userEvent.setup();
      await user.press(await screen.findByRole('combobox', { name: 'Kind of number' }));
      await user.press(await screen.findByRole('option', { name: 'Social Security number' }));
      await user.type(screen.getByLabelText('Number'), '987-65-4321');

      await waitFor(() =>
        expect(lastSave(fetchMock).tax_id).toEqual({ kind: 'ssn', value: '987-65-4321' }),
      );
      expect(lastSave(fetchMock).provenance).toEqual({ tax_id: { source: 'staff_typed' } });
    });

    it('shows only the last four once stored, and echoes it back on the next save', async () => {
      // The screen never holds the number after a save; the echo is what
      // tells the API to keep what it stored when something else changes.
      const fetchMock = signedIn({
        [`/v1/cases/${CASE_ID}/debtors/debtor_1`]: () => jsonResponse(200, withTaxId),
        [`/v1/cases/${CASE_ID}/debtors`]: () => jsonResponse(200, { debtors: [withTaxId] }),
      });

      expect(await screen.findByText(/ending in 4321 is on file/)).toBeTruthy();
      expect(screen.queryByDisplayValue('987-65-4321')).toBeNull();

      const user = userEvent.setup();
      await user.type(screen.getByLabelText('Middle name'), 'Q');

      await waitFor(() =>
        expect(lastSave(fetchMock).name).toEqual({
          given: 'Ada',
          middle: 'Q',
          surname: 'Lovelace',
        }),
      );
      expect(lastSave(fetchMock).tax_id).toEqual({ kind: 'ssn', last_four: '4321' });
      expect(lastSave(fetchMock).provenance).toMatchObject({ tax_id: { source: 'staff_typed' } });
    });

    it('removes a stored number by leaving it out of the save', async () => {
      const fetchMock = signedIn({
        [`/v1/cases/${CASE_ID}/debtors/debtor_1`]: () => jsonResponse(200, SAVED),
        [`/v1/cases/${CASE_ID}/debtors`]: () => jsonResponse(200, { debtors: [withTaxId] }),
      });

      const user = userEvent.setup();
      await user.press(await screen.findByRole('button', { name: 'Remove the stored number' }));

      await waitFor(() =>
        expect(lastSave(fetchMock).name).toEqual({ given: 'Ada', surname: 'Lovelace' }),
      );
      expect('tax_id' in lastSave(fetchMock)).toBe(false);
    });

    it('puts a server message about the number on its field', async () => {
      signedIn({
        [`/v1/cases/${CASE_ID}/debtors/debtor_1`]: () =>
          jsonResponse(400, {
            error: 'validation failed',
            fields: {
              'tax_id.value': 'Not a Social Security number — no SSN begins with 000, 666 or 9.',
            },
          }),
        [`/v1/cases/${CASE_ID}/debtors`]: justOpened,
      });

      const user = userEvent.setup();
      await user.type(await screen.findByLabelText('Number'), '900-12-3456');

      expect(await screen.findByText(/no SSN begins with/)).toBeTruthy();
    });
  });

  it('says that changes save themselves, because there is no save button', async () => {
    signedIn({ [`/v1/cases/${CASE_ID}/debtors`]: justOpened });

    expect(await screen.findByText('Changes save automatically')).toBeTruthy();
  });

  it('offers the community-property section only once a debtor’s state is one of the nine', async () => {
    // §541(a)(2) / Schedule H line 2 — issue #347. No debtor recorded yet, so
    // the section is absent.
    signedIn({ [`/v1/cases/${CASE_ID}/debtors`]: justOpened });

    const user = userEvent.setup();
    await user.press(await screen.findByRole('combobox', { name: 'Section' }));
    expect(screen.queryByRole('option', { name: 'Community property household' })).toBeNull();
  });

  it('offers it once a debtor’s residence is in a community-property state', async () => {
    const inTexas = {
      ...SAVED,
      residence_address: { city: 'Austin', state: 'TX' },
      provenance: { ...SAVED.provenance, 'residence_address.city': { source: 'staff_typed' } },
    };
    signedIn({ [`/v1/cases/${CASE_ID}/debtors`]: () => jsonResponse(200, { debtors: [inTexas] }) });

    const user = userEvent.setup();
    await user.press(await screen.findByRole('combobox', { name: 'Section' }));
    expect(
      await screen.findByRole('option', { name: 'Community property household' }),
    ).toBeTruthy();
  });

  describe('the ways it used to lose work', () => {
    // Every one of these passed the old suite while being broken. They are
    // grouped so the next reader can see what an adversarial pass found.

    it('flushes a pending edit when the screen goes away', async () => {
      // Typing and then navigating within 800ms produced ZERO requests: the
      // cleanup cleared the debounce timer and dropped the keystrokes, with
      // the status region still reading "Changes save automatically".
      const fetchMock = signedIn({
        [`/v1/cases/${CASE_ID}/debtors/debtor_1`]: savedOk,
        [`/v1/cases/${CASE_ID}/debtors`]: justOpened,
      });

      const user = userEvent.setup();
      await user.type(await screen.findByLabelText('First name'), 'Ada');
      screen.unmount();

      await waitFor(() => expect(lastSave(fetchMock).name).toEqual({ given: 'Ada' }));
    });

    it('shows a save error against the debtor it belongs to', async () => {
      // The flush on switching resolves AFTER the switch, so a single shared
      // slot rendered debtor 1's error under debtor 2's empty field — and
      // someone correcting it would type the fix into the wrong record.
      signedIn({
        [`/v1/cases/${CASE_ID}/debtors/debtor_1`]: () =>
          jsonResponse(400, {
            error: 'validation failed',
            fields: { 'name.given': 'Must be a single line.' },
          }),
        [`/v1/cases/${CASE_ID}/debtors`]: justOpened,
      });

      const user = userEvent.setup();
      await user.type(await screen.findByLabelText('First name'), 'Ada');
      await user.press(screen.getByRole('tab', { name: 'Debtor 2' }));

      // Debtor 2 is on screen and is not the record that failed.
      await waitFor(() => expect(screen.queryByText('Must be a single line.')).toBeNull());
      expect(screen.queryByText('Some answers need attention.')).toBeNull();

      // It is still there when you go back to the record it is about.
      await user.press(screen.getByRole('tab', { name: 'Debtor 1' }));
      expect(await screen.findByText('Must be a single line.')).toBeTruthy();
    });

    it('does not re-send a record just because a tab was pressed', async () => {
      // The debounce handle was never nulled, so after the first edit of the
      // session every tab press flushed an identical record again.
      const fetchMock = signedIn({
        [`/v1/cases/${CASE_ID}/debtors/debtor_1`]: savedOk,
        [`/v1/cases/${CASE_ID}/debtors`]: justOpened,
      });

      const user = userEvent.setup();
      await user.type(await screen.findByLabelText('First name'), 'Ada');
      await screen.findByText('Saved');
      const before = fetchMock.mock.calls.filter(([url]) => url.includes('/debtors/')).length;

      await user.press(screen.getByRole('tab', { name: 'Debtor 2' }));
      await user.press(screen.getByRole('tab', { name: 'Debtor 1' }));

      const after = fetchMock.mock.calls.filter(([url]) => url.includes('/debtors/')).length;
      expect(after).toBe(before);
    });

    it('puts an alias error on the alias row', async () => {
      // The fields were keyed by the row's id and the API keys body errors by
      // INDEX, so every alias error rendered nowhere at all — the user saw
      // only the summary, with no field flagged and every save still failing.
      signedIn({
        [`/v1/cases/${CASE_ID}/debtors/debtor_1`]: () =>
          jsonResponse(400, {
            error: 'validation failed',
            fields: { 'other_names_used[0].surname': 'Must be a single line.' },
          }),
        [`/v1/cases/${CASE_ID}/debtors`]: justOpened,
      });

      const user = userEvent.setup();
      await user.press(await screen.findByText('Add another name'));
      await user.type(screen.getAllByLabelText('Last name')[1]!, 'Byron');

      expect(await screen.findByText('Must be a single line.')).toBeTruthy();
    });

    it('reaches the role picker through the design system Tabs', async () => {
      // A `Text` with `onPress` renders as a plain div — react-native-web gives
      // a tabIndex only to the six roles it auto-focuses, and `tab` is not one,
      // so two of the three records were mouse-only (WCAG 2.1.1, Level A).
      // Focusability is a property of the Tabs native leaf and is tested in the
      // design system; what this asserts is that the screen uses it, which is
      // the part that regressed.
      signedIn({ [`/v1/cases/${CASE_ID}/debtors`]: justOpened });

      const user = userEvent.setup();
      await user.press(await screen.findByRole('tab', { name: 'Debtor 2' }));

      // The panel actually follows the tab — a real tab/panel pairing, which
      // the hand-rolled tablist never had at all.
      expect(
        screen.getAllByRole('heading').some((node) => node.props.children === 'Debtor 2'),
      ).toBe(true);
    });
  });

  describe('the linked client (ADR 0022)', () => {
    const COPIED = { source: 'client', client_id: CLIENT_ID };
    const CLIENT = {
      id: CLIENT_ID,
      status: 'active',
      created_at: '2026-08-01T10:00:00.000000Z',
      updated_at: '2026-08-01T10:00:00.000000Z',
      created_by: '00000000-0000-4000-8000-00000000a11c',
      name: { given: 'Ada', surname: 'Lovelace' },
      phone: '555-0100',
    };
    const COPY = {
      ...OPENED,
      name: { given: 'Ada', surname: 'Lovelace' },
      phone: '555-0100',
      provenance: { 'name.given': COPIED, 'name.surname': COPIED, phone: COPIED },
    };
    const DIVERGED = {
      ...COPY,
      phone: '555-0199',
      differs_from_client: ['phone'],
      provenance: { ...COPY.provenance, phone: { source: 'staff_typed' } },
    };
    const clientRecord = () => jsonResponse(200, CLIENT);

    /** Every request to `fragment`, with its method and parsed body. */
    function requests(fetchMock: ReturnType<typeof signedIn>, fragment: string) {
      return fetchMock.mock.calls
        .filter(([url]) => url.includes(fragment))
        .map(([, init]) => ({
          method: init?.method,
          body: init?.body === undefined ? undefined : JSON.parse(String(init.body)),
        }));
    }

    it('keeps client provenance on every copied field the preparer did not touch', async () => {
      const fetchMock = signedIn({
        [`/v1/cases/${CASE_ID}/debtors/debtor_1`]: () => jsonResponse(200, DIVERGED),
        [`/v1/cases/${CASE_ID}/debtors`]: () => jsonResponse(200, { debtors: [COPY] }),
        [`/v1/firm/clients/${CLIENT_ID}`]: clientRecord,
      });

      const user = userEvent.setup();
      await user.type(await screen.findByDisplayValue('555-0100'), '9');

      await waitFor(() => expect(lastSave(fetchMock).phone).toBe('555-01009'));
      expect(lastSave(fetchMock).provenance).toEqual({
        'name.given': COPIED,
        'name.surname': COPIED,
        phone: { source: 'staff_typed' },
      });
    });

    it('names the client and shows where the case and the client disagree', async () => {
      signedIn({
        [`/v1/cases/${CASE_ID}/debtors`]: () => jsonResponse(200, { debtors: [DIVERGED] }),
        // Every part of the name, so the heading is seen to read it the way
        // the client's own record does (`displayName`) — suffix included.
        [`/v1/firm/clients/${CLIENT_ID}`]: () =>
          jsonResponse(200, {
            ...CLIENT,
            name: { given: 'Ada', middle: 'King', surname: 'Lovelace', suffix: 'Jr.' },
          }),
      });

      expect(await screen.findByText('Client: Ada King Lovelace Jr.')).toBeTruthy();
      expect(screen.getByText('One field differs from the client record.')).toBeTruthy();
      expect(await screen.findByText(/This case: 555-0199 · Client: 555-0100/)).toBeTruthy();
    });

    it('says so when the case still matches the client, and offers no act', async () => {
      signedIn({
        [`/v1/cases/${CASE_ID}/debtors`]: () => jsonResponse(200, { debtors: [COPY] }),
        [`/v1/firm/clients/${CLIENT_ID}`]: clientRecord,
      });

      expect(await screen.findByText('Matches the client record.')).toBeTruthy();
      expect(screen.queryByRole('button', { name: 'Re-copy from client' })).toBeNull();
    });

    it('re-copies from the client only after asking, and shows the copy', async () => {
      const fetchMock = signedIn({
        [`/v1/cases/${CASE_ID}/debtors/debtor_1/copy-from-client`]: () => jsonResponse(200, COPY),
        [`/v1/cases/${CASE_ID}/debtors`]: () => jsonResponse(200, { debtors: [DIVERGED] }),
        [`/v1/firm/clients/${CLIENT_ID}`]: clientRecord,
      });

      const user = userEvent.setup();
      await user.press(await screen.findByRole('button', { name: 'Re-copy from client' }));
      expect(requests(fetchMock, '/copy-from-client')).toEqual([]);
      await user.press(await screen.findByRole('button', { name: 'Re-copy' }));

      expect(await screen.findByText('Matches the client record.')).toBeTruthy();
      expect(requests(fetchMock, '/copy-from-client')).toEqual([
        { method: 'POST', body: undefined },
      ]);
      expect(screen.getByDisplayValue('555-0100')).toBeTruthy();
    });

    it('does not offer a re-copy on a filed case, but still updates the client', async () => {
      signedIn(
        {
          [`/v1/cases/${CASE_ID}/debtors`]: () => jsonResponse(200, { debtors: [DIVERGED] }),
          [`/v1/firm/clients/${CLIENT_ID}`]: clientRecord,
        },
        { status: 'filed' },
      );

      const recopy = await screen.findByRole('button', { name: 'Re-copy from client' });
      expect(recopy.props.accessibilityState?.disabled ?? recopy.props['aria-disabled']).toBe(true);
      expect(screen.getByText(/This case is filed/)).toBeTruthy();
      expect(
        screen.getByRole('button', { name: 'Update client from this case' }).props
          .accessibilityState?.disabled,
      ).not.toBe(true);
    });

    it('updates the client from the case after asking', async () => {
      const fetchMock = signedIn({
        [`/v1/cases/${CASE_ID}/debtors/debtor_1/copy-to-client`]: () =>
          jsonResponse(200, { ...DIVERGED, differs_from_client: [] }),
        [`/v1/cases/${CASE_ID}/debtors`]: () => jsonResponse(200, { debtors: [DIVERGED] }),
        [`/v1/firm/clients/${CLIENT_ID}`]: clientRecord,
      });

      const user = userEvent.setup();
      await user.press(await screen.findByRole('button', { name: 'Update client from this case' }));
      await user.press(await screen.findByRole('button', { name: 'Update client' }));

      expect(await screen.findByText('Matches the client record.')).toBeTruthy();
      expect(requests(fetchMock, '/copy-to-client')).toEqual([{ method: 'POST', body: undefined }]);
      // The case's own value stays: it is the client that moved.
      expect(screen.getByDisplayValue('555-0199')).toBeTruthy();
    });

    it('links a client to Debtor 2 instead of minting one from a save', async () => {
      const second = {
        ...OPENED,
        id: '00000000-0000-4000-8000-0000000000d2',
        filing_role: 'debtor_2',
        client_id: '00000000-0000-4000-8000-0000000c11a2',
        name: { given: 'Grace', surname: 'Hopper' },
        provenance: {
          'name.given': { source: 'client', client_id: '00000000-0000-4000-8000-0000000c11a2' },
          'name.surname': { source: 'client', client_id: '00000000-0000-4000-8000-0000000c11a2' },
        },
      };
      const grace = { ...CLIENT, id: second.client_id, name: second.name };
      const fetchMock = signedIn({
        [`/v1/cases/${CASE_ID}/debtors/debtor_2/client`]: () => jsonResponse(201, second),
        [`/v1/cases/${CASE_ID}/debtors`]: () => jsonResponse(200, { debtors: [COPY] }),
        [`/v1/firm/clients/${CLIENT_ID}`]: clientRecord,
        [`/v1/firm/clients/${second.client_id}`]: () => jsonResponse(200, grace),
        '/v1/firm/clients': () => jsonResponse(200, { clients: [CLIENT, grace] }),
      });

      const user = userEvent.setup();
      await user.press(await screen.findByRole('tab', { name: 'Debtor 2' }));

      // No fields until a client is linked: a save cannot create Debtor 2.
      expect(await screen.findByText('Link a client')).toBeTruthy();
      expect(screen.queryByLabelText('First name')).toBeNull();

      await user.press(await screen.findByRole('combobox', { name: 'Client' }));
      // The picker names clients "Surname, Given", as every other client
      // picker does. Debtor 1's client is already on this case, so it is not
      // offered.
      const option = await screen.findByRole('option', { name: 'Hopper, Grace' });
      expect(screen.queryByRole('option', { name: 'Lovelace, Ada' })).toBeNull();
      await user.press(option);
      await user.press(screen.getByRole('button', { name: 'Link client' }));

      expect(await screen.findByDisplayValue('Grace')).toBeTruthy();
      expect(requests(fetchMock, '/debtors/debtor_2/client')).toEqual([
        { method: 'PUT', body: { client_id: second.client_id } },
      ]);
    });

    it('lets a non-filing spouse be entered without a client', async () => {
      signedIn({
        [`/v1/cases/${CASE_ID}/debtors`]: () => jsonResponse(200, { debtors: [COPY] }),
        [`/v1/firm/clients/${CLIENT_ID}`]: clientRecord,
        '/v1/firm/clients': () => jsonResponse(200, { clients: [CLIENT] }),
      });

      const user = userEvent.setup();
      await user.press(await screen.findByRole('tab', { name: 'Non-filing spouse' }));

      expect(await screen.findByText('Link a client (optional)')).toBeTruthy();
      expect(screen.getByLabelText('First name')).toBeTruthy();
    });
  });
});
