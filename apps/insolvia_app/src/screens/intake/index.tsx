import { ApiValidationException, staffTypedProvenance } from '@insolvia-ai/api-client';
import type { Debtor, DebtorBody, FilingRole } from '@insolvia-ai/api-client';
import { useLocalSearchParams } from 'expo-router';
import { useCallback, useEffect, useRef, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useApi } from '@/api/use-api';
import { AppShell } from '@/components/app-shell';
import { EnvBadge } from '@/components/env-badge';
import { Heading } from '@/components/heading';
import { appEnvironment, environmentInfo } from '@/config/environment';
import { fontSizes, spacing, useTheme } from '@/theme';

import { DebtorFields } from './debtor-fields';

/**
 * `/cases/<id>/intake` — the structured intake (issue 8.5).
 *
 * THE ONE THING THIS MUST NEVER DO IS LOSE A HALF-FINISHED INTAKE, which is
 * what shapes everything below:
 *
 * - **Autosave, debounced**, rather than a Save button. A form this long
 *   collected behind one button is a form that loses an afternoon to a closed
 *   tab. The API accepts an entirely empty record for the same reason.
 * - **The WHOLE record goes on every save.** The endpoint is a PUT, because
 *   "every populated field carries provenance" can only be checked against a
 *   complete record (see the API's parse_debtor). So the client holds the whole
 *   thing and sends it; a field left out is cleared, which is also how removing
 *   an alias works.
 * - **Resume by loading first.** Every debtor of the case is fetched on mount,
 *   so returning to a half-done intake shows it rather than an empty form.
 *
 * A joint filing is two debtor RECORDS, not a second column, which is why the
 * role picker switches between whole records rather than revealing more fields
 * — see docs/reference/case-data-model.md.
 *
 * Deliberately only the debtor step. The other twenty-two entities in the
 * field map have no API yet; a step that cannot persist would be a worse lie
 * than an absent one.
 */

const ROLES: readonly { readonly value: FilingRole; readonly label: string }[] = [
  { value: 'debtor_1', label: 'Debtor 1' },
  { value: 'debtor_2', label: 'Debtor 2' },
  { value: 'non_filing_spouse', label: 'Non-filing spouse' },
];

/** Long enough that ordinary typing does not fire a request per word, short
 * enough that a closed tab loses a sentence rather than a session. */
const AUTOSAVE_DELAY_MS = 800;

type SaveState =
  | { readonly kind: 'idle' }
  | { readonly kind: 'saving' }
  | { readonly kind: 'saved' }
  | { readonly kind: 'error'; readonly message: string };

type LoadState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready' }
  | { readonly kind: 'error'; readonly message: string };

function bodyOf(debtor: Debtor): DebtorBody {
  const {
    id: _id,
    case_id: _caseId,
    filing_role: _role,
    created_at: _created,
    updated_at: _updated,
    provenance: _provenance,
    ...body
  } = debtor;
  return body;
}

