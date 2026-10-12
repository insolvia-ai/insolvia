import { ApiValidationException, permits } from '@insolvia-ai/api-client';
import type { Note } from '@insolvia-ai/api-client';
import { Button, Field, Textarea } from '@insolvia-ai/design-system';
import { useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useMe } from '@/api/me';
import { useApi } from '@/api/use-api';
import { fontSizes, spacing, useTheme } from '@/theme';

/**
 * Free-text notes, on a case or on one of its forms (issue 14.5 / #357).
 *
 * DELIBERATELY CONTROLLED, not self-fetching: both call sites already read
 * the case's whole note list for their own reasons — the case overview shows
 * everything, the forms hub needs every row's count — so a second listNotes
 * per mount here would be a request this screen does not need. The caller
 * owns `notes` and re-reads after `onChanged`; this component only writes.
 *
 * `formSeries`, present, does two things: a note ADDED here is anchored to
 * that form (never asked — the panel is already scoped to it), and the
 * anchor badge on each row is hidden, since every row already shares it.
 * Absent (the case overview's whole-case feed), a note added here is
 * case-level, and any anchored note in `notes` shows which form it concerns.
 */
export interface NotesPanelProps {
  readonly caseId: string;
  readonly notes: readonly Note[];
  readonly formSeries?: string;
  /** Re-read the note list — called after every successful add, edit and delete. */
  readonly onChanged: () => void;
}

/** `"form/b106g"` → `"B106G"` — the same short form the forms hub and the
 * case overview's own problem list already print (see `sourceLabel` in
 * `screens/case-overview`, which this mirrors rather than imports: a
 * component does not reach into a screen for a three-line formatter). */
function anchorLabel(series: string): string {
  return series.startsWith('form/') ? series.slice('form/'.length).toUpperCase() : series;
}

