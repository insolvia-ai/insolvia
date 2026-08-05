import { Button } from '@insolvia-ai/design-system';
import { useRouter } from 'expo-router';
import { StyleSheet, Text, View } from 'react-native';

import { AppShell } from '@/components/app-shell';
import { EnvBadge } from '@/components/env-badge';
import { Heading } from '@/components/heading';
import { MePanel } from '@/components/me-panel';
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
 *
 * It is reached only through `RequireSession` (see `src/app/index.tsx`), so
 * everything below can assume a signed-in user — which is what lets
 * {@link MePanel} call the protected `GET /v1/me` without a guard of its own.
 */
export function Home() {
  const theme = useTheme();
  const env = environmentInfo(appEnvironment);
  const router = useRouter();

  const openCases = () => {
    router.push('/cases');
  };

  return (
    <AppShell actions={<EnvBadge env={env.name} />}>
      <Heading level={1}>Your case workspace</Heading>
      <Text style={[styles.body, { color: theme.colors.muted, fontFamily: theme.typography.body }]}>
        This is the shell every Insolvia screen sits inside. Case intake, the forms engine, and
        e-filing each arrive in their own ticket.
      </Text>

      <View style={styles.actions}>
        {/*
          The design system's Button (its .native leaf — see metro.config.js).
          `size="lg"` (48dp) because the package's md is 40dp, under the 44dp
          WCAG 2.5.5 target-size floor this app enforces. The arrow is
          a decorative glyph, not part of the name: it renders `aria-hidden` and
          `aria-label` pins the accessible name to exactly the visible label,
          so a screen reader never announces "Start a case right arrow".
        */}
        <Button size="lg" aria-label="Start a case" onPress={openCases}>
          Start a case <Text aria-hidden>→</Text>
        </Button>
        <Button size="lg" intent="secondary" onPress={openCases}>
          Your cases
        </Button>
      </View>

      {/* The authenticated round trip, proven on screen (issue #77). */}
      <MePanel />

      <Text style={[styles.meta, { color: theme.colors.muted, fontFamily: theme.typography.body }]}>
        Serving {env.label.toLowerCase()} · {env.host}
      </Text>
    </AppShell>
  );
}

const styles = StyleSheet.create({
  actions: {
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
});
