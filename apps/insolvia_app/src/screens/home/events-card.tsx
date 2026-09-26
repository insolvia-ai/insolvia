import { permits } from '@insolvia-ai/api-client';
import type { CalendarEvent, FirmMembership } from '@insolvia-ai/api-client';
import { Select, Tabs } from '@insolvia-ai/design-system';
import { Link, useRouter } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';

import { useApi } from '@/api/use-api';
import { Heading } from '@/components/heading';
import { addDays, formatTime, today } from '@/screens/calendar/dates';
import { fontSizes, spacing, useTheme } from '@/theme';

type Scope = 'mine' | 'firm';

const SCOPE_OPTIONS = [
  { value: 'mine', label: 'Assigned to me' },
  { value: 'firm', label: 'Firm-wide' },
] as const;

type Tab = 'today' | 'tomorrow' | 'week';

const TABS: readonly { readonly value: Tab; readonly label: string }[] = [
  { value: 'today', label: 'Today' },
  { value: 'tomorrow', label: 'Tomorrow' },
  { value: 'week', label: 'This week' },
];

/** The `from`/`to` window a tab covers, anchored on the reader's local today. */
function windowFor(tab: Tab): { from: string; to: string } {
  const now = today();
  if (tab === 'today') return { from: now, to: now };
  if (tab === 'tomorrow') {
    const tomorrow = addDays(now, 1);
    return { from: tomorrow, to: tomorrow };
  }
  return { from: now, to: addDays(now, 6) };
}

type WindowState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly events: readonly CalendarEvent[] }
  | { readonly kind: 'error'; readonly message: string };

/**
 * The dashboard's events card (issue 14.7 / #359): `GET /v1/calendar`, over
 * three short windows rather than the calendar screen's day/week/month/agenda
 * — a dashboard card previews, it does not navigate a whole month. The
 * firm-wide/assigned-to-me toggle is the same `attendee=me` narrowing the
 * calendar screen's own picker uses, defaulting to "assigned to me". A
 * generated deadline shows its rule citation, exactly as the calendar screen
 * prints it.
 */
export function EventsCard({ membership }: { membership: FirmMembership }) {
  const theme = useTheme();
  const router = useRouter();
  const { call } = useApi();

  const [scope, setScope] = useState<Scope>('mine');
  const [tab, setTab] = useState<Tab>('today');
  const [window, setWindow] = useState<WindowState>({ kind: 'loading' });

  const mayView = permits(membership.permissions.events, 'view_only');

  const load = useCallback(async () => {
    setWindow({ kind: 'loading' });
    const { from, to } = windowFor(tab);
    try {
      const result = await call((client) =>
        client.getCalendar({ from, to, attendee: scope === 'mine' ? 'me' : undefined }),
      );
      if (result.ok) setWindow({ kind: 'ready', events: result.value.events });
    } catch {
      setWindow({ kind: 'error', message: 'Could not load the calendar.' });
    }
  }, [call, scope, tab]);

  useEffect(() => {
    if (mayView) void load();
  }, [load, mayView]);

  if (!mayView) return null;

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const events = window.kind === 'ready' ? window.events : [];

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
          Events
        </Heading>
        <Select
          aria-label="Whose events"
          options={[...SCOPE_OPTIONS]}
          value={scope}
          onValueChange={(next) => setScope(next as Scope)}
        />
      </View>

      <Tabs.Root
        defaultValue="today"
        value={tab}
        onValueChange={(next) => setTab(next as Tab)}
        aria-label="Which window"
      >
        <Tabs.List>
          {TABS.map((option) => (
            <Tabs.Tab key={option.value} value={option.value}>
              {option.label}
            </Tabs.Tab>
          ))}
        </Tabs.List>
      </Tabs.Root>

      {window.kind !== 'ready' ? (
        <Text
          aria-live={window.kind === 'error' ? 'assertive' : 'polite'}
          style={[styles.body, muted]}
        >
          {window.kind === 'loading' ? 'Loading…' : window.message}
        </Text>
      ) : events.length === 0 ? (
        <Text style={[styles.body, muted]}>Nothing scheduled.</Text>
      ) : (
        <View role="list" style={styles.list}>
          {events.map((event) => {
            const linked = event.case_id !== undefined;
            const time = formatTime(event) ?? 'All day';
            const meta = [time, event.rule_citation].filter(
              (part): part is string => part !== null && part !== undefined,
            );
            return (
              <Pressable
                key={event.id}
                role="listitem"
                accessibilityRole={linked ? 'link' : 'text'}
                aria-label={linked ? `${event.title} — open the case` : event.title}
                disabled={!linked}
                onPress={() => {
                  if (event.case_id !== undefined) router.push(`/cases/${event.case_id}`);
                }}
                style={[styles.row, { borderColor: theme.colors.line }]}
              >
                <Text
                  style={[
                    styles.rowTitle,
                    { color: theme.colors.ink, fontFamily: theme.typography.body },
                  ]}
                >
                  {event.title}
                </Text>
                <Text style={[styles.rowMeta, muted]}>{meta.join(' · ')}</Text>
              </Pressable>
            );
          })}
        </View>
      )}

      <Link
        href="/calendar"
        style={[styles.expand, { color: theme.colors.primary, fontFamily: theme.typography.body }]}
      >
        Open the calendar
      </Link>
    </View>
  );
}

const styles = StyleSheet.create({
  body: { fontSize: fontSizes.body, lineHeight: fontSizes.body * 1.5 },
  card: { borderWidth: 1, padding: spacing.lg },
  expand: {
    fontSize: fontSizes.label,
    fontWeight: '600',
    lineHeight: 44,
    marginTop: spacing.sm,
  },
  head: {
    alignItems: 'center',
    borderBottomWidth: 1,
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.md,
    justifyContent: 'space-between',
    marginBottom: spacing.md,
    paddingBottom: spacing.md,
  },
  list: { gap: spacing.xs, marginTop: spacing.sm },
  row: { borderBottomWidth: 1, gap: 2, minHeight: 44, paddingVertical: spacing.xs },
  rowMeta: { fontSize: fontSizes.caption },
  rowTitle: { fontSize: fontSizes.body },
  title: { flexShrink: 0 },
});
