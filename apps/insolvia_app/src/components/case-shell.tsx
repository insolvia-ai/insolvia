import { permits } from '@insolvia-ai/api-client';
import type { Case, Debtor, PersonName } from '@insolvia-ai/api-client';
import { Badge, Sidebar, ThemeProvider } from '@insolvia-ai/design-system';
import type { BadgeIntent } from '@insolvia-ai/design-system';
import { usePathname, useRouter } from 'expo-router';
import { createContext, useCallback, useContext, useEffect, useState } from 'react';
import type { ReactNode } from 'react';
import { StyleSheet, Text, View, useWindowDimensions } from 'react-native';

import { useMembership } from '@/api/me';
import { useApi } from '@/api/use-api';
import { AppShell } from '@/components/app-shell';
import { StatusScreen } from '@/components/status-screen';
import { contentMaxWidth, fontSizes, railBreakpoint, spacing, useTheme } from '@/theme';
import { brandColors, brandFonts, brandRadii } from '@/theme/brand-colors';

/**
 * The one count the rail shows beside a section's name.
 *
 * `null` is "not read yet, or could not be read", and renders as no badge at
 * all rather than as a zero — a rail that says "Extraction review 0" on every
 * case whose count request failed is worse than one that says nothing.
 *
 * ONLY the review queue, and that is a deliberate limit. The rail is on all
 * seven case screens, so anything counted here is a request every one of them
 * pays. A documents badge cost exactly that and duplicated the documents
 * screen's own listing; the review queue earns it because it is the "somebody
 * owes this case work" signal and no other screen fetches it. The overview
 * reads this rather than asking again.
 */
export interface CaseCounts {
  readonly pendingReview: number | null;
}

/** The case a screen is inside, loaded once by {@link CaseShell}. */
export interface CaseContextValue {
  readonly caseId: string;
  readonly matter: Case;
  /** Empty until intake has been started. */
  readonly debtors: readonly Debtor[];
  readonly counts: CaseCounts;
  /** Whether this firm may see the extraction queue — the rail's own gate. */
  readonly mayReview: boolean;
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
  /** Which count, if any, this section shows beside its name. */
  readonly count?: 'pendingReview';
}

const SECTIONS: readonly Section[] = [
  { segment: '', label: 'Overview' },
  { segment: 'intake', label: 'Intake' },
  { segment: 'documents', label: 'Documents' },
  {
    segment: 'extraction-review',
    label: 'Extraction review',
    feature: 'extraction_review',
    count: 'pendingReview',
  },
  { segment: 'creditor-matrix', label: 'Creditor matrix' },
  { segment: 'packet', label: 'Filing packet' },
  { segment: 'team', label: 'Team' },
];

/**
 * The rail's own colours, taken from the DARK scheme whatever the app's scheme
 * is.
 *
 * The rail is chrome and stays dark in both — near-black beside an ivory
 * workspace in light mode, a step above the ground in dark. Reading these from
 * `theme.colors` instead would paint near-black text on near-black the moment
 * somebody switched to light.
 *
 * They come from `brandColors.dark` rather than being spelled out, so the one
 * file that owns the palette still owns this.
 */
const railColors = {
  bg: brandColors.dark.card,
  line: brandColors.dark.line,
  ink: brandColors.dark.ink,
  muted: brandColors.dark.muted,
  active: brandColors.dark.surfaceAlt,
} as const;

/**
 * The theme the rail's own package components run under.
 *
 * BOTH slots hold the dark palette, which is the same trick
 * `ThemePreferenceProvider` uses for an explicit scheme: the package's leaves
 * consult the OS themselves and cannot be redirected, so the way to pin them is
 * to make both answers the same one. Without it a `Sidebar.Item` in light mode
 * takes near-black ink from the active scheme and paints it on the near-black
 * rail.
 *
 * Nesting is supported and the nearest provider wins outright — the package
 * says so explicitly — so this pins the rail without touching the rest of the
 * app. Frozen and hoisted so it is one stable object for the module's life,
 * which is what keeps the leaves' `React.memo` boundaries intact.
 */
const RAIL_THEME = Object.freeze({
  light: brandColors.dark,
  dark: brandColors.dark,
  fonts: brandFonts,
  radii: brandRadii,
});

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
 * The reading column a case screen renders into.
 *
 * The shell gives its child the whole frame beside the rail and caps nothing,
 * because the six screens do not want one measure: five are forms and prose
 * that want a short line, and the overview lays out two columns and wants more.
 * A cap in the shell served whichever of those it was written for and broke the
 * other — at 1440 it gave intake a 1280px-wide "First name" box.
 *
 * So the measure is the screen's own choice, and this is the default: wrap in
 * it, or state a wider one, but state one.
 */