export function Intake() {
  const theme = useTheme();
  const env = environmentInfo(appEnvironment);
  const { call } = useApi();
  const { caseId } = useLocalSearchParams<{ caseId: string }>();

  const [role, setRole] = useState<FilingRole>('debtor_1');
  const [bodies, setBodies] = useState<Partial<Record<FilingRole, DebtorBody>>>({});
  const [load, setLoad] = useState<LoadState>({ kind: 'loading' });
  const [save, setSave] = useState<SaveState>({ kind: 'idle' });
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});

  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const result = await call((client) => client.listDebtors(caseId));
        if (!result.ok || cancelled) return;
        setBodies(
          Object.fromEntries(result.value.map((debtor) => [debtor.filing_role, bodyOf(debtor)])),
        );
        setLoad({ kind: 'ready' });
      } catch {
        if (!cancelled) setLoad({ kind: 'error', message: 'Could not load this intake.' });
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [call, caseId]);

  const persist = useCallback(
    async (which: FilingRole, body: DebtorBody) => {
      setSave({ kind: 'saving' });
      try {
        // The provenance map is built from the body rather than tracked
        // alongside it: a person typed every value on this screen, so
        // "staff_typed on each populated field" is the whole truth, and
        // deriving it means it cannot drift out of step with the record.
        const result = await call((client) =>
          client.putDebtor(caseId, which, { ...body, provenance: staffTypedProvenance(body) }),
        );
        if (!result.ok) return;
        setFieldErrors({});
        setSave({ kind: 'saved' });
      } catch (cause) {
        if (cause instanceof ApiValidationException) {
          // The server is the source of truth for validation (ADR 0001), so
          // its per-field messages are rendered as-is against the same dotted
          // paths the fields write to.
          setFieldErrors(cause.fields);
          setSave({ kind: 'error', message: 'Some answers need attention.' });
        } else {
          setSave({ kind: 'error', message: 'Could not save. Retrying on your next change.' });
        }
      }
    },
    [call, caseId],
  );

  const change = (next: DebtorBody) => {
    setBodies((current) => ({ ...current, [role]: next }));
    if (timer.current !== null) clearTimeout(timer.current);
    timer.current = setTimeout(() => void persist(role, next), AUTOSAVE_DELAY_MS);
  };

  // Switching roles flushes first. Waiting out the debounce would mean the
  // pending edit lands under whichever role happened to be selected when the
  // timer fired — writing one debtor's name onto another's record.
  const switchRole = (next: FilingRole) => {
    if (timer.current !== null) {
      clearTimeout(timer.current);
      timer.current = null;
      const pending = bodies[role];
      if (pending !== undefined) void persist(role, pending);
    }
    setFieldErrors({});
    setSave({ kind: 'idle' });
    setRole(next);
  };

  useEffect(
    () => () => {
      if (timer.current !== null) clearTimeout(timer.current);
    },
    [],
  );

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };

  return (
    <AppShell actions={<EnvBadge env={env.name} />}>
      <Heading level={1}>Intake</Heading>

      {load.kind === 'loading' ? (
        <Text aria-live="polite" style={[styles.status, muted]}>
          Loading this intake…
        </Text>
      ) : load.kind === 'error' ? (
        <Text aria-live="assertive" style={[styles.status, { color: theme.colors.danger }]}>
          {load.message}
        </Text>
      ) : (
        <>
          {/* Whole records, not columns: a joint filing is two debtor records
              and a non-filing spouse may appear on 106I without filing. */}
          <View role="tablist" aria-label="Who this is about" style={styles.roles}>
            {ROLES.map((option) => (
              <Text
                key={option.value}
                role="tab"
                aria-selected={option.value === role}
                onPress={() => switchRole(option.value)}
                style={[
                  styles.role,
                  {
                    color: option.value === role ? theme.colors.ink : theme.colors.muted,
                    borderBottomColor: option.value === role ? theme.colors.primary : 'transparent',
                  },
                ]}
              >
                {option.label}
              </Text>
            ))}
          </View>

          <Heading level={2}>{ROLES.find((option) => option.value === role)?.label}</Heading>

          <Text aria-live="polite" style={[styles.status, muted]}>
            {save.kind === 'saving'
              ? 'Saving…'
              : save.kind === 'saved'
                ? 'Saved'
                : save.kind === 'error'
                  ? save.message
                  : 'Changes save automatically'}
          </Text>

          <DebtorFields
            body={bodies[role] ?? {}}
            onChange={change}
            errors={fieldErrors}
            disabled={false}
          />
        </>
      )}
    </AppShell>
  );
}

const styles = StyleSheet.create({
  role: {
    borderBottomWidth: 2,
    fontSize: fontSizes.label,
    fontWeight: '600',
    // 44dp, the WCAG 2.5.5 target the app enforces everywhere it can be pressed.
    lineHeight: 44,
    paddingHorizontal: spacing.sm,
  },
  roles: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.sm },
  status: { fontSize: fontSizes.label },
});