export function NotesPanel({ caseId, notes, formSeries, onChanged }: NotesPanelProps) {
  const theme = useTheme();
  const { call } = useApi();
  const me = useMe();

  const [draft, setDraft] = useState('');
  const [adding, setAdding] = useState(false);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editingText, setEditingText] = useState('');
  const [busyId, setBusyId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const subject = me.kind === 'ready' ? me.principal.subject : undefined;
  const firm = me.kind === 'ready' ? me.principal.firm : undefined;
  // Fail closed exactly as the server does: no firm answer yet, no button.
  const mayAdd = firm !== undefined && permits(firm.permissions.notes, 'add_edit');
  const isAdmin = firm?.isAdmin ?? false;
  const mayEdit = (note: Note) => mayAdd && (isAdmin || note.author_subject === subject);

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };

  const handleAdd = async () => {
    const text = draft.trim();
    if (text === '') return;
    setAdding(true);
    setError(null);
    try {
      const result = await call((client) =>
        client.addNote(
          caseId,
          formSeries === undefined ? { text } : { text, form_series: formSeries },
        ),
      );
      if (result.ok) {
        setDraft('');
        onChanged();
      }
    } catch (cause) {
      setError(
        cause instanceof ApiValidationException
          ? (cause.fields.text ?? 'Could not save that note.')
          : 'Could not save that note. Try again.',
      );
    } finally {
      setAdding(false);
    }
  };

  const startEdit = (note: Note) => {
    setEditingId(note.id);
    setEditingText(note.text ?? '');
    setError(null);
  };

  const handleSaveEdit = async (note: Note) => {
    const text = editingText.trim();
    if (text === '') return;
    setBusyId(note.id);
    setError(null);
    try {
      // PUT replaces the record whole, so the anchor has to be sent back
      // explicitly — THE NOTE'S OWN `form_series`, never this panel's
      // `formSeries` prop. On the case overview `formSeries` is undefined
      // for every note; sending that through unchanged would silently strip
      // the anchor off a form-anchored note the first time someone edited it
      // from the whole-case feed rather than from the form's own panel.
      const result = await call((client) =>
        client.putNote(caseId, note.id, { text, form_series: note.form_series }),
      );
      if (result.ok) {
        setEditingId(null);
        onChanged();
      }
    } catch (cause) {
      setError(
        cause instanceof ApiValidationException
          ? (cause.fields.text ?? 'Could not save that note.')
          : 'Could not save that note. Try again.',
      );
    } finally {
      setBusyId(null);
    }
  };

  const handleDelete = async (noteId: string) => {
    setBusyId(noteId);
    setError(null);
    try {
      const result = await call((client) => client.deleteNote(caseId, noteId));
      if (result.ok) onChanged();
    } catch {
      setError('Could not remove that note. Try again.');
    } finally {
      setBusyId(null);
    }
  };

  return (
    <View style={styles.panel}>
      {error === null ? null : (
        <Text
          aria-live="assertive"
          style={[styles.error, { color: theme.colors.danger, fontFamily: theme.typography.body }]}
        >
          {error}
        </Text>
      )}

      {notes.length === 0 ? (
        <Text style={[styles.empty, muted]}>No notes yet.</Text>
      ) : (
        <View role="list" style={styles.list}>
          {notes.map((note) => {
            const editingThis = editingId === note.id;
            return (
              <View
                key={note.id}
                role="listitem"
                style={[styles.row, { borderColor: theme.colors.line }]}
              >
                {editingThis ? (
                  <View style={styles.editForm}>
                    <Field.Root name={`note-${note.id}`}>
                      <Field.Label>Note</Field.Label>
                      <Textarea value={editingText} onValueChange={setEditingText} />
                    </Field.Root>
                    <View style={styles.rowActions}>
                      <Button
                        size="lg"
                        onPress={() => void handleSaveEdit(note)}
                        disabled={busyId === note.id}
                      >
                        Save
                      </Button>
                      <Button size="lg" intent="secondary" onPress={() => setEditingId(null)}>
                        Cancel
                      </Button>
                    </View>
                  </View>
                ) : (
                  <>
                    <Text
                      style={[
                        styles.body,
                        { color: theme.colors.ink, fontFamily: theme.typography.body },
                      ]}
                    >
                      {note.text}
                    </Text>
                    <View style={styles.metaRow}>
                      <Text style={[styles.meta, muted]}>
                        {note.author_name} · {note.created_at.slice(0, 10)}
                        {formSeries === undefined && note.form_series !== undefined
                          ? ` · ${anchorLabel(note.form_series)}`
                          : ''}
                      </Text>
                      {mayEdit(note) ? (
                        <View style={styles.rowActions}>
                          <Button
                            size="lg"
                            intent="secondary"
                            aria-label="Edit note"
                            onPress={() => startEdit(note)}
                            disabled={busyId === note.id}
                          >
                            Edit
                          </Button>
                          <Button
                            size="lg"
                            intent="secondary"
                            aria-label="Delete note"
                            onPress={() => void handleDelete(note.id)}
                            disabled={busyId === note.id}
                          >
                            Delete
                          </Button>
                        </View>
                      ) : null}
                    </View>
                  </>
                )}
              </View>
            );
          })}
        </View>
      )}

      {mayAdd ? (
        <View style={styles.composer}>
          <Field.Root name="new-note">
            <Field.Label>Add a note</Field.Label>
            <Textarea value={draft} onValueChange={setDraft} />
          </Field.Root>
          <Button
            size="lg"
            onPress={() => void handleAdd()}
            disabled={adding || draft.trim() === ''}
          >
            Add note
          </Button>
        </View>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  body: {
    fontSize: fontSizes.label,
    lineHeight: fontSizes.label * 1.5,
  },
  composer: {
    gap: spacing.sm,
  },
  editForm: {
    gap: spacing.sm,
  },
  empty: {
    fontSize: fontSizes.label,
  },
  error: {
    fontSize: fontSizes.label,
  },
  list: {
    gap: spacing.md,
  },
  meta: {
    fontSize: fontSizes.caption,
  },
  metaRow: {
    alignItems: 'center',
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.sm,
    justifyContent: 'space-between',
  },
  panel: {
    gap: spacing.md,
  },
  row: {
    borderBottomWidth: 1,
    gap: spacing.xs,
    paddingBottom: spacing.md,
  },
  rowActions: {
    flexDirection: 'row',
    gap: spacing.sm,
  },
});
