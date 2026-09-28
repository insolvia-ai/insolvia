import type {
  ClientRole,
  Debtor,
  InvitePortalClientRequest,
  PortalClient,
  PortalClientStatus,
} from '@insolvia-ai/api-client';
import { ApiException, ApiValidationException } from '@insolvia-ai/api-client';
import { AlertDialog, Badge, Button, Field, Input, RadioGroup } from '@insolvia-ai/design-system';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useApi } from '@/api/use-api';
import { personName } from '@/components/case-shell';
import { Heading } from '@/components/heading';
import { fontSizes, spacing, useTheme } from '@/theme';

/**
 * The firm side of the client portal, on the case overview (ADR 0023 PR 2):
 * who has been invited to this case's portal, and — for a holder of
 * `client_portal` at `add_edit` — inviting, re-sending and revoking.
 *
 * Rendered only for a caller who holds `client_portal` at all (the overview
 * decides, from the same `permits` courtesy every gated surface uses); the API
 * re-checks every call regardless. `canEdit` is the `add_edit` half.
 *
 * ## A joint case offers each debtor, or one login for both
 *
 * The ADR's joint-case rule, as a choice on the form: by DEFAULT a joint case
 * is two invitations, one per spouse — each states their own facts and the
 * review queue can say who said what — and the firm may instead invite one
 * person for both, which is recorded as that choice. So when the case has a
 * second debtor the form asks whom this login is for: Debtor 1, Debtor 2, or
 * both. A single-debtor case asks nothing; the API defaults to `debtor_1`.
 *
 * What it does NOT do is second-guess the server's one-holder-per-role rule.
 * Inviting Debtor 2 while one login holds both narrows the first; taking a
 * role from someone who would be left with none is a 409 whose message says
 * so, and the panel shows that message as the server wrote it (ADR 0001: the
 * server owns the rule, so it owns the sentence).
 */
export interface PortalPanelProps {
  readonly caseId: string;
  readonly debtors: readonly Debtor[];
  /** `client_portal` at `add_edit`: invite, re-send, revoke. */
  readonly canEdit: boolean;
}

type ListState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly clients: readonly PortalClient[] }
  | { readonly kind: 'error' };

type Notice = { readonly tone: 'ok' | 'error'; readonly message: string } | null;

/** The form's choice. `both` is the explicit one-login-for-two option. */
type Choice = 'debtor_1' | 'debtor_2' | 'both';

const ROLES_FOR: Record<Choice, readonly ClientRole[]> = {
  debtor_1: ['debtor_1'],
  debtor_2: ['debtor_2'],
  both: ['debtor_1', 'debtor_2'],
};

const STATUS_LABEL: Record<PortalClientStatus, string> = {
  invited: 'Invited',
  active: 'Active',
  revoked: 'Revoked',
};

const STATUS_INTENT = {
  invited: 'warning',
  active: 'success',
  revoked: 'neutral',
} as const satisfies Record<PortalClientStatus, 'warning' | 'success' | 'neutral'>;

