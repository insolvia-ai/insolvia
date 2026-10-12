import { permits } from '@insolvia-ai/api-client';
import type { Case, FirmColleague } from '@insolvia-ai/api-client';
import { Badge, Button, Table, Toggle, ToggleGroup } from '@insolvia-ai/design-system';
import { Link, useRouter } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useMembership } from '@/api/me';
import { useApi } from '@/api/use-api';
import { AppShell } from '@/components/app-shell';
import { CASE_STATUS_INTENT, CASE_STATUS_LABEL } from '@/components/case-status';
import { Heading } from '@/components/heading';
import { fontSizes, spacing, useTheme } from '@/theme';

/** Which list: the working list, or the archive (issue #355). */
type CaseView = 'working' | 'archived';

type ListState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly cases: readonly Case[] }
  | { readonly kind: 'error'; readonly message: string };

/**
 * `/cases` — the cases the caller may see, and the way to open another.
 *
 * The form that opens a case used to live here (issue 8.3, then #411's
 * minimal client picker). It moved to `/cases/new` when a case became
 * something opened FOR A CLIENT (ADR 0022 / #354): a client's record and
 * "Add client" start cases too, and a form reached from three places is a
 * page, not a panel on one of them. "New case" is the way in from here.
 *
 * TWO VIEWS (issue #355): the working list, and the archive — the cases the
 * firm has put away, which leave the default list and never the firm's
 * records. The API holds the two apart (`listCases({ archived })`); a deleted
 * case is in neither.
 */
export function Cases() {
  const theme = useTheme();
  const router = useRouter();
  const { call } = useApi();
  const membership = useMembership();

  const [view, setView] = useState<CaseView>('working');
  const [list, setList] = useState<ListState>({ kind: 'loading' });
  // Subject -> name, so `createdBy` renders as a colleague rather than a uuid.
  // Loaded once and separately from the cases: it fails independently, and a
  // directory this screen could not fetch should cost names, not the list.
  const [colleagues, setColleagues] = useState<readonly FirmColleague[]>([]);

  const load = useCallback(async () => {
    try {
      setList({ kind: 'loading' });
      const result = await call((client) => client.listCases({ archived: view === 'archived' }));
      if (result.ok) {
        setList({ kind: 'ready', cases: result.value.cases });
      }
      // !ok means the session ended and useApi already navigated; leaving the
      // screen in `loading` is correct — it is about to unmount.
    } catch {
      setList({ kind: 'error', message: 'Could not load your cases.' });
    }
  }, [call, view]);

  useEffect(() => {
    void load();
  }, [load]);

  useEffect(() => {
    const loadDirectory = async () => {
      try {
        const result = await call((client) => client.listFirmDirectory());
        if (result.ok) {
          setColleagues(result.value);
        }
      } catch {
        // Names are a nicety here; the list is not. Falling back to the
        // subject is worse than a name and much better than an error screen
        // over a case list that loaded perfectly.
      }
    };
    void loadDirectory();
  }, [call]);

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  // A courtesy, never a control: hidden only when the membership is KNOWN not
  // to permit it. `/cases/new` says so itself, and the API refuses regardless.
  const mayOpen = membership == null || permits(membership.permissions.cases, 'add_edit');

  return (
    <AppShell>
      <Heading level={1}>Your cases</Heading>

      {mayOpen ? (
        <View style={styles.actions}>
          {/* size="lg" (48dp): the package's md is 40dp, under the 44dp
              WCAG 2.5.5 target-size floor this app enforces. */}
          <Button size="lg" onPress={() => router.push('/cases/new')}>
            New case
          </Button>
        </View>
      ) : null}

      <ToggleGroup.Root
        aria-label="Show cases"
        value={[view]}
        // Single-select, and never empty: pressing the pressed one again
        // reports `[]`, which keeps the current choice.
        onValueChange={(next) => {
          const picked = next[0] as CaseView | undefined;
          if (picked !== undefined) setView(picked);
        }}
        style={styles.toggles}
      >
        <Toggle value="working">Working</Toggle>
        <Toggle value="archived">Archived</Toggle>
      </ToggleGroup.Root>

      {list.kind === 'ready' ? (
        <CaseList cases={list.cases} colleagues={colleagues} archived={view === 'archived'} />
      ) : (
        <Text
          aria-live={list.kind === 'error' ? 'assertive' : 'polite'}
          style={[styles.body, muted]}
        >
          {list.kind === 'loading' ? 'Loading your cases…' : list.message}
        </Text>
      )}
    </AppShell>
  );
}

