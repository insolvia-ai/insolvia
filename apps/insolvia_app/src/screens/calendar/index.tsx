import { ApiValidationException, permits } from '@insolvia-ai/api-client';
import type { CalendarEvent, Case, FirmColleague, FirmMembership } from '@insolvia-ai/api-client';
import { Button, Select, Tabs } from '@insolvia-ai/design-system';
import { useRouter } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { Pressable, StyleSheet, Text, View, useWindowDimensions } from 'react-native';

import { useApi } from '@/api/use-api';
import { AppShell } from '@/components/app-shell';
import { EMPTY_EVENT, EventForm, toDraft } from '@/components/event-form';
import type { EventFormState } from '@/components/event-form';
import { Heading } from '@/components/heading';
import { fontSizes, railBreakpoint, spacing, useTheme, workspaceMaxWidth } from '@/theme';

import {
  VIEWS,
  daysBetween,
  eventsOn,
  formatShort,
  formatTime,
  monthGrid,
  step,
  today,
  windowFor,
  windowTitle,
} from './dates';
import type { CalendarView } from './dates';

type WindowState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly events: readonly CalendarEvent[] }
  | { readonly kind: 'error'; readonly message: string };

const WEEKDAYS = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'] as const;

/** How many of a day's events a month cell lists before "+n more". */
const CELL_EVENTS = 3;

const ALL_CASES = '__all__';
const EVERYONE = '__everyone__';

/**
 * `/calendar` — the firm's events and every generated deadline the caller
 * may see, by day, week, month or as an agenda (issue 14.6 / #358).
 *
 * ONE READ PER WINDOW. The view and the anchor day decide a `from`/`to`;
 * `GET /v1/calendar` answers with everything the caller may see in it —
 * the server applies the case access rule, so this screen never filters
 * by permission itself. "Mine" and the case picker are query narrowings
 * the server also applies; they never widen what is shown.
 *
 * Timed events sit on the READER's local day (`dates.ts` says why), all-day
 * ones on their calendar date. A deadline the engine generated names its
 * rule; a dismissed one is shown struck through rather than hidden, because
 * "we dismissed that" is a fact worth seeing on the day it would have been
 * due.
 */
