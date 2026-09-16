import {
  ApiValidationException,
  FILING_PROFESSIONAL_ROLES,
  staffTypedProvenance,
} from '@insolvia-ai/api-client';
import type {
  Address,
  FilingProfessionalBody,
  FilingProfessionalRole,
  PersonName,
} from '@insolvia-ai/api-client';
import { Button, DateInput, Field, Input, Select } from '@insolvia-ai/design-system';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useApi } from '@/api/use-api';
import { Heading } from '@/components/heading';
import { labelize } from '@/screens/intake/collections';
import { fontSizes, spacing, useTheme } from '@/theme';

/**
 * B101 Part 7 — who signs (issue #342). 0-2 `filing_professional` records per
 * case: an attorney signs the petition, or a bankruptcy petition preparer
 * does (→ Form 119); the pro se path (neither) needs no record here at all.
 *
 * A ONE-RECORD-PER-ROLE FORM, not the generic `CollectionEditor` — the issue
 * asks for a dedicated signer-block form, and unlike the three repeating
 * lists this collection is not a plain list a paralegal adds rows to: there
 * are at most two meaningful rows, one per role, and the role picker below
 * IS the record picker. Saves are explicit, like the collection editor's own
 * (a half-typed attorney block autosaving would create a record from two
 * keystrokes), and like it the whole record is sent on every save — the
 * generic entity endpoints are PUT, not PATCH (invariant 1: every populated
 * field needs provenance, checked against a complete record).
 */

const ROLE_OPTIONS = FILING_PROFESSIONAL_ROLES.map((value) => ({ value, label: labelize(value) }));

type LoadState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready' }
  | { readonly kind: 'error'; readonly message: string };

interface StoredRecord {
  readonly id: string;
  readonly body: FilingProfessionalBody;
}

