import { ApiValidationException, permits } from '@insolvia-ai/api-client';
import type { CalendarEvent, FirmColleague } from '@insolvia-ai/api-client';
import { Button, DateInput, Field } from '@insolvia-ai/design-system';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useMembership } from '@/api/me';
import { useApi } from '@/api/use-api';
import { useCase } from '@/components/case-shell';
import { EMPTY_EVENT, EventForm, fromEvent, toDraft } from '@/components/event-form';
import type { EventFormState } from '@/components/event-form';
import { Heading } from '@/components/heading';
import { formatLong, formatTime } from '@/screens/calendar/dates';
import { fontSizes, spacing, useTheme } from '@/theme';

type ListState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly events: readonly CalendarEvent[] }
  | { readonly kind: 'error'; readonly message: string };

type Mode =
  | { readonly kind: 'list' }
  | { readonly kind: 'form'; readonly id: string | null; readonly draft: EventFormState };

/**
 * The case's events and deadlines, on its overview (issue 14.6 / #358).
 *
 * TWO DATES ABOVE THE LIST, and they are the point. The petition date and
 * the first § 341 date are the anchors every generated deadline counts from;
 * recording one here PATCHes the case, and the server regenerates the
 * case's deadlines in the same request — so the list under them fills in,
 * or moves, the moment a date is saved. Until #355 gives the case its full
 * post-filing lifecycle these two fields are the whole of it, and this is
 * where they live.
 *
 * Generated deadlines name their rule and offer one control — dismiss —
 * because their dates are the rule's; hand-made events are ordinary and
 * edit and remove like a library creditor. Both read from
 * `GET /v1/cases/{id}/events`, dismissed ones included and struck through:
 * a deadline somebody waved away should stay visible on the case that
 * waved it.
 */
