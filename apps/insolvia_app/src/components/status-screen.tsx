import type { ReactNode } from 'react';
import { StyleSheet, Text } from 'react-native';

import { AppShell } from '@/components/app-shell';
import { Heading } from '@/components/heading';
import { fontSizes, useTheme } from '@/theme';

/** Whether this state is something in progress or something that went wrong. */
export type StatusTone = 'progress' | 'error';

export interface StatusScreenProps {
  /** The page's one `<h1>`. */
  title: string;

  /** The explanation, announced as well as shown. */
  message: string;

  /** Defaults to `progress`. */
  tone?: StatusTone;

  /** Optional way forward, e.g. a "Back to sign in" button. */
  actions?: ReactNode;
}

/**
 * A whole-page "here is what is happening" screen: one heading, one announced
 * message, and an optional way forward.
 *
 * It exists because sign-in has **five** of these states — restoring a session,
 * leaving for the hosted UI, completing the exchange, an exchange that failed,
 * and an environment with no hosted UI at all — and each one is a place the
 * accessibility wiring could be forgotten. Centralising it means the two rules
 * that matter are written once:
 *
 * - **Every state carries exactly one `<h1>`.** `app-pr.yml`'s axe audit fails
 *   on `page-has-heading-one` and `heading-order`, and a spinner-only loading
 *   state is precisely the page that ships without a heading.
 * - **Every state is announced, not just drawn.** A screen reader user gets no
 *   signal from a layout change. `aria-live` on the message is what turns
 *   "signing you in…" into something heard: `assertive` for an error, because
 *   the flow has stopped and waiting for a pause would be misleading, and
 *   `polite` for progress, which must not interrupt.
 *
 * Note there is no `role="region"` and no extra landmark — `AppShell` already
 * provides `main`, and an unnamed `<section>` is what axe flags.
 */
export function StatusScreen({ title, message, tone = 'progress', actions }: StatusScreenProps) {
  const theme = useTheme();

  return (
    <AppShell>
      <Heading level={1}>{title}</Heading>
      <Text
        aria-live={tone === 'error' ? 'assertive' : 'polite'}
        style={[styles.message, { color: theme.colors.muted, fontFamily: theme.typography.body }]}
      >
        {message}
      </Text>
      {actions}
    </AppShell>
  );
}

const styles = StyleSheet.create({
  message: {
    fontSize: fontSizes.body,
    lineHeight: fontSizes.body * 1.5,
  },
});