export function SignerSection({ caseId }: { caseId: string }) {
  const theme = useTheme();
  const { call } = useApi();

  const [load, setLoad] = useState<LoadState>({ kind: 'loading' });
  const [records, setRecords] = useState<Partial<Record<FilingProfessionalRole, StoredRecord>>>({});
  const [role, setRole] = useState<FilingProfessionalRole>('attorney');
  const [body, setBody] = useState<FilingProfessionalBody>({});
  const [status, setStatus] = useState('');
  const [errors, setErrors] = useState<Readonly<Record<string, string>>>({});
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const result = await call((client) =>
          client.listCaseEntities(caseId, 'filing_professionals'),
        );
        if (!result.ok || cancelled) return;
        const byRole: Partial<Record<FilingProfessionalRole, StoredRecord>> = {};
        for (const entity of result.value) {
          // Two rows can share a role only if something wrote them outside
          // this screen; the first one found is what this form edits and
          // saves over — the same "first block only" trade the B101
          // projection itself makes for sole proprietorships.
          if (entity.role !== undefined && byRole[entity.role] === undefined) {
            const {
              id: _id,
              case_id: _caseId,
              created_at: _created,
              updated_at: _updated,
              provenance: _provenance,
              ...rest
            } = entity;
            byRole[entity.role] = { id: entity.id, body: rest };
          }
        }
        setRecords(byRole);
        setBody(byRole.attorney?.body ?? {});
        setLoad({ kind: 'ready' });
      } catch {
        if (!cancelled) setLoad({ kind: 'error', message: 'Could not load the signer block.' });
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [call, caseId]);

  const switchRole = (next: FilingProfessionalRole) => {
    setRole(next);
    setBody(records[next]?.body ?? {});
    setErrors({});
    setStatus('');
  };

  const persist = useCallback(async () => {
    setSaving(true);
    setStatus('Saving…');
    try {
      const toSave: FilingProfessionalBody = { ...body, role };
      const existingId = records[role]?.id ?? null;
      // See the same cast in `screens/petition/index.tsx` — an interface
      // body needs it, a `Record<string, unknown>` does not.
      const request = {
        ...toSave,
        provenance: staffTypedProvenance(toSave as unknown as Record<string, unknown>),
      };
      const result = await call((client) =>
        existingId === null
          ? client.addCaseEntity(caseId, 'filing_professionals', request)
          : client.putCaseEntity(caseId, 'filing_professionals', existingId, request),
      );
      if (!result.ok) {
        setStatus('');
        return;
      }
      const {
        id: _id,
        case_id: _caseId,
        created_at: _created,
        updated_at: _updated,
        provenance: _provenance,
        ...rest
      } = result.value;
      setRecords((current) => ({ ...current, [role]: { id: result.value.id, body: rest } }));
      setErrors({});
      setStatus('Saved');
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
  }, [body, call, caseId, records, role]);

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const setName = (part: keyof PersonName, value: string) =>
    setBody((current) => ({ ...current, name: { ...current.name, [part]: value } }));
  const setAddress = (part: keyof Address, value: string) =>
    setBody((current) => ({ ...current, address: { ...current.address, [part]: value } }));

  return (
    <View style={styles.section}>
      <Heading level={2}>Who signs</Heading>
      <Text style={[styles.help, muted]}>
        An attorney signs the petition, or a bankruptcy petition preparer does (Form 119). Choose
        which block to fill in — a pro se filing needs neither.
      </Text>
      <Text
        aria-live={load.kind === 'error' ? 'assertive' : 'polite'}
        style={[
          styles.status,
          load.kind === 'error'
            ? { color: theme.colors.danger, fontFamily: theme.typography.body }
            : muted,
        ]}
      >
        {load.kind === 'loading'
          ? 'Loading the signer block…'
          : load.kind === 'error'
            ? load.message
            : status}
      </Text>

      {load.kind !== 'ready' ? null : (
        <View style={styles.form}>
          <Field.Root>
            <Field.Label>Role</Field.Label>
            <Select
              options={[...ROLE_OPTIONS]}
              value={role}
              onValueChange={(next) => switchRole(next as FilingProfessionalRole)}
            />
          </Field.Root>

          <TextField
            label="First name"
            path="name.given"
            value={body.name?.given}
            onChangeText={(v) => setName('given', v)}
            errors={errors}
          />
          <TextField
            label="Middle name"
            path="name.middle"
            value={body.name?.middle}
            onChangeText={(v) => setName('middle', v)}
            errors={errors}
          />
          <TextField
            label="Last name"
            path="name.surname"
            value={body.name?.surname}
            onChangeText={(v) => setName('surname', v)}
            errors={errors}
          />
          <TextField
            label="Suffix"
            path="name.suffix"
            value={body.name?.suffix}
            onChangeText={(v) => setName('suffix', v)}
            errors={errors}
          />
          <TextField
            label="Firm name"
            path="firm_name"
            value={body.firm_name}
            onChangeText={(v) =>
              setBody((current) => ({ ...current, firm_name: v === '' ? undefined : v }))
            }
            errors={errors}
          />
          {(['line1', 'line2', 'city', 'state', 'postal_code'] as const).map((part) => (
            <TextField
              key={part}
              label={`Address — ${addressLabel(part)}`}
              path={`address.${part}`}
              value={body.address?.[part]}
              onChangeText={(v) => setAddress(part, v)}
              errors={errors}
            />
          ))}
          <TextField
            label="Phone"
            path="phone"
            value={body.phone}
            onChangeText={(v) =>
              setBody((current) => ({ ...current, phone: v === '' ? undefined : v }))
            }
            errors={errors}
          />
          <TextField
            label="Email"
            path="email"
            value={body.email}
            onChangeText={(v) =>
              setBody((current) => ({ ...current, email: v === '' ? undefined : v }))
            }
            errors={errors}
          />
          <TextField
            label="Bar number"
            path="bar_number"
            value={body.bar_number}
            onChangeText={(v) =>
              setBody((current) => ({ ...current, bar_number: v === '' ? undefined : v }))
            }
            errors={errors}
            disabled={role !== 'attorney'}
            disabledReason="Only applies to an attorney."
          />
          <TextField
            label="Bar state"
            path="bar_state"
            value={body.bar_state}
            onChangeText={(v) =>
              setBody((current) => ({ ...current, bar_state: v === '' ? undefined : v }))
            }
            errors={errors}
            disabled={role !== 'attorney'}
            disabledReason="Only applies to an attorney."
          />
          <Field.Root invalid={Boolean(errors.signature_date)}>
            <Field.Label>Date signed</Field.Label>
            <DateInput
              value={body.signature_date ?? ''}
              onValueChange={(next, dateStatus) => {
                if (dateStatus === 'incomplete') return;
                setBody((current) => ({
                  ...current,
                  signature_date: next === '' ? undefined : next,
                }));
              }}
            />
            {errors.signature_date ? (
              <Field.Error match>{errors.signature_date}</Field.Error>
            ) : null}
          </Field.Root>

          <Button size="lg" disabled={saving} onPress={() => void persist()}>
            {records[role] === undefined ? 'Save signer' : 'Save changes'}
          </Button>
        </View>
      )}
    </View>
  );
}

function addressLabel(part: 'line1' | 'line2' | 'city' | 'state' | 'postal_code'): string {
  switch (part) {
    case 'line1':
      return 'Street';
    case 'line2':
      return 'Apartment, suite or unit';
    case 'city':
      return 'City';
    case 'state':
      return 'State';
    case 'postal_code':
      return 'ZIP code';
  }
}

function TextField({
  label,
  path,
  value,
  onChangeText,
  errors,
  disabled = false,
  disabledReason,
}: {
  label: string;
  path: string;
  value: string | undefined;
  onChangeText: (value: string) => void;
  errors: Readonly<Record<string, string>>;
  disabled?: boolean;
  disabledReason?: string;
}) {
  const message = errors[path];
  return (
    <Field.Root invalid={Boolean(message)}>
      <Field.Label>{label}</Field.Label>
      <Input
        value={value ?? ''}
        onValueChange={onChangeText}
        autoCorrect={false}
        disabled={disabled}
      />
      {disabled && disabledReason ? <Field.Description>{disabledReason}</Field.Description> : null}
      {message ? <Field.Error match>{message}</Field.Error> : null}
    </Field.Root>
  );
}

const styles = StyleSheet.create({
  form: { gap: spacing.md },
  help: { fontSize: fontSizes.label },
  section: { gap: spacing.md },
  status: { fontSize: fontSizes.label },
});
