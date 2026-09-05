import { permits } from '@insolvia-ai/api-client';
import type { Case, Debtor, PersonName } from '@insolvia-ai/api-client';
import { Badge, Sidebar } from '@insolvia-ai/design-system';
import type { BadgeIntent } from '@insolvia-ai/design-system';
import { usePathname, useRouter } from 'expo-router';
import { createContext, useCallback, useContext, useEffect, useState } from 'react';
import type { ReactNode } from 'react';
import { StyleSheet, Text, View, useWindowDimensions } from 'react-native';

import { useMembership } from '@/api/me';
import { useApi } from '@/api/use-api';
import { AppShell } from '@/components/app-shell';
import { StatusScreen } from '@/components/status-screen';
import { fontSizes, railBreakpoint, spacing, useTheme, workspaceMaxWidth } from '@/theme';

/** The case a screen is inside, loaded once by {@link CaseShell}. */
export interface CaseContextValue {
  readonly caseId: string;
  readonly matter: Case;
  /** Empty until intake has been started. */
  readonly debtors: readonly Debtor[];
  /** Re-reads the case and its debtors — for a screen that just changed one. */
  readonly reload: () => Promise<void>;
}

const CaseContext = createContext<CaseContextValue | null>(null);

/**
 * The case the current screen belongs to.
 *
 * Throws outside a {@link CaseShell} rather than returning null: every screen
 * under `/cases/[caseId]` renders inside one by construction, so a null here
 * would be a routing bug wearing an optional chain.
 */
export function useCase(): CaseContextValue {
  const value = useContext(CaseContext);
  if (value === null) {
    throw new Error('useCase() must be called inside <CaseShell>, i.e. under /cases/[caseId]');
  }
  return value;
}

/**
 * One section of a case, in filing order.
 *
 * `segment` is the route segment under `/cases/<id>`; the overview is the empty
 * one. Ordering is the order the work actually happens in, which is the only
 * thing that makes a rail readable as a process rather than an alphabetised
 * menu.
 */
interface Section {
  readonly segment: string;
  readonly label: string;
  /** Present when the section sits behind a firm permission. */
  readonly feature?: 'extraction_review';
}

const SECTIONS: readonly Section[] = [
  { segment: '', label: 'Overview' },
  { segment: 'intake', label: 'Intake' },
  { segment: 'documents', label: 'Documents' },
  { segment: 'extraction-review', label: 'Extraction review', feature: 'extraction_review' },
  { segment: 'creditor-matrix', label: 'Creditor matrix' },
  { segment: 'packet', label: 'Filing packet' },
  { segment: 'team', label: 'Team' },
];

const STATUS_LABEL: Record<Case['status'], string> = {
  intake: 'In intake',
  ready_to_file: 'Ready to file',
  filed: 'Filed',
};

const STATUS_INTENT: Record<Case['status'], BadgeIntent> = {
  intake: 'neutral',
  ready_to_file: 'success',
  filed: 'primary',
};

/**
 * A debtor's name as one string, or null when intake has not supplied one.
 *
 * Parts are joined in the order the forms print them and blanks are dropped,
 * so a debtor with only a surname still reads as a name rather than as
 * `undefined undefined Reyes`.
 */
function personName(name: PersonName | undefined): string | null {
  if (name === undefined) return null;
  const joined = [name.given, name.middle, name.surname, name.suffix]
    .map((part) => part?.trim() ?? '')
    .filter((part) => part !== '')
    .join(' ');
  return joined === '' ? null : joined;
}

/**
 * What to call this case: its debtors if intake has named any, else the
 * chapter and district.
 *
 * Never the id. It is a server-generated uuid that identifies nothing about a
 * person, and a rail that led with one would be the same defect the case list
 * had — see {@link Case.createdBy}, which carries the same warning.
 */
export function caseTitle(matter: Case, debtors: readonly Debtor[]): string {
  const names = debtors
    .map((debtor) => personName(debtor.name))
    .filter((name): name is string => name !== null);
  if (names.length > 0) return names.join(' & ');
  return chapterAndDistrict(matter);
}

/** The case's chapter and district, as one line. Also `caseTitle`'s fallback. */
export function chapterAndDistrict(matter: Case): string {
  return `Chapter ${matter.chapter} · ${matter.district}`;
}

/**
 * The frame every screen under `/cases/[caseId]` sits inside: the case's
 * identity, the rail that moves between its sections, and the content column
 * beside them.
 *
 * **This is the tier the app was missing.** The six case screens existed, but
 * nothing above them did — no `_layout.tsx` and no `/cases/[caseId]` — so each
 * one rendered a bare `AppShell` with a heading like "Creditor matrix" and no
 * answer to "of which case?". The only links into any of them came from the
 * case list, six per row, which made the list a menu and made moving from
 * intake to documents a trip back through it. One layout fixes all three: the
 * case is fetched once here, identity comes free to every child, and the six
 * links live in one rail instead of nine rows.
 *
 * **The case is loaded here, not per screen.** {@link useCase} hands children
 * what this already fetched, so a screen that needs the chapter does not spend
 * a request on it. The two calls are made together and a failed debtor read is
 * survivable — see below.
 *
 * **The rail is `Sidebar` from the design system, not a hand-rolled column.**
 * Its items are `Sidebar.Item`, whose native leaf is a `Pressable` with
 * `accessibilityRole="link"` and no `href`. That trade is deliberate here and
 * the opposite of the call `AppShell`'s footer makes: those links LEAVE the app,
 * where a real anchor is the whole point, while these are in-app route changes
 * the router handles either way.
 */
