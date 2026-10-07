import {
  ApiException,
  ApiReauthenticationRequiredException,
  permits,
} from '@insolvia-ai/api-client';
import type {
  ChecklistItemStatus,
  FilingApproval,
  FilingApprovalDocument,
  FilingApprovalStatus,
  FilingApprovalView,
} from '@insolvia-ai/api-client';
import { Badge, Button, Checkbox } from '@insolvia-ai/design-system';
import type { BadgeIntent } from '@insolvia-ai/design-system';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useMembership } from '@/api/me';
import { useApi } from '@/api/use-api';
import { Heading } from '@/components/heading';
import { useSession } from '@/session';
import { fontSizes, spacing, useTheme } from '@/theme';

type LoadState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly view: FilingApprovalView }
  | { readonly kind: 'error' };

type Notice = { readonly tone: 'saved' | 'error'; readonly message: string };

/**
 * How recent a sign-in this screen treats as fresh enough to offer Approve
 * directly. The API's window is `basis.signInMaxAgeSeconds` (five minutes)
 * and the API is the only judge; this offers a minute less, so a person who
 * reads for a while is sent to sign in again rather than refused after
 * pressing Approve — the filing authorization screen's rule.
 */
const FRESH_MARGIN_SECONDS = 60;

const STATUS_INTENT: Record<FilingApprovalStatus, BadgeIntent> = {
  pending: 'primary',
  consumed: 'success',
  voided: 'danger',
  expired: 'warning',
};

const STATUS_LABEL: Record<FilingApprovalStatus, string> = {
  pending: 'Approved — waiting to be filed',
  consumed: 'Being filed',
  voided: 'Voided',
  expired: 'Expired',
};

const VOID_REASON: Record<string, string> = {
  changed:
    'Something in the filing set changed after it was approved — a document, the case data, the court or the order. Review it again and approve what is there now.',
  superseded: 'A newer approval replaced it.',
  cancelled: 'It was cancelled before it was used.',
  enqueue_failed: 'It could not be handed to the filing service. Approve again.',
};

const CHECKLIST_INTENT: Record<ChecklistItemStatus, BadgeIntent> = {
  ready: 'success',
  missing: 'danger',
  action: 'primary',
  confirm: 'warning',
};

const CHECKLIST_LABEL: Record<ChecklistItemStatus, string> = {
  ready: 'Ready',
  missing: 'Missing',
  action: 'To do',
  confirm: 'Confirm',
};

