import { useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { AppShell } from '@/components/app-shell';
import { Button } from '@/components/button';
import { EnvBadge } from '@/components/env-badge';
import { Heading } from '@/components/heading';
import { appEnvironment, environmentInfo } from '@/config/environment';
import { fontSizes, spacing, useTheme } from '@/theme';

/**
 * The signed-in shell's home screen.
 *
 * Deliberately thin: this milestone proves the delivery pipeline, not the
 * product. Everything visual comes from our own components — the {@link AppShell}
 * frame (wordmark, landmarks, centered max-width column), {@link Button},
 * {@link Heading} — and every color, radius and spacing step comes from
 * `@insolvia-ai/tokens` via `@/theme`, so none is spelled out here. It also
 * surfaces the active environment, which is what makes a staging build
 * unmistakable at a glance.
 */
export function Home() {
  const theme = useTheme();
  const env = environmentInfo(appEnvironment);
  const [notice, setNotice] = useState<string | null>(null);

  const showSoon = () => {
    setNotice('Case tools arrive in a later release.');
  };

  return (
    <AppShell actions={<EnvBadge env={env.name} />}>
      <Heading level={1}>Your case workspace</Heading>
      <Text style={[styles.body, { color: theme.colors.muted, fontFamily: theme.typography.body }]}>
        This is the shell every Insolvia screen sits inside. Case intake, the forms engine, and
        e-filing each arrive in their own ticket.
      </Text>

      <View style={styles.actions}>
        <Button label="Start a case" icon="→" onPress={showSoon} />
        <Button label="Open a case" variant="secondary" onPress={showSoon} />
      </View>

      {/*
        The Flutter screen raised a SnackBar here. React Native has no
        equivalent, and a toast is the wrong shape for a message a keyboard or
        screen-reader user has to notice: `aria-live="polite"` announces it in
        place without stealing focus.
      */}
      {notice === null ? null : (
        <Text
          aria-live="polite"
          style={[styles.notice, { color: theme.colors.ink, fontFamily: theme.typography.body }]}
        >
          {notice}
        </Text>
      )}

      <Text style={[styles.meta, { color: theme.colors.muted, fontFamily: theme.typography.body }]}>
        Serving {env.label.toLowerCase()} · {env.host}
      </Text>
    </AppShell>
  );
}

const styles = StyleSheet.create({
  actions: {
    // The Flutter screen used a `Wrap`; this is its React Native equivalent.
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.md,
    marginTop: spacing.md,
  },
  body: {
    fontSize: fontSizes.body,
    lineHeight: fontSizes.body * 1.5,
  },
  meta: {
    fontSize: fontSizes.caption,
    marginTop: spacing.lg,
  },
  notice: {
    fontSize: fontSizes.label,
  },
});
