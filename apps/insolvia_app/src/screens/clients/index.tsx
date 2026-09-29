import { permits } from '@insolvia-ai/api-client';
import type { FirmClient, FirmClientCase, FirmMembership } from '@insolvia-ai/api-client';
import {
  Badge,
  Button,
  EmptyState,
  Field,
  Input,
  Table,
  Toggle,
  ToggleGroup,
} from '@insolvia-ai/design-system';
import { Link, useRouter } from 'expo-router';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useApi } from '@/api/use-api';
import { AppShell } from '@/components/app-shell';
import { CASE_STATUS_LABEL } from '@/components/case-status';
import { sortName } from '@/components/client-names';
import { Heading } from '@/components/heading';
import { fontSizes, spacing, useTheme } from '@/theme';

type ListState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly clients: readonly FirmClient[] }
  | { readonly kind: 'error' };

type Show = 'active' | 'archived' | 'all';

/** Rows rendered before "Show more" — and so the most per-row case reads in flight at once. */
const PAGE = 25;

/** Case links a row shows before sending the reader on to the record for the rest. */
const ROW_CASES = 3;

/**
 * The words a client can be found by: their names (including the middle
 * name and every other name they have used) and their home city. Case
 * numbers are the issue's third search key and are not here yet — a case has
 * no number until it is filed, which is #355's lifecycle.
 */
function haystack(client: FirmClient): string {
  const { given, middle, surname } = client.name;
  const aliases = (client.other_names_used ?? []).flatMap((other) => [
    other.given,
    other.middle,
    other.surname,
  ]);
  return [given, middle, surname, ...aliases, client.residence_address?.city]
    .filter((part): part is string => typeof part === 'string' && part !== '')
    .join(' ')
    .toLowerCase();
}

/** Every word of the query appears somewhere — "jor exa" finds Jordan Example. */
function matches(client: FirmClient, query: string): boolean {
  const words = query
    .toLowerCase()
    .split(/\s+/)
    .filter((word) => word !== '');
  if (words.length === 0) return true;
  const text = haystack(client);
  return words.every((word) => text.includes(word));
}

/** "Tampa, FL", or whichever half exists — where a person is, at a glance. */
function place(client: FirmClient): string {
  const { city, state } = client.residence_address ?? {};
  return [city, state].filter((part) => part !== undefined && part !== '').join(', ');
}

/**
 * `/clients` — the firm's client directory (ADR 0022 / #354).
 *
 * ONE ROW PER CLIENT. A joint case is two clients on one matter, so it
 * appears on BOTH rows, each pointing at the same case — the row model the
 * ADR's `by-client` index was built to serve.
 *
 * A ROW'S CASES ARE ONLY THE ONES THE CALLER MAY SEE, and the row says
 * nothing about the rest. `GET /v1/firm/clients/<id>/cases` filters per case
 * (ADR 0009's reach) and never counts what it left out, because "3 cases"
 * where you can open one is exactly the enumeration a 404 on the other two
 * exists to hide. Each visible row asks for its own — the directory endpoint
 * carries no case facts — which is why the table renders a page at a time:
 * at most {@link PAGE} reads in flight, each remembered for the visit so
 * searching back and forth does not ask twice. Without `cases` at
 * `view_only` there is nothing the caller could open, and the column is not
 * drawn at all.
 *
 * SEARCH AND FILTER ARE LOCAL. `GET /v1/firm/clients` is the whole
 * directory, archived included, already ordered by surname — so the search
 * box and the Active / Archived / All switch narrow a list already in hand,
 * with no request per keystroke.
 *
 * Gated on `clients` (ADR 0022): `hidden` has no nav entry and gets the
 * explanation below; `view_only` sees everything and changes nothing — no
 * "Add client"; `add_edit` adds.
 */