export function PortalPanel({ caseId, debtors, canEdit }: PortalPanelProps) {
  const theme = useTheme();
  const { call } = useApi();
  const [list, setList] = useState<ListState>({ kind: 'loading' });
  const [notice, setNotice] = useState<Notice>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [revoking, setRevoking] = useState<PortalClient | null>(null);

  const debtorNames = namesByRole(debtors);
  const joint = debtors.some((debtor) => debtor.filing_role === 'debtor_2');

  const load = useCallback(async () => {
    try {
      const result = await call((client) => client.listPortalClients(caseId));
      if (result.ok) setList({ kind: 'ready', clients: result.value });
    } catch {
      setList({ kind: 'error' });
    }
  }, [call, caseId]);

  useEffect(() => {
    void load();
  }, [load]);

  const resend = async (client: PortalClient) => {
    setBusy(client.subject);
    setNotice(null);
    try {
      const result = await call((api) => api.resendPortalInvitation(caseId, client.subject));
      if (result.ok) {
        setNotice({ tone: 'ok', message: `Invitation re-sent to ${client.email}.` });
        await load();
      }
    } catch (cause) {
      setNotice({ tone: 'error', message: failureMessage(cause, 'Could not re-send it.') });
    } finally {
      setBusy(null);
    }
  };

  const confirmRevoke = async () => {
    const client = revoking;
    setRevoking(null);
    if (client === null) return;
    setBusy(client.subject);
    setNotice(null);
    try {
      const result = await call((api) => api.revokePortalClient(caseId, client.subject));
      if (result.ok) {
        setNotice({ tone: 'ok', message: `${client.displayName} no longer has portal access.` });
        await load();
      }
    } catch (cause) {
      setNotice({ tone: 'error', message: failureMessage(cause, 'Could not revoke access.') });
    } finally {
      setBusy(null);
    }
  };

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const ink = { color: theme.colors.ink, fontFamily: theme.typography.body };

  return (
    <View style={styles.panel}>
      {list.kind === 'loading' ? (
        <Text style={[styles.body, muted]}>Loading who has been invited…</Text>
      ) : list.kind === 'error' ? (
        <Text style={[styles.body, muted]}>
          Could not load this case&apos;s portal invitations. Reload the page to try again.
        </Text>
      ) : list.clients.length === 0 ? (
        <Text style={[styles.body, muted]}>
          Nobody has been invited yet. An invited client signs in to see their case&apos;s status;
          the questionnaire and document requests follow.
        </Text>
      ) : (
        <View style={styles.rows}>
          {list.clients.map((client) => (
            <View
              key={client.subject}
              style={[styles.row, { borderBottomColor: theme.colors.line }]}
            >
              <View style={styles.who}>
                <Text style={[styles.name, ink]}>{client.displayName}</Text>
                <Text style={[styles.meta, muted]} numberOfLines={1}>
                  {client.email} · {rolesLabel(client.roles, debtorNames)}
                </Text>
              </View>
              <Badge intent={STATUS_INTENT[client.status]}>{STATUS_LABEL[client.status]}</Badge>
              {canEdit && client.status !== 'revoked' ? (
                <View style={styles.rowActions}>
                  {client.status === 'invited' ? (
                    <Button
                      size="lg"
                      intent="secondary"
                      disabled={busy !== null}
                      aria-label={`Re-send invitation to ${client.displayName}`}
                      onPress={() => void resend(client)}
                    >
                      Re-send
                    </Button>
                  ) : null}
                  <Button
                    size="lg"
                    intent="ghost"
                    disabled={busy !== null}
                    aria-label={`Revoke portal access for ${client.displayName}`}
                    onPress={() => setRevoking(client)}
                  >
                    Revoke
                  </Button>
                </View>
              ) : null}
            </View>
          ))}
        </View>
      )}

      {notice === null ? null : (
        <Text
          aria-live={notice.tone === 'error' ? 'assertive' : 'polite'}
          style={[
            styles.body,
            notice.tone === 'error'
              ? { color: theme.colors.danger, fontFamily: theme.typography.body }
              : ink,
          ]}
        >
          {notice.message}
        </Text>
      )}

      {canEdit ? (
        <InviteForm
          caseId={caseId}
          joint={joint}
          debtorNames={debtorNames}
          debtors={debtors}
          onInvited={async (client) => {
            setNotice({
              tone: 'ok',
              message: `Invited ${client.displayName}. Insolvia has emailed them a temporary password and a link to the portal.`,
            });
            await load();
          }}
        />
      ) : null}

      {/* One dialog for the list, driven by whom is being confirmed — the
          documents screen's pattern. Revoking is not a single press. */}
      <AlertDialog.Root
        open={revoking !== null}
        onOpenChange={(next) => {
          if (!next) setRevoking(null);
        }}
      >
        <AlertDialog.Popup>
          <AlertDialog.Title>
            Revoke portal access for {revoking?.displayName ?? 'this client'}?
          </AlertDialog.Title>
          <AlertDialog.Description>
            They will no longer be able to open this case in the client portal — within the hour, at
            most. Anything they have already sent stays on the case. You can invite them again
            later.
          </AlertDialog.Description>
          <View style={styles.dialogActions}>
            <Button size="lg" onPress={() => void confirmRevoke()}>
              Revoke access
            </Button>
            <AlertDialog.Close>Keep access</AlertDialog.Close>
          </View>
        </AlertDialog.Popup>
      </AlertDialog.Root>
    </View>
  );
}

