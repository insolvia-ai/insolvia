import type { Case, CaseStatus, Debtor } from '@insolvia-ai/api-client';
import { Badge } from '@insolvia-ai/design-system';
import type { BadgeIntent } from '@insolvia-ai/design-system';
import { Link } from 'expo-router';
import { useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useApi } from '@/api/use-api';
import { caseTitle, chapterAndDistrict } from '@/components/case-shell';
import { Heading } from '@/components/heading';
import { MAX_RECENT_CASES, recentCaseIds } from '@/components/recent-cases';
import { fontSizes, spacing, useTheme } from '@/theme';

/** How a case's own status reads — mirrors `screens/cases`' mapping. */
const STATUS_LABEL: Record<CaseStatus, string> = {
  intake: 'In intake',
  ready_to_file: 'Ready to file',
  filed: 'Filed',
};

const STATUS_INTENT: Record<CaseStatus, BadgeIntent> = {
  intake: 'neutral',
  ready_to_file: 'success',
  filed: 'primary',
};

interface Row {
  readonly matter: Case;
  readonly debtors: readonly Debtor[];
}

type State =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly rows: readonly Row[] }
  | { readonly kind: 'error'; readonly message: string };

/**
 * The dashboard's first card (issue 14.7 / #359): the cases this viewer most
 * recently opened, falling back to the firm's most recently opened reachable
 * cases when this browser has never viewed one.
 *
 * **The trail is per-viewer `localStorage`** — see `components/recent-cases.ts`
 * for why a client-side pointer list, re-read through `getCase`/`listDebtors`,
 * is the right shape rather than a server-side "recently viewed" record.
 *
 * **The fallback is `GET /v1/cases` with no query**, which the store already
 * returns newest-first (`CaseStore.list_for_accessor`) under the same
 * reachability rule (ADR 0009) every other case read uses — so a firm signing
 * up for the first time sees its own first cases here rather than an empty
 * card, and nothing here re-derives who may see what.
 *
 * A recently-viewed id the caller can no longer reach (removed, or a
 * permission withdrawn) simply fails to load and drops out silently — the
 * same trade the case list makes for a colleague's name it cannot resolve.
 */
export function RecentCasesCard() {
  const theme = useTheme();
  const { call } = useApi();
  const [state, setState] = useState<State>({ kind: 'loading' });

  useEffect(() => {
    let live = true;

    /** `matter`'s own row, with its debtors read alongside it — never a
     * second read of the case itself, which the caller already has. */
    const rowFor = async (matter: Case): Promise<Row> => {
      try {
        const debtorResult = await call((client) => client.listDebtors(matter.id));
        return { matter, debtors: debtorResult.ok ? debtorResult.value : [] };
      } catch {
        // A nameless row still shows chapter and district — see caseTitle.
        return { matter, debtors: [] };
      }
    };

    /** A viewed id back into a row, or null if it can no longer be opened
     * (removed, or a permission withdrawn since it was last viewed) — the
     * ONE path that still needs `getCase`, since a bare id is all this
     * browser recorded. */
    const rowForViewedId = async (caseId: string): Promise<Row | null> => {
      try {
        const matterResult = await call((client) => client.getCase(caseId));
        return matterResult.ok ? await rowFor(matterResult.value) : null;
      } catch {
        return null;
      }
    };

    const load = async () => {
      const viewed = recentCaseIds();
      if (viewed.length > 0) {
        const rows = (await Promise.all(viewed.map(rowForViewedId))).filter(
          (row): row is Row => row !== null,
        );
        if (!live) return;
        if (rows.length > 0) {
          setState({ kind: 'ready', rows });
          return;
        }
        // Every recorded id failed to load (removed, or reach withdrawn) —
        // fall through to the same "nothing viewed yet" fallback below.
      }

      try {
        const listed = await call((client) => client.listCases({ limit: MAX_RECENT_CASES }));
        if (!live) return;
        if (!listed.ok) return;
        const rows = await Promise.all(listed.value.cases.map(rowFor));
        if (live) setState({ kind: 'ready', rows });
      } catch {
        if (live) setState({ kind: 'error', message: 'Could not load your cases.' });
      }
    };

    void load();
    return () => {
      live = false;
    };
    // Runs once per mount, like every other dashboard card's load — `call`'s
    // identity follows the access token (see `useApi`), not something this
    // effect should re-run for.
  }, []);

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };

  return (
    <View
      style={[
        styles.card,
        {
          backgroundColor: theme.colors.card,
          borderColor: theme.colors.line,
          borderRadius: theme.radii.lg,
        },
      ]}
    >
      <View style={[styles.head, { borderBottomColor: theme.colors.line }]}>
        <Heading level={2} size="body" style={styles.title}>
          Recent cases
        </Heading>
      </View>

      {state.kind !== 'ready' ? (
        <Text
          aria-live={state.kind === 'error' ? 'assertive' : 'polite'}
          style={[styles.body, muted]}
        >
          {state.kind === 'loading' ? 'Loading your cases…' : state.message}
        </Text>
      ) : state.rows.length === 0 ? (
        <Text style={[styles.body, muted]}>No cases yet. Open one to see it here next time.</Text>
      ) : (
        <View role="list" style={styles.list}>
          {state.rows.map(({ matter, debtors }) => (
            <View
              key={matter.id}
              role="listitem"
              style={[styles.row, { borderColor: theme.colors.line }]}
            >
              <Link
                href={`/cases/${matter.id}`}
                aria-label={`${caseTitle(matter, debtors)} — ${STATUS_LABEL[matter.status]}`}
                style={styles.rowLink}
              >
                <View style={styles.rowBody}>
                  <Text
                    style={[
                      styles.rowTitle,
                      { color: theme.colors.ink, fontFamily: theme.typography.body },
                    ]}
                  >
                    {caseTitle(matter, debtors)}
                  </Text>
                  <Text style={[styles.rowMeta, muted]}>
                    {chapterAndDistrict(matter)} ·{' '}
                    {matter.status === 'filed'
                      ? `Filed${matter.filedAt === undefined ? '' : ` ${matter.filedAt.slice(0, 10)}`}`
                      : 'Not yet filed'}
                  </Text>
                </View>
              </Link>
              <Badge intent={STATUS_INTENT[matter.status]} size="sm">
                {STATUS_LABEL[matter.status]}
              </Badge>
            </View>
          ))}
        </View>
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  body: { fontSize: fontSizes.body, lineHeight: fontSizes.body * 1.5 },
  card: { borderWidth: 1, padding: spacing.lg },
  head: {
    borderBottomWidth: 1,
    marginBottom: spacing.md,
    paddingBottom: spacing.md,
  },
  list: { gap: spacing.sm },
  row: {
    alignItems: 'center',
    borderBottomWidth: 1,
    flexDirection: 'row',
    gap: spacing.md,
    justifyContent: 'space-between',
    minHeight: 44,
    paddingVertical: spacing.sm,
  },
  rowBody: { flex: 1, gap: 2, minWidth: 0 },
  rowLink: { flex: 1, minWidth: 0 },
  rowMeta: { fontSize: fontSizes.caption },
  rowTitle: { fontSize: fontSizes.body, fontWeight: '600' },
  title: { flexShrink: 0 },
});
