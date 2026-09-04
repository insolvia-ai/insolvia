import type { Job, Packet } from '@insolvia-ai/api-client';
import { Button } from '@insolvia-ai/design-system';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useApi } from '@/api/use-api';
import { AppShell } from '@/components/app-shell';
import { Heading } from '@/components/heading';
import { openDownload } from '@/screens/documents/browser';
import { fontSizes, spacing, useTheme } from '@/theme';

/**
 * How often a running assembly is polled. Assembly takes seconds to a couple
 * of minutes; two seconds keeps the wait honest without hammering the API.
 */
const POLL_INTERVAL_MS = 2000;

/**
 * One reason the gate refused, as the worker's `blocked` result lists them —
 * `problem_json` in services/api core/packet_assembly.py: `source` and
 * `message` always, `itemId`/`field` only when one record owns the fix.
 */
interface PacketProblem {
  readonly source: string;
  readonly message: string;
  readonly itemId?: string;
  readonly field?: string;
}

/**
 * Where the trigger currently stands. `blocked` is a SETTLED, successful
 * outcome — the job ran and its answer is the fix list — which is why it is a
 * state of its own rather than an error message.
 */
type Assembly =
  | { readonly phase: 'idle' }
  | { readonly phase: 'running'; readonly jobId: string }
  | { readonly phase: 'blocked'; readonly problems: readonly PacketProblem[] }
  | { readonly phase: 'failed'; readonly message: string };

type ListState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly packets: readonly Packet[] }
  | { readonly kind: 'error'; readonly message: string };

/**
 * The worker's result, narrowed field by field rather than cast: `Job.result`
 * is `Record<string, unknown>` on purpose (its shape is per-kind), and a
 * malformed one should degrade to "assembled, reload the list" — the list is
 * the durable truth — never to a crash.
 */
function decodeOutcome(
  result: Readonly<Record<string, unknown>> | undefined,
):
  | { readonly outcome: 'assembled' }
  | { readonly outcome: 'blocked'; readonly problems: readonly PacketProblem[] } {
  if (result !== undefined && result.outcome === 'blocked' && Array.isArray(result.problems)) {
    const problems: PacketProblem[] = [];
    for (const raw of result.problems) {
      if (typeof raw !== 'object' || raw === null) {
        continue;
      }
      const entry = raw as Record<string, unknown>;
      if (typeof entry.source === 'string' && typeof entry.message === 'string') {
        problems.push({
          source: entry.source,
          message: entry.message,
          ...(typeof entry.itemId === 'string' ? { itemId: entry.itemId } : {}),
          ...(typeof entry.field === 'string' ? { field: entry.field } : {}),
        });
      }
    }
    return { outcome: 'blocked', problems };
  }
  return { outcome: 'assembled' };
}

/**
 * The gate's `source` in a person's words. Collection names come from the
 * generic entity framework; `form/<x>` names a projection refusal on that
 * official form.
 */
function describeSource(source: string): string {
  if (source.startsWith('form/')) {
    return `Form ${source.slice('form/'.length).toUpperCase()}`;
  }
  const labels: Record<string, string> = {
    case: 'Case',
    debtors: 'Debtors',
    petitions: 'Petition',
    creditors: 'Creditors',
    claims: 'Claims',
    exemptions: 'Exemptions',
    income_summaries: 'Income',
    employments: 'Employment',
    households: 'Households',
    expenses: 'Expenses',
    dependents: 'Dependents',
    codebtors: 'Codebtors',
  };
  return labels[source] ?? source.replace(/_/g, ' ');
}

function formatSize(bytes: number): string {
  if (bytes >= 1024 * 1024) {
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  }
  return `${Math.max(1, Math.round(bytes / 1024))} KB`;
}

/**
 * `/cases/<id>/packet` — assemble and download the Chapter 7 filing packet
 * (issue #96).
 *
 * The shape follows the pipeline it fronts (ADR 0018): "Assemble" accepts a
 * `packet_assembly` job and this screen POLLS the job's status — the client
 * never sees the queue, only the API's record of it. A settled job lands in
 * one of three places, and only one of them is an error:
 *
 * - **assembled** — the packet is stored server-side; the list below is
 *   reloaded and the download button mints a short-lived URL on press (the
 *   same never-on-render rule the documents screen states).
 * - **blocked** — the completeness gate refused, and the problem list IS the
 *   deliverable: every reason, per record, so the preparer fixes the case in
 *   one pass. Rendered as a list, not collapsed into a toast.
 * - **failed** — the pipeline itself broke; the preparer-safe message from
 *   the job record is shown verbatim and the button re-enables.
 *
 * Old packets stay listed after re-assembly — the server keeps every one, so
 * the packet an attorney reviewed last week is still openable.
 */