export function CalendarScreen({ membership }: { membership: FirmMembership }) {
  const theme = useTheme();
  const router = useRouter();
  const { call } = useApi();
  const { width } = useWindowDimensions();

  const [view, setView] = useState<CalendarView>('month');
  const [anchor, setAnchor] = useState(() => today());
  const [attendee, setAttendee] = useState<string>(EVERYONE);
  const [caseId, setCaseId] = useState<string>(ALL_CASES);
  const [window, setWindow] = useState<WindowState>({ kind: 'loading' });
  const [cases, setCases] = useState<readonly Case[]>([]);
  const [colleagues, setColleagues] = useState<readonly FirmColleague[]>([]);
  const [adding, setAdding] = useState<EventFormState | null>(null);
  const [errors, setErrors] = useState<Readonly<Record<string, string>>>({});
  const [saving, setSaving] = useState(false);
  const [status, setStatus] = useState('');

  const mayView = permits(membership.permissions.events, 'view_only');
  const mayChange = permits(membership.permissions.events, 'add_edit');

  const load = useCallback(async () => {
    const { from, to } = windowFor(view, anchor);
    try {
      const result = await call((client) =>
        client.getCalendar({
          from,
          to,
          attendee: attendee === EVERYONE ? undefined : attendee,
          caseId: caseId === ALL_CASES ? undefined : caseId,
        }),
      );
      if (result.ok) setWindow({ kind: 'ready', events: result.value.events });
    } catch {
      setWindow({ kind: 'error', message: 'Could not load the calendar.' });
    }
  }, [anchor, attendee, call, caseId, view]);

  useEffect(() => {
    if (mayView) void load();
  }, [load, mayView]);

  // The pickers' options: the first page of cases the caller may see, and
  // the directory for attendee names. Both are niceties — a failed read
  // leaves the picker at "all" rather than taking the calendar down.
  useEffect(() => {
    if (!mayView) return;
    let live = true;
    void (async () => {
      try {
        const listed = await call((client) => client.listCases({ limit: 100 }));
        if (live && listed.ok) setCases(listed.value.cases);
      } catch {
        // The picker simply offers "all cases".
      }
      try {
        const directory = await call((client) => client.listFirmDirectory());
        if (live && directory.ok) setColleagues(directory.value);
      } catch {
        // Attendees render as "you" and nothing else.
      }
    })();
    return () => {
      live = false;
    };
  }, [call, mayView]);

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const ink = { color: theme.colors.ink, fontFamily: theme.typography.body };

  if (!mayView) {
    return (
      <AppShell>
        <Heading level={1}>Calendar</Heading>
        <Text style={[styles.body, muted]}>
          Your firm has not given you access to the calendar. Ask one of your firm’s administrators
          if you need it.
        </Text>
      </AppShell>
    );
  }

  const persist = async (form: EventFormState) => {
    setSaving(true);
    setStatus('Saving…');
    setErrors({});
    try {
      const result = await call((client) => client.addFirmEvent(toDraft(form)));
      if (!result.ok) {
        setStatus('');
        return;
      }
      setStatus('Saved');
      setAdding(null);
      await load();
    } catch (cause) {
      if (cause instanceof ApiValidationException) {
        setErrors(cause.fields);
        setStatus('Some answers need attention.');
      } else {
        setStatus('Could not save. Your entries are still here — try again.');
      }
    } finally {
      setSaving(false);
    }
  };

  const caseLabel = (id: string): string => {
    const matter = cases.find((c) => c.id === id);
    return matter === undefined ? 'Case' : `Chapter ${matter.chapter} · ${matter.district}`;
  };

  const open = (event: CalendarEvent) => {
    if (event.case_id !== undefined) router.push(`/cases/${event.case_id}`);
  };

  const stacked = width < railBreakpoint;
  const { from, to } = windowFor(view, anchor);
  const events = window.kind === 'ready' ? window.events : [];

  return (
    <AppShell maxContentWidth={workspaceMaxWidth}>
      <View style={styles.head}>
        <Heading level={1}>Calendar</Heading>
        <Text style={[styles.body, muted]}>
          Hearings, meetings and every deadline the rules set from a case’s filing — yours, or the
          whole firm’s.
        </Text>
      </View>

      <View style={[styles.controls, stacked ? styles.controlsStacked : null]}>
        <Tabs.Root
          defaultValue="month"
          value={view}
          onValueChange={(next) => setView(next as CalendarView)}
          aria-label="Calendar view"
        >
          <Tabs.List>
            {VIEWS.map((option) => (
              <Tabs.Tab key={option.value} value={option.value}>
                {option.label}
              </Tabs.Tab>
            ))}
          </Tabs.List>
        </Tabs.Root>

        <View style={styles.nav}>
          <Button
            size="lg"
            intent="secondary"
            aria-label="Previous"
            onPress={() => setAnchor(step(view, anchor, -1))}
          >
            ‹
          </Button>
          <Button size="lg" intent="secondary" onPress={() => setAnchor(today())}>
            Today
          </Button>
          <Button
            size="lg"
            intent="secondary"
            aria-label="Next"
            onPress={() => setAnchor(step(view, anchor, 1))}
          >
            ›
          </Button>
        </View>

        <View style={styles.pickers}>
          <Select
            aria-label="Whose events"
            options={[
              { value: EVERYONE, label: 'Everyone' },
              { value: 'me', label: 'Mine' },
            ]}
            value={attendee}
            onValueChange={(next) => setAttendee(next)}
          />
          <Select
            aria-label="Which case"
            options={[
              { value: ALL_CASES, label: 'All cases' },
              ...cases.map((matter) => ({ value: matter.id, label: caseLabel(matter.id) })),
            ]}
            value={caseId}
            onValueChange={(next) => setCaseId(next)}
          />
        </View>
      </View>

      <Heading level={2} size="body" style={styles.windowTitle}>
        {windowTitle(view, anchor)}
      </Heading>

      <Text
        aria-live={window.kind === 'error' ? 'assertive' : 'polite'}
        style={[styles.help, muted]}
      >
        {window.kind === 'loading' ? 'Loading…' : window.kind === 'error' ? window.message : status}
      </Text>

      {view === 'month' ? (
        <MonthView
          anchor={anchor}
          events={events}
          caseLabel={caseLabel}
          onOpen={open}
          onPickDay={(day) => {
            setAnchor(day);
            setView('day');
          }}
        />
      ) : (
        <View style={styles.list}>
          {daysBetween(from, to).map((day) => {
            const todays = eventsOn(events, day);
            if (view === 'agenda' && todays.length === 0) return null;
            return (
              <View key={day} style={[styles.dayBlock, { borderTopColor: theme.colors.line }]}>
                <Text style={[styles.dayLabel, ink]}>{formatShort(day)}</Text>
                {todays.length === 0 ? (
                  <Text style={[styles.help, muted]}>Nothing scheduled</Text>
                ) : (
                  todays.map((event) => (
                    <EventRow
                      key={`${day}-${event.id}`}
                      event={event}
                      caseLabel={caseLabel}
                      onOpen={open}
                    />
                  ))
                )}
              </View>
            );
          })}
          {view === 'agenda' && events.length === 0 && window.kind === 'ready' ? (
            <Text style={[styles.body, muted]}>Nothing in the next 60 days.</Text>
          ) : null}
        </View>
      )}

      {mayChange ? (
        adding === null ? (
          <View style={styles.addRow}>
            <Button size="lg" onPress={() => setAdding({ ...EMPTY_EVENT, startDate: anchor })}>
              Add office event
            </Button>
          </View>
        ) : (
          <View style={styles.addRow}>
            <Heading level={2} size="body">
              New office event
            </Heading>
            <EventForm
              form={adding}
              colleagues={colleagues}
              errors={errors}
              saving={saving}
              saveLabel="Save event"
              onChange={setAdding}
              onSave={() => void persist(adding)}
              onCancel={() => {
                setAdding(null);
                setErrors({});
                setStatus('');
              }}
            />
          </View>
        )
      ) : null}
    </AppShell>
  );
}

