import {
  ApiValidationException,
  DOCUMENT_CONTENT_TYPES,
  MAX_DOCUMENT_BYTE_SIZE,
  isDocumentContentType,
  isUploadIncomplete,
} from '@insolvia-ai/api-client';
import type { PortalDocumentRequest, PortalDocumentRequests } from '@insolvia-ai/api-client';
import { Badge, Button, Card, Meter } from '@insolvia-ai/design-system';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { usePortalApi } from '@/api/use-portal-api';
import { Heading } from '@/components/heading';
import { pickFile } from '@/screens/documents/browser';
import { fontSizes, spacing, useTheme } from '@/theme';

type State =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly value: PortalDocumentRequests }
  | { readonly kind: 'error' };

/** The allowlist, in the words a person uses for it. */
const ACCEPTED_TYPES = 'PDF, JPEG, PNG, HEIC and TIFF';

/** In the client's words: what the firm still needs, and what it has. */
const STATUS = {
  requested: { intent: 'warning', label: 'Needed' },
  received: { intent: 'success', label: 'Received' },
  waived: { intent: 'neutral', label: 'No longer needed' },
} as const;

/**
 * "Documents we need" on the portal landing screen (ADR 0023 PR 5 / #364):
 * each document the client's firm asked for, whether it has arrived, and an
 * upload for each one still taking files.
 *
 * Read from `GET /v1/portal/document-requests`, which carries the client's
 * OWN uploads against each request and nothing of the firm's — so a request
 * can read "Received" with no file listed, when the firm uploaded it
 * itself. Nothing here can download a file, not even the client's own: the
 * portal has no download in v1.
 *
 * An upload is the same create → PUT → complete as the firm's
 * (`uploadPortalDocument`), and completing it is what marks the request
 * received. Its own request, like the questionnaire card, so a list that
 * will not load costs this card and not the landing screen.
 */
