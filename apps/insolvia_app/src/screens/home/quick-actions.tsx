import { ApiValidationException, permits } from '@insolvia-ai/api-client';
import type { Case, FirmColleague, FirmMembership } from '@insolvia-ai/api-client';
import {
  Button,
  DateInput,
  Dialog,
  Field,
  Input,
  Select,
  Textarea,
} from '@insolvia-ai/design-system';
import { useRouter } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, View } from 'react-native';

import { useApi } from '@/api/use-api';
import { EMPTY_EVENT, EventForm, toDraft } from '@/components/event-form';
import type { EventFormState } from '@/components/event-form';
import { spacing } from '@/theme';

/** Which quick-add sheet, if any, is open. At most one at a time. */
type Open = 'task' | 'note' | 'event' | null;

/**
 * The dashboard's four quick actions (issue 14.7 / #359): open a case, add a
 * task, add a note, add an event. "Add a client" is not here — 14.1's client
 * domain has not landed (#354), and this row does not invent a fifth action
 * for a feature that does not exist yet.
 *
 * **What is reused, and what is not.** "New case" sends the caller to the
 * existing `/cases` screen, which already carries the create-case form — a
 * second copy here would drift from it the first time either changed. "Add
 * event" reuses {@link EventForm} whole, the exact component the calendar
 * screen's own "Add office event" uses, because a firm event needs no case
 * context. Tasks and notes are different: both belong to a case, and neither
 * `TasksPanel`'s form nor `NotesPanel` is written to work without one already
 * open — `NotesPanel` takes a case's already-loaded note list as a prop, and
 * the task form is a local, unexported part of its panel. Reusing either
 * from here would mean lifting a case's context into the dashboard for a
 * shortcut that only needs two fields. So each gets a MINIMAL sheet of its
 * own: which case, and the one field that names the thing — the server
 * fills in everything else `createTask`/`addNote` already default.
 */