export function ClientList({ membership }: { membership: FirmMembership }) {
  const theme = useTheme();
  const router = useRouter();
  const { call } = useApi();

  const [list, setList] = useState<ListState>({ kind: 'loading' });
  const [query, setQuery] = useState('');
  const [show, setShow] = useState<Show>('active');
  const [limit, setLimit] = useState(PAGE);

  const mayView = permits(membership.permissions.clients, 'view_only');
  const mayAdd = permits(membership.permissions.clients, 'add_edit');
  const maySeeCases = permits(membership.permissions.cases, 'view_only');

  // A client's reachable cases, by id — read once per visit, shared by every
  // render of that client's row.
  const casesById = useRef(new Map<string, Promise<readonly FirmClientCase[] | null>>());
  const casesFor = useCallback(
    (clientId: string) => {
      let pending = casesById.current.get(clientId);
      if (pending === undefined) {
        pending = call((client) => client.listFirmClientCases(clientId)).then(
          (result) => (result.ok ? result.value : null),
          () => null,
        );
        casesById.current.set(clientId, pending);
      }
      return pending;
    },
    [call],
  );

  useEffect(() => {
    if (!mayView) return;
    let cancelled = false;
    void (async () => {
      try {
        const result = await call((client) => client.listFirmClients());
        if (result.ok && !cancelled) setList({ kind: 'ready', clients: result.value });
      } catch {
        if (!cancelled) setList({ kind: 'error' });
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [call, mayView]);

  const visible = useMemo(
    () =>
      list.kind === 'ready'
        ? list.clients.filter(
            (client) => (show === 'all' || client.status === show) && matches(client, query),
          )
        : [],
    [list, query, show],
  );

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };

  if (!mayView) {
    return (
      <AppShell>
        <Heading level={1}>Clients</Heading>
        <Text style={[styles.body, muted]}>
          Your firm has not given you access to its client list. Ask one of your firm’s
          administrators if you need it.
        </Text>
      </AppShell>
    );
  }

  const addClient = () => router.push('/clients/new');
  const directoryEmpty = list.kind === 'ready' && list.clients.length === 0;

  return (
    <AppShell>
      <Heading level={1}>Clients</Heading>
      <Text style={[styles.body, muted]}>
        Everyone your firm acts for, with or without a case — start here to find a person, or to add
        one and open their case.
      </Text>

      {mayAdd ? (
        <View style={styles.actions}>
          <Button size="lg" onPress={addClient}>
            Add client
          </Button>
        </View>
      ) : null}

      {list.kind === 'loading' ? (
        <Text aria-live="polite" style={[styles.body, muted]}>
          Loading your clients…
        </Text>
      ) : list.kind === 'error' ? (
        <Text
          aria-live="assertive"
          style={[styles.body, { color: theme.colors.danger, fontFamily: theme.typography.body }]}
        >
          Could not load your client list.
        </Text>
      ) : directoryEmpty ? (
        <EmptyState.Root>
          <Heading level={2} size="body">
            No clients yet
          </Heading>
          <EmptyState.Description>
            {mayAdd
              ? 'Add the first person your firm is helping. You can open their case straight after.'
              : 'Nobody has been added to your firm’s client list yet.'}
          </EmptyState.Description>
        </EmptyState.Root>
      ) : (
        <>
          <View style={styles.controls}>
            <View style={styles.search}>
              <Field.Root name="search">
                <Field.Label>Search clients</Field.Label>
                <Input
                  type="search"
                  value={query}
                  onValueChange={(next) => {
                    setQuery(next);
                    setLimit(PAGE);
                  }}
                  autoCorrect={false}
                  placeholder="Name or city"
                />
              </Field.Root>
            </View>
            <ToggleGroup.Root
              aria-label="Show clients"
              value={[show]}
              // Single-select, and never empty: pressing the pressed one
              // again reports `[]`, which keeps the current choice.
              onValueChange={(next) => {
                const picked = next[0] as Show | undefined;
                if (picked !== undefined) {
                  setShow(picked);
                  setLimit(PAGE);
                }
              }}
              style={styles.toggles}
            >
              <Toggle value="active">Active</Toggle>
              <Toggle value="archived">Archived</Toggle>
              <Toggle value="all">All</Toggle>
            </ToggleGroup.Root>
          </View>

          <Text aria-live="polite" style={[styles.count, muted]}>
            {visible.length === 1 ? '1 client' : `${visible.length} clients`}
          </Text>

          {visible.length === 0 ? (
            <Text style={[styles.body, muted]}>
              {query.trim() === ''
                ? show === 'archived'
                  ? 'No archived clients.'
                  : 'No active clients.'
                : `No ${show === 'all' ? '' : `${show} `}clients match “${query.trim()}”.`}
            </Text>
          ) : (
            <Table.Root dense>
              <Table.Head>
                <Table.Row>
                  <Table.HeaderCell>Client</Table.HeaderCell>
                  {maySeeCases ? <Table.HeaderCell>Cases</Table.HeaderCell> : null}
                  <Table.HeaderCell width={160}>Lives in</Table.HeaderCell>
                  <Table.HeaderCell width={110}>Status</Table.HeaderCell>
                </Table.Row>
              </Table.Head>
              <Table.Body>
                {visible.slice(0, limit).map((client) => (
                  <Table.Row key={client.id}>
                    <Table.Cell>
                      <Link
                        href={`/clients/${client.id}`}
                        style={[
                          styles.link,
                          { color: theme.colors.primary, fontFamily: theme.typography.body },
                        ]}
                      >
                        {sortName(client)}
                      </Link>
                    </Table.Cell>
                    {maySeeCases ? (
                      <Table.Cell>
                        <RowCases clientId={client.id} load={casesFor} />
                      </Table.Cell>
                    ) : null}
                    <Table.Cell width={160}>
                      <Text style={[styles.meta, muted]}>{place(client) || '—'}</Text>
                    </Table.Cell>
                    <Table.Cell width={110}>
                      <Badge intent={client.status === 'active' ? 'success' : 'neutral'} size="sm">
                        {client.status === 'active' ? 'Active' : 'Archived'}
                      </Badge>
                    </Table.Cell>
                  </Table.Row>
                ))}
              </Table.Body>
            </Table.Root>
          )}

          {visible.length > limit ? (
            <View style={styles.actions}>
              <Button size="lg" intent="secondary" onPress={() => setLimit(limit + PAGE)}>
                {`Show more (${visible.length - limit} more)`}
              </Button>
            </View>
          ) : null}
        </>
      )}
    </AppShell>
  );
}

/**
 * One row's cases: a link to each (up to {@link ROW_CASES}), then the record
 * for the rest. Only reachable cases ever arrive, so "No cases" means "none
 * you can see" — the honest reading, and the only one the API allows.
 *
 * The link's accessible name is the case list's own — "Chapter N case in
 * <district>, opened <date>" — so a case is announced the same way wherever
 * it is listed, and the e2e suite finds it the same way from here.
 */
function RowCases({
  clientId,
  load,
}: {
  clientId: string;
  load: (clientId: string) => Promise<readonly FirmClientCase[] | null>;
}) {
  const theme = useTheme();
  const [cases, setCases] = useState<readonly FirmClientCase[] | null | 'loading'>('loading');

  useEffect(() => {
    let cancelled = false;
    void load(clientId).then((found) => {
      if (!cancelled) setCases(found);
    });
    return () => {
      cancelled = true;
    };
  }, [clientId, load]);

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  if (cases === 'loading') return <Text style={[styles.meta, muted]}>…</Text>;
  if (cases === null) return <Text style={[styles.meta, muted]}>Could not load cases</Text>;
  if (cases.length === 0) return <Text style={[styles.meta, muted]}>No cases</Text>;

  return (
    <View style={styles.rowCases}>
      {cases.slice(0, ROW_CASES).map(({ case: matter }) => (
        <Link
          key={matter.id}
          href={`/cases/${matter.id}`}
          aria-label={`Chapter ${matter.chapter} case in ${matter.district}, opened ${matter.createdAt.slice(0, 10)}`}
          style={[
            styles.caseLink,
            { color: theme.colors.primary, fontFamily: theme.typography.body },
          ]}
        >
          {`Ch. ${matter.chapter} · ${CASE_STATUS_LABEL[matter.status]}`}
        </Link>
      ))}
      {cases.length > ROW_CASES ? (
        <Link
          href={`/clients/${clientId}`}
          style={[
            styles.caseLink,
            { color: theme.colors.primary, fontFamily: theme.typography.body },
          ]}
        >
          {`+${cases.length - ROW_CASES} more`}
        </Link>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  actions: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.sm },
  body: { fontSize: fontSizes.body, lineHeight: fontSizes.body * 1.5 },
  caseLink: { fontSize: fontSizes.label, lineHeight: 44 },
  controls: {
    alignItems: 'flex-end',
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.md,
  },
  count: { fontSize: fontSizes.label },
  link: {
    fontSize: fontSizes.label,
    fontWeight: '600',
    // 44dp, the WCAG 2.5.5 target size — a text link is no exception.
    lineHeight: 44,
  },
  meta: { fontSize: fontSizes.label },
  rowCases: { columnGap: spacing.md, flexDirection: 'row', flexWrap: 'wrap' },
  search: { flexBasis: 280, flexGrow: 1 },
  toggles: { flexDirection: 'row' },
});
