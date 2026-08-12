import { Button } from '@insolvia-ai/design-system';
import { useRouter } from 'expo-router';
import { StyleSheet, Text, View } from 'react-native';

import { AppShell } from '@/components/app-shell';
import { Heading } from '@/components/heading';
import { fontSizes, spacing, useTheme } from '@/theme';

/**
 * The signed-in shell's home screen.
 *
 * Deliberately thin: this milestone proves the delivery pipeline, not the
 * product. Everything visual comes from our own components — the {@link AppShell}
 * frame (wordmark, landmarks, centered max-width column), {@link Button},
 * {@link Heading} — and every color, radius and spacing step comes from
 * `@insolvia-ai/tokens` via `@/theme`, so none is spelled out here.
 *
 * It USED to end with two more things, both removed as leftovers: the
 * `GET /v1/me` panel that proved the authenticated round trip (issue #77),
 * which is support detail rather than product and now lives collapsed on
 * `/account`; and a line reading "Serving local · localhost", which repeated
 * what the header's environment badge already says and now appears once, in
 * the footer's build stamp.
 *
 * It is reached only through `RequireSession` (see `src/app/index.tsx`), so
 * everything below can assume a signed-in user.
 */
export function Home() {
  const theme = useTheme();
  const router = useRouter();

  const openCases = () => {
    router.push('/cases');
  };

  return (
    <AppShell>
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
});