export function CaseShell({ caseId, children }: { caseId: string; children: ReactNode }) {
  const theme = useTheme();
  const router = useRouter();
  const pathname = usePathname();
  const membership = useMembership();
  const { call } = useApi();
  const { width } = useWindowDimensions();

  const [matter, setMatter] = useState<Case | null>(null);
  const [debtors, setDebtors] = useState<readonly Debtor[]>([]);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      const result = await call((client) => client.getCase(caseId));
      // !ok means the session ended and useApi already navigated; leaving this
      // in its loading state is correct, because it is about to unmount.
      if (!result.ok) return;
      setMatter(result.value);
      setError(null);
    } catch {
      // A 404 here means unknown OR not the caller's — the API refuses to say
      // which (see `getCase`), and so does this. "Could not be opened" is the
      // honest wording for both, and re-stating the id would be an oracle.
      setError('This case could not be opened. It may have been removed, or it may not be yours.');
      return;
    }

    try {
      const result = await call((client) => client.listDebtors(caseId));
      if (result.ok) setDebtors(result.value);
    } catch {
      // Names are a nicety; the case is not. `caseTitle` already falls back to
      // the chapter and district, so a directory this failed to read costs a
      // nicer heading and nothing else — the same trade the case list makes
      // over its colleague names.
    }
  }, [call, caseId]);

  useEffect(() => {
    void load();
  }, [load]);

  if (error !== null) {
    return <StatusScreen title="Case unavailable" message={error} tone="error" />;
  }
  if (matter === null) {
    return <StatusScreen title="Opening case" message="Loading this case…" />;
  }

  // A COURTESY, never a control — the same `permits` rule the case list and the
  // firm screen document. The API re-checks, and the extraction-review screen
  // states the refusal itself rather than 404ing on a page a colleague linked.
  const visible = SECTIONS.filter(
    (section) =>
      section.feature === undefined ||
      (membership != null && permits(membership.permissions[section.feature], 'view_only')),
  );

  // Which section is showing, as its route segment — '' for the overview.
  // Derived from the pathname rather than `useSegments()`, whose return type is
  // a tuple narrowed to the CURRENT route, so indexing past its length is a
  // type error rather than the `undefined` the runtime would hand back.
  const base = `/cases/${caseId}`;
  const current = pathname.startsWith(`${base}/`) ? pathname.slice(base.length + 1) : '';
  const stacked = width < railBreakpoint;
  const title = caseTitle(matter, debtors);
  // `caseTitle` falls back to "Chapter 7 · NDCA" when intake has not named a
  // debtor yet, which is exactly what the line below says — so on a fresh case
  // the rail printed it twice, one above the other. Only worth showing when
  // the title is a person.
  const titleIsDebtors = title !== chapterAndDistrict(matter);

  return (
    <CaseContext.Provider value={{ caseId, matter, debtors, reload: load }}>
      <AppShell maxContentWidth={workspaceMaxWidth}>
        <View style={[styles.workspace, stacked ? styles.workspaceStacked : null]}>
          <View style={stacked ? styles.railStacked : styles.rail}>
            <Sidebar.Root>
              <Sidebar.Head>
                <Sidebar.Title>{title}</Sidebar.Title>
              </Sidebar.Head>

              <View style={styles.identity}>
                {titleIsDebtors ? (
                  <Text
                    style={[
                      styles.identityLine,
                      { color: theme.colors.muted, fontFamily: theme.typography.body },
                    ]}
                  >
                    {chapterAndDistrict(matter)}
                  </Text>
                ) : null}
                <View style={styles.status}>
                  <Badge intent={STATUS_INTENT[matter.status]} size="sm">
                    {STATUS_LABEL[matter.status]}
                  </Badge>
                </View>
              </View>

              <Sidebar.Separator />

              {/* NAMED, and named something other than "Primary". `Sidebar.Nav`
                emits `role="navigation"`, which is a landmark, and so does
                `AppShell`'s header nav. Two landmarks of a kind on one page
                have to be told apart by name — axe flags the pair when both
                take the default, and a screen reader offers "navigation,
                navigation". This is also why "All cases" sits in the footer
                below rather than in a second nav of its own. */}
              <Sidebar.Nav label="Case sections">
                {visible.map((section) => (
                  <Sidebar.Item
                    key={section.segment}
                    label={section.label}
                    active={section.segment === current}
                    onPress={() => {
                      router.push(
                        section.segment === ''
                          ? `/cases/${caseId}`
                          : `/cases/${caseId}/${section.segment}`,
                      );
                    }}
                  />
                ))}
              </Sidebar.Nav>

              <Sidebar.Separator />

              <Sidebar.Footer>
                <Sidebar.Item
                  label="All cases"
                  onPress={() => {
                    router.push('/cases');
                  }}
                />
              </Sidebar.Footer>
            </Sidebar.Root>
          </View>

          <View style={styles.content}>{children}</View>
        </View>
      </AppShell>
    </CaseContext.Provider>
  );
}

const styles = StyleSheet.create({
  content: {
    flex: 1,
    gap: spacing.md,
    // Without this a long unbroken cell — a filename, an email — makes the
    // flex child refuse to shrink and pushes the rail off screen.
    minWidth: 0,
  },
  identity: {
    gap: spacing.xs,
    paddingHorizontal: spacing.sm,
  },
  identityLine: {
    fontSize: fontSizes.caption,
  },
  rail: {
    width: 232,
  },
  railStacked: {
    width: '100%',
  },
  status: {
    // `alignItems: 'flex-start'` on the parent would stretch nothing else, but
    // a Badge in a full-width column would grow to fill it.
    flexDirection: 'row',
  },
  workspace: {
    flexDirection: 'row',
    gap: spacing.lg,
  },
  workspaceStacked: {
    flexDirection: 'column',
  },
});