export function QuickActions({ membership }: { membership: FirmMembership }) {
  const router = useRouter();
  const { call } = useApi();

  const [open, setOpen] = useState<Open>(null);
  const [cases, setCases] = useState<readonly Case[]>([]);
  const [colleagues, setColleagues] = useState<readonly FirmColleague[]>([]);
  const [casesLoaded, setCasesLoaded] = useState(false);

  const mayOpenCase = permits(membership.permissions.cases, 'add_edit');
  const mayAddTask = permits(membership.permissions.tasks, 'add_edit');
  const mayAddNote = permits(membership.permissions.notes, 'add_edit');
  const mayAddEvent = permits(membership.permissions.events, 'add_edit');

  // The case picker's options, loaded once on first need rather than on every
  // dashboard visit — neither quick-add sheet is opened most of the time.
  const ensureCasesLoaded = useCallback(async () => {
    if (casesLoaded) return;
    setCasesLoaded(true);
    try {
      const result = await call((client) => client.listCases({ limit: 100 }));
      if (result.ok) setCases(result.value.cases);
    } catch {
      // The picker simply offers nothing to choose — the same trade the
      // calendar screen's own case picker makes.
    }
  }, [call, casesLoaded]);

  useEffect(() => {
    if (open !== 'event' || colleagues.length > 0) return;
    let live = true;
    void (async () => {
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
  }, [call, colleagues.length, open]);

  return (
    <View style={styles.row}>
      <Button size="lg" onPress={() => router.push('/cases')} disabled={!mayOpenCase}>
        New case
      </Button>
      {mayAddTask ? (
        <Button
          size="lg"
          intent="secondary"
          onPress={() => {
            void ensureCasesLoaded();
            setOpen('task');
          }}
        >
          Add task
        </Button>
      ) : null}
      {mayAddNote ? (
        <Button
          size="lg"
          intent="secondary"
          onPress={() => {
            void ensureCasesLoaded();
            setOpen('note');
          }}
        >
          Add note
        </Button>
      ) : null}
      {mayAddEvent ? (
        <Button size="lg" intent="secondary" onPress={() => setOpen('event')}>
          Add event
        </Button>
      ) : null}

      <AddTaskDialog open={open === 'task'} cases={cases} onClose={() => setOpen(null)} />
      <AddNoteDialog open={open === 'note'} cases={cases} onClose={() => setOpen(null)} />
      <AddEventDialog
        open={open === 'event'}
        colleagues={colleagues}
        onClose={() => setOpen(null)}
      />
    </View>
  );
}

/** A case picker's options: chapter and district, the same label the
 * calendar screen's own case `Select` prints. */
function caseOptions(cases: readonly Case[]): { value: string; label: string }[] {
  return cases.map((matter) => ({
    value: matter.id,
    label: `Chapter ${matter.chapter} · ${matter.district}`,
  }));
}

function AddTaskDialog({
  open,
  cases,
  onClose,
}: {
  open: boolean;
  cases: readonly Case[];
  onClose: () => void;
}) {
  const { call } = useApi();
  const [caseId, setCaseId] = useState('');
  const [subject, setSubject] = useState('');
  const [dueDate, setDueDate] = useState('');
  const [errors, setErrors] = useState<Readonly<Record<string, string>>>({});
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const reset = () => {
    setCaseId('');
    setSubject('');
    setDueDate('');
    setErrors({});
    setError(null);
  };

  const save = async () => {
    if (caseId === '') {
      setError('Choose which case this task belongs to.');
      return;
    }
    setSaving(true);
    setErrors({});
    setError(null);
    try {
      const result = await call((client) =>
        client.createTask(caseId, {
          subject,
          dueDate: dueDate === '' ? undefined : dueDate,
        }),
      );
      if (result.ok) {
        reset();
        onClose();
      }
    } catch (cause) {
      if (cause instanceof ApiValidationException) {
        setErrors(cause.fields);
      } else {
        setError('Could not add that task. Try again.');
      }
    } finally {
      setSaving(false);
    }
  };

  return (
    <Dialog.Root
      open={open}
      onOpenChange={(next) => {
        if (!next) {
          reset();
          onClose();
        }
      }}
    >
      <Dialog.Popup>
        <Dialog.Title>Add a task</Dialog.Title>
        <View style={styles.form}>
          <Field.Root name="case" invalid={error !== null}>
            <Field.Label>Case</Field.Label>
            <Select
              options={[{ value: '', label: 'Choose a case' }, ...caseOptions(cases)]}
              value={caseId}
              onValueChange={setCaseId}
            />
            {error === null ? null : <Field.Error match>{error}</Field.Error>}
          </Field.Root>

          <Field.Root name="subject" invalid={Boolean(errors.subject)}>
            <Field.Label>Subject</Field.Label>
            <Input value={subject} onValueChange={setSubject} autoCorrect={false} />
            {errors.subject ? <Field.Error match>{errors.subject}</Field.Error> : null}
          </Field.Root>

          <Field.Root name="dueDate" invalid={Boolean(errors.dueDate)}>
            <Field.Label>Due date (optional)</Field.Label>
            <DateInput
              value={dueDate}
              onValueChange={(next, status) => {
                if (status === 'incomplete') return;
                setDueDate(next);
              }}
            />
            {errors.dueDate ? <Field.Error match>{errors.dueDate}</Field.Error> : null}
          </Field.Root>

          <View style={styles.actions}>
            <Button
              size="lg"
              disabled={saving || subject.trim() === ''}
              onPress={() => void save()}
            >
              Add task
            </Button>
            <Dialog.Close>Cancel</Dialog.Close>
          </View>
        </View>
      </Dialog.Popup>
    </Dialog.Root>
  );
}

function AddNoteDialog({
  open,
  cases,
  onClose,
}: {
  open: boolean;
  cases: readonly Case[];
  onClose: () => void;
}) {
  const { call } = useApi();
  const [caseId, setCaseId] = useState('');
  const [text, setText] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const reset = () => {
    setCaseId('');
    setText('');
    setError(null);
  };

  const save = async () => {
    if (caseId === '') {
      setError('Choose which case this note belongs to.');
      return;
    }
    setSaving(true);
    setError(null);
    try {
      const result = await call((client) => client.addNote(caseId, { text }));
      if (result.ok) {
        reset();
        onClose();
      }
    } catch (cause) {
      setError(
        cause instanceof ApiValidationException
          ? (cause.fields.text ?? 'Could not save that note.')
          : 'Could not save that note. Try again.',
      );
    } finally {
      setSaving(false);
    }
  };

  return (
    <Dialog.Root
      open={open}
      onOpenChange={(next) => {
        if (!next) {
          reset();
          onClose();
        }
      }}
    >
      <Dialog.Popup>
        <Dialog.Title>Add a note</Dialog.Title>
        <View style={styles.form}>
          <Field.Root name="case" invalid={error !== null}>
            <Field.Label>Case</Field.Label>
            <Select
              options={[{ value: '', label: 'Choose a case' }, ...caseOptions(cases)]}
              value={caseId}
              onValueChange={setCaseId}
            />
            {error === null ? null : <Field.Error match>{error}</Field.Error>}
          </Field.Root>

          <Field.Root name="text">
            <Field.Label>Note</Field.Label>
            <Textarea value={text} onValueChange={setText} />
          </Field.Root>

          <View style={styles.actions}>
            <Button size="lg" disabled={saving || text.trim() === ''} onPress={() => void save()}>
              Add note
            </Button>
            <Dialog.Close>Cancel</Dialog.Close>
          </View>
        </View>
      </Dialog.Popup>
    </Dialog.Root>
  );
}

function AddEventDialog({
  open,
  colleagues,
  onClose,
}: {
  open: boolean;
  colleagues: readonly FirmColleague[];
  onClose: () => void;
}) {
  const { call } = useApi();
  const [form, setForm] = useState<EventFormState>(EMPTY_EVENT);
  const [errors, setErrors] = useState<Readonly<Record<string, string>>>({});
  const [saving, setSaving] = useState(false);

  const save = async () => {
    setSaving(true);
    setErrors({});
    try {
      const result = await call((client) => client.addFirmEvent(toDraft(form)));
      if (result.ok) {
        setForm(EMPTY_EVENT);
        onClose();
      }
    } catch (cause) {
      if (cause instanceof ApiValidationException) {
        setErrors(cause.fields);
      }
    } finally {
      setSaving(false);
    }
  };

  return (
    <Dialog.Root
      open={open}
      onOpenChange={(next) => {
        if (!next) {
          setForm(EMPTY_EVENT);
          setErrors({});
          onClose();
        }
      }}
    >
      <Dialog.Popup>
        <Dialog.Title>Add a firm event</Dialog.Title>
        <EventForm
          form={form}
          colleagues={colleagues}
          errors={errors}
          saving={saving}
          saveLabel="Add event"
          onChange={setForm}
          onSave={() => void save()}
          onCancel={() => {
            setForm(EMPTY_EVENT);
            setErrors({});
            onClose();
          }}
        />
      </Dialog.Popup>
    </Dialog.Root>
  );
}

const styles = StyleSheet.create({
  actions: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.sm },
  form: { gap: spacing.md, marginTop: spacing.sm },
  row: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.md },
});
