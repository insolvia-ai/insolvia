import type { CalendarEventDraft, FirmColleague } from '@insolvia-ai/api-client';
import { Button, Checkbox, DateInput, Field, Input, Textarea } from '@insolvia-ai/design-system';
import { StyleSheet, Text, View } from 'react-native';

import { fontSizes, spacing, useTheme } from '@/theme';

/**
 * What the form holds while somebody types (issue 14.6 / #358). Strings
 * throughout, so a half-typed time is never a parse error; `toDraft` turns
 * it into the request the API validates — the server owns validation (ADR
 * 0001) and its field messages render as they come.
 */
export interface EventFormState {
  readonly title: string;
  readonly allDay: boolean;
  /** `YYYY-MM-DD` either way; the time fields are only read when timed. */
  readonly startDate: string;
  readonly startTime: string;
  readonly endDate: string;
  readonly endTime: string;
  readonly location: string;
  readonly description: string;
  readonly attendees: readonly string[];
}

export const EMPTY_EVENT: EventFormState = {
  title: '',
  allDay: true,
  startDate: '',
  startTime: '09:00',
  endDate: '',
  endTime: '10:00',
  location: '',
  description: '',
  attendees: [],
};

function undefinedIfBlank(value: string): string | undefined {
  const trimmed = value.trim();
  return trimmed === '' ? undefined : trimmed;
}

/**
 * A local date and clock time as the RFC 3339 instant the API stores — the
 * reader's own zone, because "3pm" on the form means 3pm where they sit.
 */
function localInstant(date: string, time: string): string {
  const [hours, minutes] = time.split(':').map(Number);
  const [year, month, day] = date.split('-').map(Number);
  const local = new Date(year ?? 0, (month ?? 1) - 1, day ?? 1, hours ?? 0, minutes ?? 0, 0);
  return local.toISOString().replace(/\.\d{3}Z$/u, 'Z');
}

/** The form's request body — see {@link EventFormState}. */
export function toDraft(form: EventFormState): CalendarEventDraft {
  if (form.allDay) {
    return {
      title: form.title,
      start: form.startDate,
      end: undefinedIfBlank(form.endDate),
      all_day: true,
      location: undefinedIfBlank(form.location),
      description: undefinedIfBlank(form.description),
      attendees: form.attendees.length === 0 ? undefined : form.attendees,
    };
  }
  const endDate = form.endDate.trim() === '' ? form.startDate : form.endDate;
  return {
    title: form.title,
    start: form.startDate === '' ? '' : localInstant(form.startDate, form.startTime),
    end: form.startDate === '' ? undefined : localInstant(endDate, form.endTime),
    all_day: false,
    location: undefinedIfBlank(form.location),
    description: undefinedIfBlank(form.description),
    attendees: form.attendees.length === 0 ? undefined : form.attendees,
  };
}

/**
 * A stored event back into the form: an instant becomes the reader's local
 * date and clock time, the inverse of {@link toDraft}.
 */
export function fromEvent(event: {
  readonly title: string;
  readonly all_day: boolean;
  readonly start: string;
  readonly end: string;
  readonly location?: string | undefined;
  readonly description?: string | undefined;
  readonly attendees: readonly string[];
}): EventFormState {
  if (event.all_day) {
    return {
      ...EMPTY_EVENT,
      title: event.title,
      allDay: true,
      startDate: event.start,
      endDate: event.end === event.start ? '' : event.end,
      location: event.location ?? '',
      description: event.description ?? '',
      attendees: event.attendees,
    };
  }
  const start = new Date(event.start);
  const end = new Date(event.end);
  const pad = (n: number) => String(n).padStart(2, '0');
  const dateOf = (d: Date) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
  const timeOf = (d: Date) => `${pad(d.getHours())}:${pad(d.getMinutes())}`;
  return {
    title: event.title,
    allDay: false,
    startDate: dateOf(start),
    startTime: timeOf(start),
    endDate: dateOf(end) === dateOf(start) ? '' : dateOf(end),
    endTime: timeOf(end),
    location: event.location ?? '',
    description: event.description ?? '',
    attendees: event.attendees,
  };
}

/**
 * The one form for a hand-made event, shared by the case overview's panel
 * and the calendar's office-event form so the two cannot drift.
 *
 * Every input is a package control inside a `Field.Root` — the app's rule —
 * and the time fields are plain `Input`s with `HH:MM` rather than a
 * `DateInput mode="time"`, because two wheels for a start and an end is
 * more instrument than a court hearing's time needs; the server validates
 * what arrives either way.
 */
