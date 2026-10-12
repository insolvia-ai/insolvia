import { ApiValidationException, permits } from '@insolvia-ai/api-client';
import type { FirmMembership } from '@insolvia-ai/api-client';
import { Button } from '@insolvia-ai/design-system';
import { Link, useRouter } from 'expo-router';
import { useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useApi } from '@/api/use-api';
import { AppShell } from '@/components/app-shell';
import { Heading } from '@/components/heading';
import { fontSizes, spacing, useTheme } from '@/theme';

import { ClientForm, EMPTY_CLIENT_FORM, requestFromForm } from './client-form';

type Then = 'record' | 'case';

/**
 * `/clients/new` — "Add client", the front door (ADR 0022 / #354).
 *
 * A person comes into the firm before any matter does: a consultation, a
 * referral, a prospect who may never file. So the first step is the person,
 * and the case is an OPTIONAL second step — "Save and start a case" adds the
 * client and goes straight to `/cases/new?client=<id>` with them chosen as
 * Debtor 1; "Save client" adds them and opens their record.
 *
 * The client is saved BEFORE the case form opens, and on purpose: the two are
 * separate writes (a case can be refused for its court), and a client
 * half-entered into a case form that then failed would be retyped.
 *
 * Gated on `clients` at `add_edit`. A view-only colleague is told so here
 * rather than shown a form the API would refuse; opening the case afterwards
 * needs `cases` at `add_edit` too, and without it only "Save client" shows.
 */
export function AddClient({ membership }: { membership: FirmMembership }) {
  const theme = useTheme();
  const router = useRouter();
  const { call } = useApi();
  const [form, setForm] = useState(EMPTY_CLIENT_FORM);
  const [errors, setErrors] = useState<Readonly<Record<string, string>>>({});
  const [status, setStatus] = useState('');
  const [saving, setSaving] = useState(false);

  const mayAdd = permits(membership.permissions.clients, 'add_edit');
  const mayOpenCase = permits(membership.permissions.cases, 'add_edit');
  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };

  if (!mayAdd) {
    return (
      <AppShell>
        <Heading level={1}>Add a client</Heading>
        <Text style={[styles.body, muted]}>
          Your firm has not given you permission to add clients. Ask one of your firm’s
          administrators if you need it.
        </Text>
      </AppShell>
    );
  }

  const save = async (then: Then) => {
    setSaving(true);
    setErrors({});
    setStatus('Saving…');
    try {
      const result = await call((client) => client.createFirmClient(requestFromForm(form)));
      if (!result.ok) {
        setStatus('');
        return;
      }
      setStatus('');
      // `replace`, not `push`: Back from the next page should not land on a
      // blank form that would add the same person a second time.
      router.replace(
        then === 'case'
          ? `/cases/new?client=${encodeURIComponent(result.value.id)}`
          : `/clients/${result.value.id}`,
      );
    } catch (cause) {
      if (cause instanceof ApiValidationException) {
        setErrors(cause.fields);
        setStatus('Some answers need attention.');
      } else {
        setStatus('Could not save the client. Your entries are still here — try again.');
      }
    } finally {
      setSaving(false);
    }
  };

  return (
    <AppShell>
      <Heading level={1}>Add a client</Heading>
      <Text style={[styles.body, muted]}>
        The person first, then — if they are ready — their case. Everything but a name can be filled
        in later.
      </Text>

      <ClientForm value={form} onChange={setForm} errors={errors} headingLevel={2} />

      <View style={styles.actions}>
        {mayOpenCase ? (
          <>
            <Button size="lg" disabled={saving} onPress={() => void save('case')}>
              Save and start a case
            </Button>
            <Button
              size="lg"
              intent="secondary"
              disabled={saving}
              onPress={() => void save('record')}
            >
              Save client
            </Button>
          </>
        ) : (
          <Button size="lg" disabled={saving} onPress={() => void save('record')}>
            Save client
          </Button>
        )}
        <Link
          href="/clients"
          style={[
            styles.cancel,
            { color: theme.colors.primary, fontFamily: theme.typography.body },
          ]}
        >
          Cancel
        </Link>
      </View>
      <Text
        aria-live={status === 'Saving…' ? 'polite' : 'assertive'}
        style={[
          styles.status,
          status === 'Saving…'
            ? muted
            : { color: theme.colors.danger, fontFamily: theme.typography.body },
        ]}
      >
        {status}
      </Text>
    </AppShell>
  );
}

const styles = StyleSheet.create({
  actions: { alignItems: 'center', flexDirection: 'row', flexWrap: 'wrap', gap: spacing.md },
  body: { fontSize: fontSizes.body, lineHeight: fontSizes.body * 1.5 },
  cancel: { fontSize: fontSizes.label, fontWeight: '600', lineHeight: 44 },
  status: { fontSize: fontSizes.label },
});
