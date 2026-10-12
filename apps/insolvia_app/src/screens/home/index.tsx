import type { FirmMembership } from '@insolvia-ai/api-client';
import { StyleSheet, View } from 'react-native';

import { AppShell } from '@/components/app-shell';
import { Heading } from '@/components/heading';
import { spacing, workspaceMaxWidth } from '@/theme';

import { EventsCard } from './events-card';
import { QuickActions } from './quick-actions';
import { RecentCasesCard } from './recent-cases-card';
import { TasksCard } from './tasks-card';

/**
 * The signed-in shell's home screen — the working surface (issue 14.7 /
 * #359), replacing the two-button landing shell that proved the delivery
 * pipeline while intake, the forms engine and e-filing were still arriving.
 *
 * Reached only through `RequireSession` **and** `RequireFirm` (see
 * `src/app/index.tsx`), the same pair `/calendar` and `/my-tasks` use: the
 * dashboard is built entirely out of firm-owned things — cases, tasks,
 * events — so a signed-in person with no firm yet has nothing here to show,
 * and `RequireFirm` already renders that explanation in place.
 *
 * **Four independent cards, four independent reads.** `GET /v1/cases`,
 * `GET /v1/me/tasks` and `GET /v1/calendar` already existed and already apply
 * ADR 0009's reachability rule each in its own right; a bundling
 * `GET /v1/me/dashboard` would have to re-state that rule a fourth time for
 * one screen's convenience, at the cost of a slower first paint whenever any
 * one of the three is slow. Four small requests that can each show their own
 * loading and empty state — the shape every other multi-panel screen in this
 * app already uses (`case-overview`'s spine, notes, events and tasks panels
 * are four reads too) — cost the browser nothing a spinner would not, and
 * keep each card testable and independently cacheable. See the report for
 * the one place this app DID add a route rather than composing existing
 * ones: `GET /v1/me/tasks?scope=` (`api/routes/tasks.py`), for the firm-wide
 * toggle no existing endpoint could answer.
 */
export function Home({ membership }: { membership: FirmMembership }) {
  return (
    <AppShell maxContentWidth={workspaceMaxWidth}>
      <Heading level={1}>Home</Heading>

      <QuickActions membership={membership} />

      <View style={styles.grid}>
        <View style={styles.column}>
          <RecentCasesCard />
          <TasksCard membership={membership} />
        </View>
        <View style={styles.column}>
          <EventsCard membership={membership} />
        </View>
      </View>
    </AppShell>
  );
}

const styles = StyleSheet.create({
  column: { flex: 1, gap: spacing.lg, minWidth: 320 },
  grid: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.lg,
    marginTop: spacing.lg,
  },
});