export function FilingPacket({ caseId }: { readonly caseId: string }) {
  const theme = useTheme();
  const { call } = useApi();

  const [list, setList] = useState<ListState>({ kind: 'loading' });
  const [assembly, setAssembly] = useState<Assembly>({ phase: 'idle' });
  const [busyId, setBusyId] = useState<string | null>(null);
  const [activity, setActivity] = useState('');
  const [actionError, setActionError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      const result = await call((client) => client.listCasePackets(caseId));
      if (result.ok) {
        setList({ kind: 'ready', packets: result.value });
      }
    } catch {
      setList({ kind: 'error', message: 'Could not load this case’s packets.' });
    }
  }, [call, caseId]);

  useEffect(() => {
    void load();
  }, [load]);

  const settle = useCallback(
    (job: Job) => {
      if (job.status === 'succeeded') {
        const outcome = decodeOutcome(job.result);
        if (outcome.outcome === 'blocked') {
          setAssembly({ phase: 'blocked', problems: outcome.problems });
          setActivity('');
          return;
        }
        setAssembly({ phase: 'idle' });
        setActivity('Packet assembled. It is ready to download below.');
        void load();
        return;
      }
      // failed: the record's failure block carries the preparer-safe words.
      setAssembly({
        phase: 'failed',
        message: job.failure?.message ?? 'Assembly did not finish. Try again in a moment.',
      });
      setActivity('');
    },
    [load],
  );

  /**
   * The poll loop, alive exactly while a job is running. First check fires
   * immediately — a small case assembles in seconds and should read that way
   * — then every {@link POLL_INTERVAL_MS}. Cancelled on unmount and whenever
   * the phase leaves `running`, so a settled job cannot set state twice.
   */
  useEffect(() => {
    if (assembly.phase !== 'running') {
      return;
    }
    const jobId = assembly.jobId;
    let cancelled = false;
    const check = async () => {
      try {
        const result = await call((client) => client.getCaseJob(caseId, jobId));
        if (cancelled || !result.ok) {
          return;
        }
        const job = result.value;
        if (job.status === 'succeeded' || job.status === 'failed') {
          settle(job);
        }
      } catch {
        // A dropped poll is not a failed job; the next tick asks again.
      }
    };
    void check();
    const timer = setInterval(() => {
      void check();
    }, POLL_INTERVAL_MS);
    return () => {
      cancelled = true;
      clearInterval(timer);
    };
  }, [assembly, call, caseId, settle]);

  const assemble = async () => {
    if (assembly.phase === 'running') {
      return;
    }
    setActionError(null);
    setActivity('Assembling the filing packet…');
    try {
      const result = await call((client) => client.acceptCaseJob(caseId, 'packet_assembly'));
      if (result.ok) {
        // 202 either way: a fresh job, or the one already in flight — the
        // API's one-active-job rule makes re-pressing the button safe.
        setAssembly({ phase: 'running', jobId: result.value.id });
      }
    } catch {
      setActivity('');
      setActionError('Could not start assembly. Please try again.');
      setAssembly({ phase: 'idle' });
    }
  };

  const download = async (entry: Packet) => {
    setActionError(null);
    setBusyId(entry.id);
    try {
      // Minted on press, never on render — the URL is a short-lived bearer
      // capability, exactly as the document download's is.
      const result = await call((client) => client.getPacketUrl(caseId, entry.id));
      if (result.ok) {
        if (openDownload(result.value.url, entry.fileName)) {
          setActivity(`Opened ${entry.fileName}.`);
        } else {
          setActionError(`Could not open ${entry.fileName} — this needs a web browser.`);
        }
      }
    } catch {
      setActionError(`Could not prepare ${entry.fileName} for download. Please try again.`);
    } finally {
      setBusyId((current) => (current === entry.id ? null : current));
    }
  };

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const danger = { color: theme.colors.danger, fontFamily: theme.typography.body };
  const ink = { color: theme.colors.ink, fontFamily: theme.typography.body };

  const statusText =
    activity !== '' ? activity : list.kind === 'loading' ? 'Loading this case’s packets…' : '';

  // One assertive region, whatever went wrong most recently wins: an action
  // error, a failed job's own words, or the list failing to load.
  const assertiveText =
    actionError ??
    (assembly.phase === 'failed' ? assembly.message : list.kind === 'error' ? list.message : '');

  return (
    <AppShell>
      <Heading level={1}>Filing packet</Heading>
      <Text style={[styles.body, muted]}>
        Assembles the full individual Chapter 7 set — the petition, every schedule, the
        declarations, the statement of financial affairs and the creditor matrix — into one download
        of filed-ready PDFs. Assembly first checks the case is complete, and refuses with a list of
        what to fix rather than producing a partial packet.
      </Text>

      {/* One always-present live region per urgency — the documents screen's
          rule, for the documents screen's reason. */}
      <Text aria-live="polite" style={[styles.status, muted]}>
        {statusText}
      </Text>
      <Text aria-live="assertive" style={[styles.status, danger]}>
        {assertiveText}
      </Text>

      <View style={styles.actions}>
        <Button
          size="lg"
          onPress={assemble}
          disabled={assembly.phase === 'running'}
          aria-label="Assemble the Chapter 7 filing packet for this case"
        >
          {assembly.phase === 'running' ? 'Assembling…' : 'Assemble packet'}
        </Button>
      </View>

      {assembly.phase === 'blocked' ? (
        <View>
          <Heading level={2}>The case is not ready to file</Heading>
          <Text style={[styles.body, muted]}>
            Nothing was produced — a packet with a schedule missing is worse than none. Fix the
            items below, then assemble again.
          </Text>
          <View role="list" style={styles.list}>
            {assembly.problems.map((problem, index) => (
              <View role="listitem" key={`${problem.source}-${index}`} style={styles.problem}>
                <Text style={[styles.problemSource, ink]}>{describeSource(problem.source)}</Text>
                <Text style={[styles.body, muted]}>{problem.message}</Text>
              </View>
            ))}
          </View>
        </View>
      ) : null}

      <Heading level={2}>Assembled packets</Heading>
      {list.kind === 'ready' ? (
        list.packets.length === 0 ? (
          <Text style={[styles.body, muted]}>
            No packet has been assembled yet. When one is, every version stays available here.
          </Text>
        ) : (
          <View role="list" style={styles.list}>
            {list.packets.map((entry) => (
              <View role="listitem" key={entry.id} style={styles.row}>
                <Text style={[styles.rowTitle, ink]}>{entry.fileName}</Text>
                <Text style={[styles.body, muted]}>
                  Assembled {entry.createdAt.slice(0, 10)} · {formatSize(entry.byteSize)} ·{' '}
                  {entry.creditorCount} creditors on the matrix
                </Text>
                <View style={styles.actions}>
                  <Button
                    size="lg"
                    intent="secondary"
                    disabled={busyId === entry.id}
                    onPress={() => {
                      void download(entry);
                    }}
                    aria-label={`Download the packet assembled ${entry.createdAt.slice(0, 10)}`}
                  >
                    {busyId === entry.id ? 'Preparing…' : 'Download'}
                  </Button>
                </View>
              </View>
            ))}
          </View>
        )
      ) : (
        <Text style={[styles.body, muted]}>
          {list.kind === 'loading' ? 'Loading…' : `${list.message} Reload the page to try again.`}
        </Text>
      )}
    </AppShell>
  );
}

const styles = StyleSheet.create({
  actions: {
    flexDirection: 'row',
    marginTop: spacing.sm,
  },
  body: {
    fontSize: fontSizes.body,
    lineHeight: fontSizes.body * 1.5,
  },
  list: {
    gap: spacing.md,
    marginTop: spacing.sm,
  },
  problem: {
    gap: spacing.xs / 2,
  },
  problemSource: {
    fontSize: fontSizes.label,
    fontWeight: '600',
  },
  row: {
    gap: spacing.xs / 2,
  },
  rowTitle: {
    fontSize: fontSizes.body,
    fontWeight: '600',
  },
  status: {
    fontSize: fontSizes.label,
    minHeight: fontSizes.label * 1.5,
  },
});