function InviteForm({
  caseId,
  joint,
  debtorNames,
  debtors,
  onInvited,
}: {
  caseId: string;
  joint: boolean;
  debtorNames: Partial<Record<ClientRole, string>>;
  debtors: readonly Debtor[];
  onInvited: (client: PortalClient) => Promise<void>;
}) {
  const theme = useTheme();
  const { call } = useApi();
  const [choice, setChoice] = useState<Choice>('debtor_1');
  const [displayName, setDisplayName] = useState('');
  const [email, setEmail] = useState('');
  // Whether the person has typed in a field since the last prefill. A typed
  // value is never overwritten by one arriving from the debtor record.
  const [edited, setEdited] = useState({ name: false, email: false });
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});
  const [failure, setFailure] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  /**
   * Prefill from the chosen debtor's record — a convenience, never a lock:
   * the firm may know a better address than the one intake captured, and the
   * name is what the review queue will show. An EFFECT rather than initial
   * state because `CaseShell` reads the debtors after the case itself, so on
   * the first render they are usually not there yet.
   */
  const prefillRole: ClientRole | null = choice === 'both' ? null : choice;
  const prefillName = prefillRole === null ? null : (debtorNames[prefillRole] ?? '');
  const prefillEmail = prefillRole === null ? null : debtorEmail(debtors, prefillRole);
  useEffect(() => {
    if (prefillName !== null && !edited.name) setDisplayName(prefillName);
    if (prefillEmail !== null && !edited.email) setEmail(prefillEmail);
  }, [edited.email, edited.name, prefillEmail, prefillName]);

  /** Choosing whom the login is for is a fresh prefill, over any typing. */
  const choose = (next: Choice) => {
    setChoice(next);
    if (next !== 'both') setEdited({ name: false, email: false });
  };

  const submit = async () => {
    setSubmitting(true);
    setFieldErrors({});
    setFailure(null);
    const request: InvitePortalClientRequest = {
      email: email.trim(),
      displayName: displayName.trim(),
      ...(joint ? { roles: ROLES_FOR[choice] } : {}),
    };
    try {
      const result = await call((client) => client.invitePortalClient(caseId, request));
      if (result.ok) {
        await onInvited(result.value);
      }
    } catch (cause) {
      if (cause instanceof ApiValidationException) {
        setFieldErrors(cause.fields);
      } else {
        setFailure(failureMessage(cause, 'Could not send the invitation. Please try again.'));
      }
    } finally {
      setSubmitting(false);
    }
  };

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const ink = { color: theme.colors.ink, fontFamily: theme.typography.body };

  return (
    <View style={styles.form}>
      <Heading level={3} size="body">
        Invite a client
      </Heading>

      {joint ? (
        <View style={styles.group}>
          <Text style={[styles.label, ink]}>This login is for</Text>
          <Text style={[styles.meta, muted]}>
            By default each spouse gets their own login, so each answers for themselves. Choose one
            login for both only if that is what the couple wants.
          </Text>
          <RadioGroup.Root
            aria-label="This login is for"
            value={choice}
            onValueChange={(next) => choose(next as Choice)}
            style={styles.radios}
          >
            {(
              [
                ['debtor_1', debtorNames.debtor_1 ?? 'Debtor 1'],
                ['debtor_2', debtorNames.debtor_2 ?? 'Debtor 2'],
                ['both', 'Both debtors, one login'],
              ] as const
            ).map(([value, label]) => (
              <View key={value} style={styles.radio}>
                <RadioGroup.Item value={value} aria-label={label} hitSlop={12}>
                  <RadioGroup.Indicator />
                </RadioGroup.Item>
                <Text style={[styles.body, ink]}>{label}</Text>
              </View>
            ))}
          </RadioGroup.Root>
        </View>
      ) : null}

      <Field.Root name="displayName" invalid={Boolean(fieldErrors.displayName)}>
        <Field.Label>Name</Field.Label>
        <Field.Description>What your firm will see beside their answers.</Field.Description>
        <Input
          value={displayName}
          onValueChange={(next) => {
            setDisplayName(next);
            setEdited((was) => ({ ...was, name: true }));
          }}
        />
        <Field.Error>{fieldErrors.displayName}</Field.Error>
      </Field.Root>

      <Field.Root name="email" invalid={Boolean(fieldErrors.email)}>
        <Field.Label>Email address</Field.Label>
        <Input
          type="email"
          value={email}
          onValueChange={(next) => {
            setEmail(next);
            setEdited((was) => ({ ...was, email: true }));
          }}
        />
        <Field.Error>{fieldErrors.email}</Field.Error>
      </Field.Root>

      {failure === null ? null : (
        <Text
          aria-live="assertive"
          style={[styles.body, { color: theme.colors.danger, fontFamily: theme.typography.body }]}
        >
          {failure}
        </Text>
      )}

      <Button size="lg" disabled={submitting} onPress={() => void submit()} style={styles.submit}>
        {submitting ? 'Sending…' : 'Send invitation'}
      </Button>
    </View>
  );
}