export function EventForm({
  form,
  colleagues,
  errors,
  saving,
  saveLabel,
  onChange,
  onSave,
  onCancel,
}: {
  form: EventFormState;
  colleagues: readonly FirmColleague[];
  errors: Readonly<Record<string, string>>;
  saving: boolean;
  saveLabel: string;
  onChange: (next: EventFormState) => void;
  onSave: () => void;
  onCancel: () => void;
}) {
  const theme = useTheme();
  const ink = { color: theme.colors.ink, fontFamily: theme.typography.body };
  const set = <K extends keyof EventFormState>(key: K, value: EventFormState[K]) =>
    onChange({ ...form, [key]: value });

  return (
    <View style={styles.form}>
      <Field.Root name="title" invalid={Boolean(errors.title)}>
        <Field.Label>Title</Field.Label>
        <Input value={form.title} onValueChange={(next) => set('title', next)} />
        {errors.title ? <Field.Error match>{errors.title}</Field.Error> : null}
      </Field.Root>

      <View style={styles.checkboxRow}>
        <Checkbox.Root
          aria-label="All day"
          checked={form.allDay}
          onCheckedChange={(next) => set('allDay', next)}
        >
          <Checkbox.Indicator>✓</Checkbox.Indicator>
        </Checkbox.Root>
        <Text style={[styles.checkboxLabel, ink]}>All day</Text>
      </View>

      <Field.Root name="start" invalid={Boolean(errors.start)}>
        <Field.Label>{form.allDay ? 'Date' : 'Start date'}</Field.Label>
        <DateInput
          value={form.startDate}
          onValueChange={(next, status) => {
            if (status === 'incomplete') return;
            set('startDate', next);
          }}
        />
        {errors.start ? <Field.Error match>{errors.start}</Field.Error> : null}
      </Field.Root>

      {form.allDay ? null : (
        <Field.Root name="start_time">
          <Field.Label>Start time</Field.Label>
          <Input
            value={form.startTime}
            onValueChange={(next) => set('startTime', next)}
            placeholder="HH:MM"
            autoCorrect={false}
          />
        </Field.Root>
      )}

      <Field.Root name="end" invalid={Boolean(errors.end)}>
        <Field.Label>{form.allDay ? 'Last day (optional)' : 'End date (optional)'}</Field.Label>
        <DateInput
          value={form.endDate}
          onValueChange={(next, status) => {
            if (status === 'incomplete') return;
            set('endDate', next);
          }}
        />
        {errors.end ? <Field.Error match>{errors.end}</Field.Error> : null}
      </Field.Root>

      {form.allDay ? null : (
        <Field.Root name="end_time">
          <Field.Label>End time</Field.Label>
          <Input
            value={form.endTime}
            onValueChange={(next) => set('endTime', next)}
            placeholder="HH:MM"
            autoCorrect={false}
          />
        </Field.Root>
      )}

      <Field.Root name="location" invalid={Boolean(errors.location)}>
        <Field.Label>Location</Field.Label>
        <Input value={form.location} onValueChange={(next) => set('location', next)} />
        {errors.location ? <Field.Error match>{errors.location}</Field.Error> : null}
      </Field.Root>

      <Field.Root name="description" invalid={Boolean(errors.description)}>
        <Field.Label>Notes</Field.Label>
        <Textarea value={form.description} onValueChange={(next) => set('description', next)} />
        {errors.description ? <Field.Error match>{errors.description}</Field.Error> : null}
      </Field.Root>

      {colleagues.length === 0 ? null : (
        <View style={styles.attendees}>
          <Text style={[styles.attendeesLabel, ink]}>Attending</Text>
          {colleagues.map((colleague) => {
            const checked = form.attendees.includes(colleague.subject);
            return (
              <View key={colleague.subject} style={styles.checkboxRow}>
                <Checkbox.Root
                  aria-label={colleague.displayName || colleague.subject}
                  checked={checked}
                  onCheckedChange={(next) =>
                    set(
                      'attendees',
                      next
                        ? [...form.attendees, colleague.subject]
                        : form.attendees.filter((subject) => subject !== colleague.subject),
                    )
                  }
                >
                  <Checkbox.Indicator>✓</Checkbox.Indicator>
                </Checkbox.Root>
                <Text style={[styles.checkboxLabel, ink]}>
                  {colleague.displayName || colleague.subject}
                </Text>
              </View>
            );
          })}
        </View>
      )}

      <View style={styles.actions}>
        <Button size="lg" disabled={saving} onPress={onSave}>
          {saveLabel}
        </Button>
        <Button size="lg" intent="secondary" onPress={onCancel}>
          Cancel
        </Button>
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  actions: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.sm },
  attendees: { gap: spacing.xs },
  attendeesLabel: { fontSize: fontSizes.label, fontWeight: '600' },
  checkboxLabel: { fontSize: fontSizes.body },
  checkboxRow: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm },
  form: { gap: spacing.md },
});