/** One event as a row: its time, its title, its case, its rule. */
function EventRow({
  event,
  caseLabel,
  onOpen,
}: {
  event: CalendarEvent;
  caseLabel: (id: string) => string;
  onOpen: (event: CalendarEvent) => void;
}) {
  const theme = useTheme();
  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const time = formatTime(event);
  const meta = [
    time,
    event.case_id === undefined ? 'Office' : caseLabel(event.case_id),
    event.rule_citation,
    event.location,
  ].filter((part): part is string => part !== null && part !== undefined);
  const linked = event.case_id !== undefined;
  return (
    <Pressable
      accessibilityRole={linked ? 'link' : 'text'}
      aria-label={linked ? `${event.title} — open the case` : event.title}
      disabled={!linked}
      onPress={() => onOpen(event)}
      style={[
        styles.eventRow,
        { borderLeftColor: event.generated ? theme.colors.warning : theme.colors.primary },
      ]}
    >
      <Text
        style={[
          styles.eventTitle,
          {
            color: theme.colors.ink,
            fontFamily: theme.typography.body,
            textDecorationLine: event.dismissed ? 'line-through' : 'none',
          },
        ]}
      >
        {event.title}
      </Text>
      <Text style={[styles.eventMeta, muted]}>{meta.join(' · ')}</Text>
    </Pressable>
  );
}