export function CaseColumn({ children }: { children: ReactNode }) {
  return <View style={styles.column}>{children}</View>;
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
  const [counts, setCounts] = useState<CaseCounts>({ pendingReview: null });
  const [error, setError] = useState<string | null>(null);

  // A COURTESY, never a control — the same `permits` rule the case list and the
  // firm screen document. It decides whether the rail shows the section at all
  // AND whether its count is worth asking for; the API re-checks regardless,
  // and the extraction-review screen states the refusal itself rather than
  // 404ing on a page a colleague linked.
  const mayReview =
    membership != null && permits(membership.permissions.extraction_review, 'view_only');

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

    // The rail's badge. Read after the case, never before: if the case itself
    // is unreachable this never runs, and the shell shows the refusal rather
    // than a second request failing behind it.
    if (mayReview) {
      try {
        const queue = await call((client) => client.listExtractionCandidates(caseId, 'pending'));
        setCounts({ pendingReview: queue.ok ? queue.value.length : null });
      } catch {
        // A badge is a nicety; the rail is not.
      }
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
  }, [call, caseId, mayReview]);

  useEffect(() => {
    void load();
  }, [load]);

  if (error !== null) {
    return <StatusScreen title="Case unavailable" message={error} tone="error" />;
  }
  if (matter === null) {
    return <StatusScreen title="Opening case" message="Loading this case…" />;
  }

  const visible = SECTIONS.filter((section) => section.feature === undefined || mayReview);

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
    <CaseContext.Provider value={{ caseId, matter, debtors, counts, mayReview, reload: load }}>
      <AppShell frame="workspace">
        <View style={[styles.workspace, stacked ? styles.workspaceStacked : null]}>
          {/*
            THE RAIL IS DARK ON EVERY SCHEME, and that is the composition rather
            than an oversight. In light mode it is near-black against an ivory
            workspace, which is what gives a case a permanent identity and what
            keeps the chrome from dissolving into the page now that no colour is
            doing that job. In dark mode it is a step ABOVE the ground for the
            same reason — the rail must read as chrome either way.

            It is the one place in the app that paints a colour the scheme did
            not choose, so it takes its ink and its muted text from the DARK
            scheme explicitly rather than from `theme.colors`, which would hand
            it near-black text on near-black in light mode.
          */}
          <View
            style={[
              stacked ? styles.railStacked : styles.rail,
              { backgroundColor: railColors.bg, borderRightColor: railColors.line },
            ]}
          >
            <ThemeProvider theme={RAIL_THEME}>
              <Sidebar.Root>
                <Sidebar.Head>
                  <Text
                    numberOfLines={2}
                    style={[styles.railTitle, { fontFamily: theme.typography.heading }]}
                  >
                    {title}
                  </Text>
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
                  {visible.map((section) => {
                    const badge = section.count === undefined ? null : counts[section.count];
                    return (
                      <Sidebar.Item
                        key={section.segment}
                        // The count rides in the LABEL rather than as a node
                        // beside it: `Sidebar.Item` pins its accessible name to
                        // `label`, so a separately-rendered badge would be
                        // invisible to a screen reader — "Extraction review"
                        // whether twelve records were waiting or none.
                        label={
                          badge === null || badge === 0
                            ? section.label
                            : `${section.label} (${badge})`
                        }
                        active={section.segment === current}
                        onPress={() => {
                          router.push(
                            section.segment === ''
                              ? `/cases/${caseId}`
                              : `/cases/${caseId}/${section.segment}`,
                          );
                        }}
                      />
                    );
                  })}
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
            </ThemeProvider>
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
    // Without this a long unbroken cell — a filename, an email — makes the
    // flex child refuse to shrink and pushes the rail off screen.
    minWidth: 0,
    padding: spacing.xl,
  },
  column: {
    gap: spacing.md,
    // The DEFAULT measure for a case screen, and the reason `CaseColumn`
    // exists rather than a cap up here: these six screens are forms and prose,
    // not tables. Letting the frame decide gave a "First name" input 1280px of
    // it. The one screen that genuinely needs two columns says so itself.
    maxWidth: contentMaxWidth,
    width: '100%',
  },

  identity: {
    gap: spacing.xs,
    paddingHorizontal: spacing.sm,
  },
  identityLine: {
    fontSize: fontSizes.caption,
  },
  railTitle: {
    color: brandColors.dark.ink,
    fontSize: fontSizes.body,
    // The same weight and leading as `Heading`'s `body` size, because that is
    // what this is: the case's title, set in the UI face. It is not a
    // `Heading` only because the rail is chrome and the page's real `<h1>` is
    // the overview's — two headings for one case would double the outline.
    fontWeight: '600',
    lineHeight: fontSizes.body * 1.5,
  },
  rail: {
    // `Sidebar.Root` sets its OWN width — `sidebarWidth.expanded`, 256 — so
    // this holds exactly that and nothing else. It used to say 232, which the
    // sidebar overflowed by 24: precisely the `spacing.lg` gap that used to be
    // on `workspace`, so the two columns rendered flush against each other and
    // the gap looked like it had never been written.
    borderRightWidth: 1,
    width: 256,
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
    // No gap and no padding: the rail is CHROME. It sits flush against the
    // header above it and the window edge beside it, and carries its own
    // right-hand rule — which is what makes it read as one frame with the top
    // bar rather than as a floating island. Padding belongs to `content`.
    flexDirection: 'row',
    flexGrow: 1,
  },
  workspaceStacked: {
    flexDirection: 'column',
  },
});