/** Each filing role's name from intake, where intake has one. */
function namesByRole(debtors: readonly Debtor[]): Partial<Record<ClientRole, string>> {
  const names: Partial<Record<ClientRole, string>> = {};
  for (const debtor of debtors) {
    if (debtor.filing_role !== 'debtor_1' && debtor.filing_role !== 'debtor_2') continue;
    const name = personName(debtor.name);
    if (name !== null) names[debtor.filing_role] = name;
  }
  return names;
}

function debtorEmail(debtors: readonly Debtor[], role: ClientRole): string {
  return debtors.find((debtor) => debtor.filing_role === role)?.email ?? '';
}

function rolesLabel(
  roles: readonly ClientRole[],
  names: Partial<Record<ClientRole, string>>,
): string {
  if (roles.length === 2) return 'both debtors';
  const role = roles[0] ?? 'debtor_1';
  const fallback = role === 'debtor_1' ? 'Debtor 1' : 'Debtor 2';
  return `for ${names[role] ?? fallback}`;
}

/**
 * The sentence for a failed call. A 409 carries the server's own explanation
 * (the role is held; the address belongs to someone else) and it is shown as
 * written; anything else gets the caller's generic line.
 */
function failureMessage(cause: unknown, fallback: string): string {
  if (cause instanceof ApiException && cause.statusCode === 409) {
    const message = serverMessage(cause.body);
    if (message !== null) return message;
  }
  return fallback;
}

function serverMessage(body: string): string | null {
  try {
    const parsed: unknown = JSON.parse(body);
    if (typeof parsed === 'object' && parsed !== null && 'message' in parsed) {
      const message = (parsed as { message: unknown }).message;
      if (typeof message === 'string' && message !== '') {
        const sentence = message.charAt(0).toUpperCase() + message.slice(1);
        return sentence.endsWith('.') ? sentence : `${sentence}.`;
      }
    }
  } catch {
    // Not the API's envelope — the caller's fallback says enough.
  }
  return null;
}

const styles = StyleSheet.create({
  body: {
    fontSize: fontSizes.body,
    lineHeight: fontSizes.body * 1.5,
  },
  dialogActions: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.sm,
    marginTop: spacing.md,
  },
  form: {
    gap: spacing.md,
    marginTop: spacing.md,
  },
  group: {
    gap: spacing.xs,
  },
  label: {
    fontSize: fontSizes.label,
    fontWeight: '600',
  },
  meta: {
    fontSize: fontSizes.label,
    lineHeight: fontSizes.label * 1.5,
  },
  name: {
    fontSize: fontSizes.body,
    fontWeight: '600',
  },
  panel: {
    gap: spacing.sm,
  },
  radio: {
    alignItems: 'center',
    flexDirection: 'row',
    gap: spacing.sm,
    minHeight: 44,
  },
  radios: {
    gap: 0,
  },
  row: {
    alignItems: 'center',
    borderBottomWidth: StyleSheet.hairlineWidth,
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.md,
    paddingVertical: spacing.sm,
  },
  rowActions: {
    flexDirection: 'row',
    gap: spacing.sm,
    marginLeft: 'auto',
  },
  rows: {
    gap: 0,
  },
  submit: {
    alignSelf: 'flex-start',
  },
  who: {
    flexBasis: 200,
    flexGrow: 1,
    flexShrink: 1,
    minWidth: 0,
  },
});
