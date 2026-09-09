import { permits } from '@insolvia-ai/api-client';
import type { Case, Debtor, PersonName } from '@insolvia-ai/api-client';
import type { BadgeIntent } from '@insolvia-ai/design-system';
import { usePathname, useRouter } from 'expo-router';
import { createContext, useCallback, useContext, useEffect, useState } from 'react';
import type { ReactNode } from 'react';
import { StyleSheet, View } from 'react-native';

import { useMembership } from '@/api/me';
import { useApi } from '@/api/use-api';
import { AppShell } from '@/components/app-shell';
import type { CaseNav } from '@/components/app-shell';
import type { IconName } from '@/components/icon';
import { StatusScreen } from '@/components/status-screen';
import { contentMaxWidth, spacing } from '@/theme';

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
  readonly icon: IconName;
  /** Present when the section sits behind a firm permission. */
  readonly feature?: 'extraction_review';
  /** Which count, if any, this section shows beside its name. */
  readonly count?: 'pendingReview';
}

const SECTIONS: readonly Section[] = [
  { segment: '', label: 'Overview', icon: 'grid' },
  { segment: 'intake', label: 'Intake', icon: 'clipboard' },
  { segment: 'documents', label: 'Documents', icon: 'file-text' },
  {
    segment: 'extraction-review',
    label: 'Extraction review',
    icon: 'check-square',
    feature: 'extraction_review',
    count: 'pendingReview',
  },
  { segment: 'creditor-matrix', label: 'Creditor matrix', icon: 'list' },
  { segment: 'packet', label: 'Filing packet', icon: 'package' },
  { segment: 'team', label: 'Team', icon: 'users' },
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
  const router = useRouter();
  const pathname = usePathname();
  const membership = useMembership();
  const { call } = useApi();

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
  const title = caseTitle(matter, debtors);
  // `caseTitle` falls back to "Chapter 7 · NDCA" when intake has not named a
  // debtor yet, which is exactly what the subtitle would say — so on a fresh
  // case the nav printed it twice, one above the other. Only worth showing
  // when the title is a person.
  const subtitle = title === chapterAndDistrict(matter) ? null : chapterAndDistrict(matter);

  // THE RAIL IS THE SHELL'S. This used to render a second dark column beside
  // the header's nav, with the case's sections in it; the app has one nav now,
  // on the left, and the case's sections are a group in it. What this hands
  // over is a description — see `CaseNav` — and the shell draws every row the
  // same way it draws Home and Cases.
  const caseNav: CaseNav = {
    title,
    subtitle,
    status: { label: STATUS_LABEL[matter.status], intent: STATUS_INTENT[matter.status] },
    items: visible.map((section) => {
      // The count rides in the LABEL rather than as a node beside it: the
      // nav pins its accessible name to `label`, so a separately-rendered
      // badge would be invisible to a screen reader — "Extraction review"
      // whether twelve records were waiting or none.
      const badge = section.count === undefined ? null : counts[section.count];
      return {
        key: section.segment,
        icon: section.icon,
        label: badge === null || badge === 0 ? section.label : `${section.label} (${badge})`,
        active: section.segment === current,
        onPress: () => {
          router.push(
            section.segment === '' ? `/cases/${caseId}` : `/cases/${caseId}/${section.segment}`,
          );
        },
      };
    }),
    onAllCases: () => {
      router.push('/cases');
    },
  };

  return (
    <CaseContext.Provider value={{ caseId, matter, debtors, counts, mayReview, reload: load }}>
      <AppShell frame="workspace" caseNav={caseNav}>
        <View style={styles.content}>{children}</View>
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
});