export function DocumentsNeeded({ firmName }: { firmName: string }) {
  const theme = useTheme();
  const { call } = usePortalApi();
  const [state, setState] = useState<State>({ kind: 'loading' });
  const [busyId, setBusyId] = useState<string | null>(null);
  const [message, setMessage] = useState<{ tone: 'polite' | 'assertive'; text: string } | null>(
    null,
  );

  const load = useCallback(async () => {
    try {
      const result = await call((client) => client.getPortalDocumentRequests());
      if (result.ok) setState({ kind: 'ready', value: result.value });
    } catch {
      setState({ kind: 'error' });
    }
  }, [call]);

  useEffect(() => {
    void load();
  }, [load]);

  const upload = async (request: PortalDocumentRequest) => {
    setMessage(null);
    const outcome = await pickFile(DOCUMENT_CONTENT_TYPES);
    if (outcome.kind === 'dismissed') return;
    if (outcome.kind === 'unavailable') {
      setMessage({ tone: 'assertive', text: 'Uploading a file needs a web browser.' });
      return;
    }
    const file = outcome.file;
    if (!isDocumentContentType(file.contentType)) {
      setMessage({
        tone: 'assertive',
        text: `We accept ${ACCEPTED_TYPES}. Please choose a different file.`,
      });
      return;
    }
    if (file.size === 0 || file.size > MAX_DOCUMENT_BYTE_SIZE) {
      setMessage({
        tone: 'assertive',
        text: `A file must be ${String(MAX_DOCUMENT_BYTE_SIZE / (1024 * 1024))} MB or smaller, and not empty.`,
      });
      return;
    }
    const contentType = file.contentType;
    setBusyId(request.id);
    setMessage({ tone: 'polite', text: `Uploading ${file.name}…` });
    try {
      const result = await call((client) =>
        client.uploadPortalDocument({
          requestId: request.id,
          file: file.bytes,
          fileName: file.name,
          contentType,
        }),
      );
      if (result.ok) {
        setMessage({ tone: 'polite', text: `${file.name} is uploaded. Thank you.` });
      }
    } catch (cause) {
      setMessage({
        tone: 'assertive',
        text:
          cause instanceof ApiValidationException
            ? Object.values(cause.fields).join(' ') || 'That file could not be accepted.'
            : isUploadIncomplete(cause)
              ? `${file.name} did not finish uploading. Please try again.`
              : `${file.name} could not be uploaded. Please try again.`,
      });
    } finally {
      setBusyId(null);
      await load();
    }
  };

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const ink = { color: theme.colors.ink, fontFamily: theme.typography.body };

  return (
    <Card.Root style={styles.card}>
      <Heading level={2} size="section">
        Documents we need
      </Heading>
      {state.kind !== 'ready' ? (
        <Text
          aria-live={state.kind === 'error' ? 'assertive' : 'polite'}
          style={[styles.body, muted]}
        >
          {state.kind === 'error'
            ? 'Your document list could not be loaded. Reload the page to try again.'
            : 'Loading your document list…'}
        </Text>
      ) : state.value.requests.length === 0 ? (
        <Text style={[styles.body, muted]}>
          {firmName} has not asked you for any documents yet.
        </Text>
      ) : (
        <>
          <Progress value={state.value} />
          {message !== null ? (
            <Text aria-live={message.tone} style={[styles.body, ink]}>
              {message.text}
            </Text>
          ) : null}
          <View role="list" style={styles.requests}>
            {state.value.requests.map((request) => (
              <View role="listitem" key={request.id} style={styles.request}>
                <View style={styles.titleRow}>
                  <Heading level={3}>{request.title}</Heading>
                  <Badge intent={STATUS[request.status].intent} size="sm">
                    {STATUS[request.status].label}
                  </Badge>
                </View>
                {request.description !== null ? (
                  <Text style={[styles.body, ink]}>{request.description}</Text>
                ) : null}
                {request.uploads.length > 0 ? (
                  <Text style={[styles.caption, muted]}>
                    You sent:{' '}
                    {request.uploads
                      .map((u) =>
                        u.status === 'stored' ? u.fileName : `${u.fileName} (unfinished)`,
                      )
                      .join(', ')}
                  </Text>
                ) : null}
                {request.status !== 'waived' ? (
                  <Button
                    size="lg"
                    intent={request.status === 'received' ? 'secondary' : 'primary'}
                    disabled={busyId !== null}
                    aria-label={`Upload ${request.title}`}
                    onPress={() => void upload(request)}
                    style={styles.action}
                  >
                    {busyId === request.id
                      ? 'Uploading…'
                      : request.status === 'received'
                        ? 'Upload another file'
                        : 'Upload'}
                  </Button>
                ) : null}
              </View>
            ))}
          </View>
          <Text style={[styles.caption, muted]}>
            {ACCEPTED_TYPES}, up to {String(MAX_DOCUMENT_BYTE_SIZE / (1024 * 1024))} MB each. A
            photo from your phone is fine.
          </Text>
        </>
      )}
    </Card.Root>
  );
}

function Progress({ value }: { value: PortalDocumentRequests }) {
  const theme = useTheme();
  const { total, received } = value.progress;
  if (total === 0) return null;
  const label = `${String(received)} of ${String(total)} documents received`;
  return (
    <View style={styles.progress}>
      <Text style={[styles.body, { color: theme.colors.ink, fontFamily: theme.typography.body }]}>
        {label}
      </Text>
      <Meter.Root value={received} min={0} max={total} aria-label={label}>
        <Meter.Track>
          <Meter.Indicator />
        </Meter.Track>
      </Meter.Root>
    </View>
  );
}

const styles = StyleSheet.create({
  action: {
    alignSelf: 'flex-start',
  },
  body: {
    fontSize: fontSizes.body,
    lineHeight: fontSizes.body * 1.5,
  },
  caption: {
    fontSize: fontSizes.caption,
    lineHeight: fontSizes.caption * 1.5,
  },
  card: {
    gap: spacing.md,
    padding: spacing.lg,
  },
  progress: {
    gap: spacing.xs,
  },
  request: {
    gap: spacing.xs,
  },
  requests: {
    gap: spacing.lg,
  },
  titleRow: {
    alignItems: 'center',
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.sm,
  },
});
