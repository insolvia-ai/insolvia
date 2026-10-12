import { ApiException, ApiValidationException } from '@insolvia-ai/api-client';
import type { FirmClient } from '@insolvia-ai/api-client';
import { AlertDialog, Button, Field, Select } from '@insolvia-ai/design-system';
import { useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useApi } from '@/api/use-api';
import { displayName, sortName } from '@/components/client-names';
import { fontSizes, spacing, useTheme } from '@/theme';

type Candidates =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly clients: readonly FirmClient[] }
  | { readonly kind: 'error' };

/**
 * "Merge into another client…" (ADR 0022's PR 7 / #354): fold THIS client
 * into the one chosen here, which survives and keeps its id.
 *
 * An `AlertDialog`, not a plain dialog: merging cannot be undone, so it asks
 * with no tap-outside dismissal and says exactly what moves and what does
 * not — the cases move to the survivor, the details copied into those cases
 * stay as they are, and this record is archived as "merged", read-only.
 *
 * The choices are the directory's ACTIVE clients other than this one — the
 * only ones the server would accept. What the server still refuses (both of
 * them debtors on one case, a merge already under way) comes back as a 409
 * whose message is shown here as it is: it already says what to do.
 */
export function MergeDialog({
  client,
  open,
  onClose,
  onMerged,
}: {
  client: FirmClient;
  open: boolean;
  onClose: () => void;
  onMerged: (survivor: FirmClient) => void;
}) {
  const theme = useTheme();
  const { call } = useApi();
  const [candidates, setCandidates] = useState<Candidates>({ kind: 'loading' });
  const [choice, setChoice] = useState<string | null>(null);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!open) return;
    let cancelled = false;
    setChoice(null);
    setError('');
    setCandidates({ kind: 'loading' });
    void (async () => {
      try {
        const result = await call((api) => api.listFirmClients());
        if (result.ok && !cancelled) {
          setCandidates({
            kind: 'ready',
            clients: result.value.filter(
              (other) => other.id !== client.id && other.status === 'active',
            ),
          });
        }
      } catch {
        if (!cancelled) setCandidates({ kind: 'error' });
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [call, client.id, open]);

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const danger = { color: theme.colors.danger, fontFamily: theme.typography.body };

  const merge = async () => {
    if (choice === null) {
      setError('Choose the client to keep.');
      return;
    }
    setBusy(true);
    setError('');
    try {
      const result = await call((api) => api.mergeFirmClient(choice, client.id));
      if (result.ok) onMerged(result.value.client);
    } catch (cause) {
      setError(
        cause instanceof ApiValidationException
          ? (Object.values(cause.fields)[0] ?? 'Could not merge. Try again.')
          : cause instanceof ApiException && cause.statusCode === 409
            ? conflictMessage(cause)
            : 'Could not merge. Try again.',
      );
    } finally {
      setBusy(false);
    }
  };

  const name = displayName(client);

  return (
    <AlertDialog.Root
      open={open}
      onOpenChange={(next) => {
        if (!next) onClose();
      }}
    >
      <AlertDialog.Popup>
        <AlertDialog.Title>{`Merge ${name} into another client?`}</AlertDialog.Title>
        <AlertDialog.Description>
          {`Choose the client to keep. Every case of ${name} moves to them; the details already ` +
            `copied into those cases stay exactly as they are. ${name} is then archived as ` +
            'merged, and this cannot be undone.'}
        </AlertDialog.Description>
        {candidates.kind === 'loading' ? (
          <Text aria-live="polite" style={[styles.body, muted]}>
            Loading your clients…
          </Text>
        ) : candidates.kind === 'error' ? (
          <Text aria-live="assertive" style={[styles.body, danger]}>
            Could not load your client list.
          </Text>
        ) : candidates.clients.length === 0 ? (
          <Text style={[styles.body, muted]}>There is no other active client to merge into.</Text>
        ) : (
          <Field.Root name="merged_into" invalid={error !== ''}>
            <Field.Label>Client to keep</Field.Label>
            <Select
              options={candidates.clients.map((other) => ({
                value: other.id,
                label: sortName(other),
              }))}
              value={choice}
              onValueChange={setChoice}
              placeholder="Choose a client"
            />
          </Field.Root>
        )}
        {error !== '' ? (
          <Text aria-live="assertive" style={[styles.body, danger]}>
            {error}
          </Text>
        ) : null}
        <View style={styles.actions}>
          <Button
            size="lg"
            intent="danger"
            disabled={busy || candidates.kind !== 'ready' || candidates.clients.length === 0}
            onPress={() => void merge()}
          >
            Merge
          </Button>
          <AlertDialog.Close>Cancel</AlertDialog.Close>
        </View>
      </AlertDialog.Popup>
    </AlertDialog.Root>
  );
}

/** The server's 409 message without the error-class prefix the api-client
 * puts in front of it. */
function conflictMessage(cause: ApiException): string {
  return cause.message.replace(/^ConflictError:\s*/, '') || 'Could not merge. Try again.';
}

const styles = StyleSheet.create({
  actions: { flexDirection: 'row', gap: spacing.sm, marginTop: spacing.md },
  body: { fontSize: fontSizes.body, lineHeight: fontSizes.body * 1.5 },
});