export function EventsPanel({ colleagues }: { colleagues: readonly FirmColleague[] }) {
  const theme = useTheme();
  const { call } = useApi();
  const { caseId, matter, reload } = useCase();
  const membership = useMembership();

  const [list, setList] = useState<ListState>({ kind: 'loading' });
  const [mode, setMode] = useState<Mode>({ kind: 'list' });
  const [errors, setErrors] = useState<Readonly<Record<string, string>>>({});
  const [dateErrors, setDateErrors] = useState<Readonly<Record<string, string>>>({});
  const [saving, setSaving] = useState(false);
  const [status, setStatus] = useState('');

  const mayView = membership != null && permits(membership.permissions.events, 'view_only');
  const mayChange = membership != null && permits(membership.permissions.events, 'add_edit');
  const mayEditCase = membership != null && permits(membership.permissions.cases, 'add_edit');

  const load = useCallback(async () => {
    try {
      const result = await call((client) => client.listCaseEvents(caseId));
      if (result.ok) setList({ kind: 'ready', events: result.value });
    } catch {
      setList({ kind: 'error', message: 'Could not load this case’s events.' });
    }
  }, [call, caseId]);

  useEffect(() => {
    if (mayView) void load();
  }, [load, mayView]);

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const ink = { color: theme.colors.ink, fontFamily: theme.typography.body };

  if (!mayView) return null;

  const saveDate = async (field: 'filedAt' | 'meeting341At', value: string) => {
    setDateErrors({});
    setStatus('Saving…');
    try {
      const result = await call((client) =>
        client.updateCase(caseId, { [field]: value === '' ? null : value }),
      );
      if (!result.ok) return;
      setStatus(value === '' ? 'Date cleared' : 'Date saved — deadlines updated');
      await Promise.all([reload(), load()]);
    } catch (cause) {
      if (cause instanceof ApiValidationException) {
        setDateErrors(cause.fields);
        setStatus('That date needs attention.');
      } else {
        setStatus('Could not save the date. Try again.');
      }
    }
  };

  const persist = async (form: Mode & { readonly kind: 'form' }) => {
    setSaving(true);
    setStatus('Saving…');
    setErrors({});
    try {
      const draft = toDraft(form.draft);
      const result = await call((client) =>
        form.id === null
          ? client.addCaseEvent(caseId, draft)
          : client.updateCaseEvent(caseId, form.id, draft),
      );
      if (!result.ok) {
        setStatus('');
        return;
      }
      setStatus('Saved');
      setMode({ kind: 'list' });
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

  const dismiss = async (event: CalendarEvent) => {
    setStatus(event.dismissed ? 'Restoring…' : 'Dismissing…');
    try {
      const result = await call((client) =>
        client.dismissCaseEvent(caseId, event.id, !event.dismissed),
      );
      if (!result.ok) return;
      setList((current) =>
        current.kind === 'ready'
          ? {
              kind: 'ready',
              events: current.events.map((e) => (e.id === result.value.id ? result.value : e)),
            }
          : current,
      );
      setStatus(event.dismissed ? 'Restored' : 'Dismissed');
    } catch {
      setStatus('Could not change it. Try again.');
    }
  };

  const remove = async (event: CalendarEvent) => {
    setStatus('Removing…');
    try {
      const result = await call((client) => client.removeCaseEvent(caseId, event.id));
      if (!result.ok) return;
      setList((current) =>
        current.kind === 'ready'
          ? { kind: 'ready', events: current.events.filter((e) => e.id !== event.id) }
          : current,
      );
      setStatus('Removed');
    } catch {
      setStatus('Could not remove it. Try again.');
    }
  };

  const when = (event: CalendarEvent): string => {
    const time = formatTime(event);
    if (event.all_day) {
      return event.start === event.end
        ? formatLong(event.start)
        : `${formatLong(event.start)} – ${formatLong(event.end)}`;
    }
    return `${formatLong(event.start.slice(0, 10))}${time === null ? '' : ` at ${time}`}`;
  };

  return (
    <View
      style={[
        styles.section,
        {
          backgroundColor: theme.colors.card,
          borderColor: theme.colors.line,
          borderRadius: theme.radii.lg,
        },
      ]}
    >
      <View style={[styles.sectionHead, { borderBottomColor: theme.colors.line }]}>
        <Heading level={2} size="body">
          Events and deadlines
        </Heading>
        <Text style={[styles.sectionMeta, muted]}>
          {matter.filedAt === undefined
            ? 'deadlines appear once the petition date is recorded'
            : 'counted under Rule 9006(a) from the dates below'}
        </Text>
      </View>

      <View style={styles.dates}>
        <Field.Root name="filed_at" invalid={Boolean(dateErrors.filed_at)}>
          <Field.Label>Petition filed on</Field.Label>
          <DateInput
            value={matter.filedAt ?? ''}
            disabled={!mayEditCase}
            onValueChange={(next, dateStatus) => {
              if (dateStatus === 'incomplete') return;
              if (next !== (matter.filedAt ?? '')) void saveDate('filedAt', next);
            }}
          />
          <Field.Description>The order for relief in a voluntary case.</Field.Description>
          {dateErrors.filed_at ? <Field.Error match>{dateErrors.filed_at}</Field.Error> : null}
        </Field.Root>
        <Field.Root name="meeting_341_at" invalid={Boolean(dateErrors.meeting_341_at)}>
          <Field.Label>§ 341 meeting first set for</Field.Label>
          <DateInput
            value={matter.meeting341At ?? ''}
            disabled={!mayEditCase}
            onValueChange={(next, dateStatus) => {
              if (dateStatus === 'incomplete') return;
              if (next !== (matter.meeting341At ?? '')) void saveDate('meeting341At', next);
            }}
          />
          <Field.Description>
            The first date on the court’s notice — a continued meeting does not move it.
          </Field.Description>
          {dateErrors.meeting_341_at ? (
            <Field.Error match>{dateErrors.meeting_341_at}</Field.Error>
          ) : null}
        </Field.Root>
      </View>

      <Text
        aria-live={status.includes('attention') ? 'assertive' : 'polite'}
        style={[styles.help, muted]}
      >
        {status}
      </Text>

      {mode.kind === 'form' ? (
        <EventForm
          form={mode.draft}
          colleagues={colleagues}
          errors={errors}
          saving={saving}
          saveLabel={mode.id === null ? 'Save event' : 'Save changes'}
          onChange={(draft) => setMode({ ...mode, draft })}
          onSave={() => void persist(mode)}
          onCancel={() => {
            setErrors({});
            setStatus('');
            setMode({ kind: 'list' });
          }}
        />
      ) : (
        <View style={styles.list}>
          {list.kind !== 'ready' ? (
            <Text
              aria-live={list.kind === 'error' ? 'assertive' : 'polite'}
              style={[styles.body, muted]}
            >
              {list.kind === 'loading' ? 'Loading events…' : list.message}
            </Text>
          ) : list.events.length === 0 ? (
            <Text style={[styles.body, muted]}>Nothing on this case’s calendar yet.</Text>
          ) : (
            <View role="list" style={styles.list}>
              {list.events.map((event) => (
                <View
                  key={event.id}
                  role="listitem"
                  style={[styles.row, { borderTopColor: theme.colors.line }]}
                >
                  <View style={styles.rowBody}>
                    <Text style={[styles.rowTitle, ink, event.dismissed ? styles.dismissed : null]}>
                      {event.title}
                    </Text>
                    <Text style={[styles.rowMeta, muted]}>
                      {[when(event), event.rule_citation, event.location]
                        .filter((part): part is string => part !== undefined)
                        .join(' · ')}
                      {event.dismissed ? ' · dismissed' : ''}
                    </Text>
                  </View>
                  {mayChange ? (
                    <View style={styles.rowActions}>
                      {event.generated ? (
                        <Button
                          size="lg"
                          intent="secondary"
                          aria-label={`${event.dismissed ? 'Restore' : 'Dismiss'} ${event.title}`}
                          onPress={() => void dismiss(event)}
                        >
                          {event.dismissed ? 'Restore' : 'Dismiss'}
                        </Button>
                      ) : (
                        <>
                          <Button
                            size="lg"
                            intent="secondary"
                            aria-label={`Edit ${event.title}`}
                            onPress={() => {
                              setErrors({});
                              setStatus('');
                              setMode({ kind: 'form', id: event.id, draft: fromEvent(event) });
                            }}
                          >
                            Edit
                          </Button>
                          <Button
                            size="lg"
                            intent="secondary"
                            aria-label={`Remove ${event.title}`}
                            onPress={() => void remove(event)}
                          >
                            Remove
                          </Button>
                        </>
                      )}
                    </View>
                  ) : null}
                </View>
              ))}
            </View>
          )}
          {mayChange ? (
            <View style={styles.rowActions}>
              <Button
                size="lg"
                onPress={() => {
                  setErrors({});
                  setStatus('');
                  setMode({ kind: 'form', id: null, draft: EMPTY_EVENT });
                }}
              >
                Add event
              </Button>
            </View>
          ) : null}
        </View>
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  body: { fontSize: fontSizes.body, lineHeight: fontSizes.body * 1.5 },
  dates: { gap: spacing.md, marginBottom: spacing.sm },
  dismissed: { textDecorationLine: 'line-through' },
  help: { fontSize: fontSizes.label, minHeight: fontSizes.label * 1.5 },
  list: { gap: spacing.sm },
  row: { borderTopWidth: 1, gap: spacing.sm, paddingTop: spacing.sm },
  rowActions: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.sm },
  rowBody: { gap: 2 },
  rowMeta: { fontSize: fontSizes.caption, lineHeight: fontSizes.caption * 1.5 },
  rowTitle: { fontSize: fontSizes.label, fontWeight: '600', lineHeight: fontSizes.label * 1.5 },
  section: { borderWidth: 1, padding: spacing.lg },
  sectionHead: {
    alignItems: 'baseline',
    borderBottomWidth: 1,
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.md,
    justifyContent: 'space-between',
    marginBottom: spacing.md,
    paddingBottom: spacing.md,
  },
  sectionMeta: { fontSize: fontSizes.label, lineHeight: fontSizes.label * 1.5 },
});