function size(bytes: number): string {
  if (bytes < 1024) return `${bytes} bytes`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

function documentFacts(document: FilingApprovalDocument): string {
  if (document.file === undefined) {
    return 'Prepared outside Insolvia — not part of what Insolvia uploads.';
  }
  const pages =
    document.file.pageCount === undefined
      ? ''
      : `, ${document.file.pageCount} page${document.file.pageCount === 1 ? '' : 's'}`;
  return `${size(document.file.byteSize)}${pages}`;
}

function approvalSummary(approval: FilingApproval): string {
  switch (approval.status) {
    case 'pending':
      return `Approved ${approval.approvedAt.slice(0, 16).replace('T', ' ')} UTC. It is used once, and expires ${approval.expiresAt.slice(0, 16).replace('T', ' ')} UTC if it has not been.`;
    case 'consumed':
      return `The filing service took this approval ${approval.consumedAt?.slice(0, 16).replace('T', ' ') ?? ''} UTC. It cannot be used again.`;
    case 'expired':
      return 'It was not used before it expired. Approve again to file.';
    case 'voided':
      return VOID_REASON[approval.voidReason ?? ''] ?? 'It no longer applies.';
  }
}

/**
 * The attorney's per-filing approval (ADR 0024, guardrail 1), under the
 * filing set it approves.
 *
 * WHAT IS SHOWN IS WHAT IS APPROVED. Everything below comes from one
 * response — the court, every document in docket order with its size and
 * SHA-256, the checklist's state, the fee handling — and approving posts
 * back that response's `digest`. The API recomputes it and refuses a
 * mismatch, so nothing can be approved that this screen did not show.
 *
 * APPROVING NEEDS A FRESH SIGN-IN. Unless the person signed in within the
 * last few minutes, the button is "Sign in again to approve", which sends
 * them to the hosted page with `prompt=login` and back here.
 *
 * Only for a member holding `electronic_filing`; only an attorney with their
 * own court login for this court can approve — the API decides that, and the
 * screen says what it answered.
 */
export function FilingApprovalPanel({
  caseId,
  reloadKey,
}: {
  readonly caseId: string;
  readonly reloadKey: number;
}) {
  const membership = useMembership();
  const level = membership?.permissions.electronic_filing;
  if (level === undefined || !permits(level, 'view_only')) return null;
  return (
    <ApprovalBody caseId={caseId} reloadKey={reloadKey} canApprove={permits(level, 'add_edit')} />
  );
}

function ApprovalBody({
  caseId,
  reloadKey,
  canApprove,
}: {
  readonly caseId: string;
  readonly reloadKey: number;
  readonly canApprove: boolean;
}) {
  const theme = useTheme();
  const { call } = useApi();
  const { user, signIn } = useSession();
  const [state, setState] = useState<LoadState>({ kind: 'loading' });
  const [agreed, setAgreed] = useState(false);
  const [busy, setBusy] = useState(false);
  const [needsSignIn, setNeedsSignIn] = useState(false);
  const [notice, setNotice] = useState<Notice | null>(null);

  const load = useCallback(async () => {
    try {
      const result = await call((client) => client.getFilingApproval(caseId));
      if (result.ok) setState({ kind: 'ready', view: result.value });
    } catch {
      setState({ kind: 'error' });
    }
  }, [call, caseId]);

  useEffect(() => {
    void load();
  }, [load, reloadKey]);

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const ink = { color: theme.colors.ink, fontFamily: theme.typography.body };
  const mono = { color: theme.colors.muted, fontFamily: theme.typography.mono };

  if (state.kind !== 'ready') {
    return (
      <View style={styles.section}>
        <Heading level={2}>Approve this filing</Heading>
        <Text style={[styles.body, muted]}>
          {state.kind === 'loading'
            ? 'Loading what would be approved…'
            : 'Could not load the approval. Reload the page to try again.'}
        </Text>
      </View>
    );
  }

  const { basis, approval } = state.view;
  const authenticatedAt = user?.authenticatedAt ?? null;
  const freshEnough =
    !needsSignIn &&
    authenticatedAt !== null &&
    Date.now() / 1000 - authenticatedAt < basis.signInMaxAgeSeconds - FRESH_MARGIN_SECONDS;
  const pending = approval?.status === 'pending';
  const blockerTitles = basis.blockers.map(
    (id) =>
      basis.checklist.find((item) => item.id === id)?.title ??
      (id === 'filed'
        ? 'The case is already filed'
        : id === 'file_digests'
          ? 'The packet was assembled before files were fingerprinted — assemble it again'
          : id),
  );

  const approve = async () => {
    setBusy(true);
    setNotice(null);
    try {
      const result = await call((client) => client.approveFiling(caseId, { digest: basis.digest }));
      if (result.ok) {
        setState({ kind: 'ready', view: result.value });
        setAgreed(false);
        setNotice({
          tone: 'saved',
          message: 'Approved. The filing is queued; this approval is used once.',
        });
      }
    } catch (cause) {
      if (cause instanceof ApiReauthenticationRequiredException) {
        setNeedsSignIn(true);
        setNotice({
          tone: 'error',
          message:
            'Your sign-in is no longer recent enough to approve. Sign in again, then approve.',
        });
      } else if (
        cause instanceof ApiException &&
        (cause.statusCode === 409 || cause.statusCode === 403)
      ) {
        setNotice({ tone: 'error', message: cause.message });
        void load();
      } else {
        setNotice({ tone: 'error', message: 'Could not record the approval. Please try again.' });
      }
    } finally {
      setBusy(false);
    }
  };

  const cancel = async () => {
    setBusy(true);
    setNotice(null);
    try {
      const result = await call((client) => client.cancelFilingApproval(caseId));
      if (result.ok) {
        setState({ kind: 'ready', view: result.value });
        setNotice({ tone: 'saved', message: 'Approval cancelled. Nothing will be filed.' });
      }
    } catch {
      setNotice({ tone: 'error', message: 'Could not cancel. Reload the page to see its state.' });
      void load();
    } finally {
      setBusy(false);
    }
  };

  return (
    <View style={styles.section}>
      <Heading level={2}>Approve this filing</Heading>
      <Text style={[styles.body, muted]}>
        Approving lets Insolvia file exactly the set below under your own court login, once. Any
        change to it afterwards — a document re-assembled, the case data edited, the court changed —
        voids the approval.
      </Text>

      {approval !== undefined ? (
        <View style={styles.status}>
          <View style={styles.badges}>
            <Badge intent={STATUS_INTENT[approval.status]} size="sm">
              {STATUS_LABEL[approval.status]}
            </Badge>
          </View>
          <Text style={[styles.body, ink]}>{approvalSummary(approval)}</Text>
          {pending ? (
            <View style={styles.actions}>
              <Button size="lg" intent="danger" disabled={busy} onPress={() => void cancel()}>
                Cancel approval
              </Button>
            </View>
          ) : null}
        </View>
      ) : null}

      <Heading level={3}>Court</Heading>
      <Text style={[styles.body, ink]}>
        {basis.court === undefined
          ? 'No court is set on the case.'
          : `${basis.court.name}${basis.court.divisionName !== undefined ? `, ${basis.court.divisionName}` : ''}`}
      </Text>
      <Text style={[styles.caption, muted]}>{`Court rules: ${basis.registryRelease}`}</Text>

      <Heading level={3}>Documents, in docket order</Heading>
      <View role="list" style={styles.list}>
        {basis.documents.map((document) => (
          <View role="listitem" key={document.key} style={styles.row}>
            <Text style={[styles.rowTitle, ink]}>
              {`${document.position}. ${document.fileName}`}
            </Text>
            <Text style={[styles.body, muted]}>{document.title}</Text>
            <Text style={[styles.caption, muted]}>{documentFacts(document)}</Text>
            {document.file?.sha256 !== undefined ? (
              <Text style={[styles.caption, mono]} selectable>
                {`SHA-256 ${document.file.sha256}`}
              </Text>
            ) : null}
          </View>
        ))}
      </View>

      <Heading level={3}>Checklist</Heading>
      <View role="list" style={styles.list}>
        {basis.checklist.map((item) => (
          <View role="listitem" key={item.id} style={styles.badges}>
            <Badge intent={CHECKLIST_INTENT[item.status]} size="sm">
              {CHECKLIST_LABEL[item.status]}
            </Badge>
            <Text style={[styles.body, ink]}>{item.title}</Text>
          </View>
        ))}
      </View>

      <Heading level={3}>Filing fee</Heading>
      <Text style={[styles.body, ink]}>{basis.fee.detail}</Text>
      {basis.fee.deadline !== undefined ? (
        <Text style={[styles.body, muted]}>{`The court’s rule: ${basis.fee.deadline}`}</Text>
      ) : null}

      <Text style={[styles.caption, mono]} selectable>
        {`Approval fingerprint ${basis.digest}`}
      </Text>

      {!basis.ready ? (
        <Text style={[styles.body, ink, { color: theme.colors.danger }]}>
          {`Not ready to approve: ${blockerTitles.join('; ')}.`}
        </Text>
      ) : canApprove && !pending && approval?.status !== 'consumed' ? (
        freshEnough ? (
          <>
            <View style={styles.agree}>
              <Checkbox.Root
                aria-label="I have reviewed this filing set and approve filing it under my court login"
                checked={agreed}
                onCheckedChange={setAgreed}
              >
                <Checkbox.Indicator>✓</Checkbox.Indicator>
              </Checkbox.Root>
              <Text style={[styles.body, ink, styles.agreeText]}>
                I have reviewed this filing set and approve filing it under my court login.
              </Text>
            </View>
            <View style={styles.actions}>
              <Button size="lg" disabled={busy || !agreed} onPress={() => void approve()}>
                {busy ? 'Approving…' : 'Approve filing'}
              </Button>
            </View>
          </>
        ) : (
          <>
            <Text style={[styles.body, muted]}>
              To approve, sign in again first: Insolvia asks for your password at the moment you
              approve, not only when your session began.
            </Text>
            <View style={styles.actions}>
              <Button
                size="lg"
                disabled={busy}
                onPress={() => void signIn(`/cases/${caseId}/packet`, { reauthenticate: true })}
              >
                Sign in again to approve
              </Button>
            </View>
          </>
        )
      ) : null}

      {notice === null ? null : (
        <Text
          aria-live="polite"
          style={[
            styles.body,
            {
              color: notice.tone === 'error' ? theme.colors.danger : theme.colors.muted,
              fontFamily: theme.typography.body,
            },
          ]}
        >
          {notice.message}
        </Text>
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  actions: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.sm,
  },
  agree: {
    alignItems: 'center',
    flexDirection: 'row',
    gap: spacing.sm,
  },
  agreeText: {
    flex: 1,
  },
  badges: {
    alignItems: 'center',
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.sm,
  },
  body: {
    fontSize: fontSizes.body,
    lineHeight: fontSizes.body * 1.5,
  },
  caption: {
    fontSize: fontSizes.caption,
    lineHeight: fontSizes.caption * 1.5,
  },
  list: {
    gap: spacing.sm,
    marginTop: spacing.xs,
  },
  row: {
    gap: spacing.xs / 2,
  },
  rowTitle: {
    fontSize: fontSizes.body,
    fontWeight: '600',
  },
  section: {
    gap: spacing.md,
    marginTop: spacing.lg,
  },
  status: {
    gap: spacing.xs,
  },
});