function MonthView({
  anchor,
  events,
  caseLabel,
  onOpen,
  onPickDay,
}: {
  anchor: string;
  events: readonly CalendarEvent[];
  caseLabel: (id: string) => string;
  onOpen: (event: CalendarEvent) => void;
  onPickDay: (day: string) => void;
}) {
  const theme = useTheme();
  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const month = anchor.slice(0, 7);
  const now = today();
  return (
    <View style={[styles.grid, { borderColor: theme.colors.line }]}>
      <View style={styles.gridRow}>
        {WEEKDAYS.map((weekday) => (
          <Text key={weekday} style={[styles.weekday, muted]}>
            {weekday}
          </Text>
        ))}
      </View>
      {monthGrid(anchor).map((week) => (
        <View key={week[0]} style={styles.gridRow}>
          {week.map((day) => {
            const todays = eventsOn(events, day);
            const inMonth = day.slice(0, 7) === month;
            return (
              <View
                key={day}
                style={[
                  styles.cell,
                  { borderColor: theme.colors.line },
                  inMonth ? null : { backgroundColor: theme.colors.surfaceAlt },
                ]}
              >
                <Pressable
                  accessibilityRole="button"
                  aria-label={`${formatShort(day)}, ${todays.length} ${todays.length === 1 ? 'event' : 'events'}`}
                  onPress={() => onPickDay(day)}
                  style={styles.cellDay}
                >
                  <Text
                    style={[
                      styles.cellDayText,
                      {
                        color: day === now ? theme.colors.primary : theme.colors.ink,
                        fontFamily: theme.typography.body,
                        fontWeight: day === now ? '600' : '400',
                      },
                    ]}
                  >
                    {String(Number(day.slice(8, 10)))}
                  </Text>
                </Pressable>
                {todays.slice(0, CELL_EVENTS).map((event) => (
                  <Pressable
                    key={event.id}
                    accessibilityRole={event.case_id === undefined ? 'text' : 'link'}
                    aria-label={`${event.title}${event.case_id === undefined ? '' : ` — ${caseLabel(event.case_id)}`}`}
                    disabled={event.case_id === undefined}
                    onPress={() => onOpen(event)}
                  >
                    <Text
                      numberOfLines={1}
                      style={[
                        styles.cellEvent,
                        {
                          color: event.generated ? theme.colors.warning : theme.colors.ink,
                          fontFamily: theme.typography.body,
                          textDecorationLine: event.dismissed ? 'line-through' : 'none',
                        },
                      ]}
                    >
                      {event.title}
                    </Text>
                  </Pressable>
                ))}
                {todays.length > CELL_EVENTS ? (
                  <Text style={[styles.cellEvent, muted]}>+{todays.length - CELL_EVENTS} more</Text>
                ) : null}
              </View>
            );
          })}
        </View>
      ))}
    </View>
  );
}

const styles = StyleSheet.create({
  addRow: { gap: spacing.md, marginTop: spacing.lg },
  body: { fontSize: fontSizes.body, lineHeight: fontSizes.body * 1.5 },
  cell: { borderWidth: 0.5, flex: 1, gap: 2, minHeight: 96, minWidth: 0, padding: spacing.xs },
  cellDay: { justifyContent: 'center', minHeight: 32 },
  cellDayText: { fontSize: fontSizes.label },
  cellEvent: { fontSize: fontSizes.caption, lineHeight: fontSizes.caption * 1.4 },
  controls: {
    alignItems: 'center',
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.md,
    justifyContent: 'space-between',
    marginBottom: spacing.md,
  },
  controlsStacked: { alignItems: 'stretch', flexDirection: 'column' },
  dayBlock: { borderTopWidth: 1, gap: spacing.xs, paddingVertical: spacing.sm },
  dayLabel: { fontSize: fontSizes.label, fontWeight: '600' },
  eventMeta: { fontSize: fontSizes.caption },
  eventRow: {
    borderLeftWidth: 2,
    gap: 2,
    minHeight: 44,
    paddingLeft: spacing.sm,
    paddingVertical: spacing.xs,
  },
  eventTitle: { fontSize: fontSizes.body },
  grid: { borderWidth: 0.5 },
  gridRow: { flexDirection: 'row' },
  head: { gap: spacing.xs, marginBottom: spacing.lg },
  help: { fontSize: fontSizes.label, minHeight: fontSizes.label * 1.5 },
  list: { gap: 0 },
  nav: { flexDirection: 'row', gap: spacing.sm },
  pickers: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.sm },
  weekday: { flex: 1, fontSize: fontSizes.caption, fontWeight: '600', padding: spacing.xs },
  windowTitle: { marginBottom: spacing.xs },
});