function CaseList({
  cases,
  colleagues,
  archived,
}: {
  cases: readonly Case[];
  colleagues: readonly FirmColleague[];
  archived: boolean;
}) {
  const theme = useTheme();
  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  // A subject the directory does not carry still renders — as the subject. A
  // case opened by somebody since removed from the firm is history, and hiding
  // who opened it would be a worse answer than an unfamiliar id.
  const openedBy = (subject: string) =>
    colleagues.find((colleague) => colleague.subject === subject)?.displayName ?? subject;

  if (cases.length === 0) {
    return (
      <Text style={[styles.body, muted]}>
        {archived
          ? 'No archived cases. A case archived from its overview appears here.'
          : 'No cases yet. Choose “New case” to open one.'}
      </Text>
    );
  }

  return (
    /*
      A TABLE, and one link per row.

      Each row used to carry six links — intake, team, documents, packet,
      creditor matrix, review — because `/cases/<id>` did not exist and there
      was nothing else for a row to point at. At 44dp apiece that made a list of
      nine cases some 2,400px of stacked navigation, and it made this screen the
      only route into any case screen: moving from intake to documents meant
      coming back here and finding the row again. The six now live in the case's
      own rail (`CaseShell`), which leaves a row free to be a row.

      `Table` from the design system rather than Views: its native leaf asserts
      `table`/`row`/`columnheader`/`cell` by hand, which is exactly the wiring
      that is easy to get wrong and impossible to see.
    */
    <Table.Root dense>
      <Table.Head>
        <Table.Row>
          <Table.HeaderCell>Case</Table.HeaderCell>
          <Table.HeaderCell width={90}>Chapter</Table.HeaderCell>
          <Table.HeaderCell width={90}>District</Table.HeaderCell>
          <Table.HeaderCell width={140}>Status</Table.HeaderCell>
          <Table.HeaderCell width={150}>Opened</Table.HeaderCell>
        </Table.Row>
      </Table.Head>
      <Table.Body>
        {cases.map((item) => (
          <Table.Row key={item.id}>
            <Table.Cell>
              {/*
                The row's ONE link, and the accessible name carries the case
                rather than repeating "Open case" nine times — WCAG 2.4.4 is
                about a link making sense out of context. The visible text is
                the start of that name, which is what 2.5.3 asks for.

                It is a `Link` and not a pressable row because these render real
                `<a href>`s: middle-click and open-in-new-tab are how anyone
                works two cases at once.
              */}
              <Link
                href={`/cases/${item.id}`}
                aria-label={`Chapter ${item.chapter} case in ${item.district}, opened ${item.createdAt.slice(0, 10)} by ${openedBy(item.createdBy)}`}
                style={[
                  styles.caseLink,
                  { color: theme.colors.primary, fontFamily: theme.typography.body },
                ]}
              >
                Chapter {item.chapter} · {item.district}
              </Link>
            </Table.Cell>
            <Table.Cell width={90}>{String(item.chapter)}</Table.Cell>
            <Table.Cell width={90}>{item.district}</Table.Cell>
            <Table.Cell width={140}>
              <Badge intent={CASE_STATUS_INTENT[item.status]} size="sm">
                {CASE_STATUS_LABEL[item.status]}
              </Badge>
            </Table.Cell>
            <Table.Cell width={150}>
              <Text style={[styles.caseMeta, muted]}>
                {item.createdAt.slice(0, 10)}
                {'\n'}
                {openedBy(item.createdBy)}
              </Text>
            </Table.Cell>
          </Table.Row>
        ))}
      </Table.Body>
    </Table.Root>
  );
}

const styles = StyleSheet.create({
  toggles: { alignSelf: 'flex-start', marginBottom: spacing.md },
  actions: {
    flexDirection: 'row',
    marginTop: spacing.xs,
  },
  body: {
    fontSize: fontSizes.body,
    lineHeight: fontSizes.body * 1.5,
  },
  caseLink: {
    fontSize: fontSizes.label,
    fontWeight: '600',
    // 44dp, the WCAG 2.5.5 target size this app enforces on anything
    // pressable — a text link is no exception.
    lineHeight: 44,
  },
  caseMeta: {
    fontSize: fontSizes.caption,
  },
});
