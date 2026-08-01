import { Button } from '@insolvia-ai/design-system';
import { useRouter } from 'expo-router';
import { StyleSheet, Text } from 'react-native';

import { AppShell } from '@/components/app-shell';
import { Heading } from '@/components/heading';
import { fontSizes, useTheme } from '@/theme';

export interface NotFoundProps {
  /**
   * The path that matched no route, echoed back so a mistyped or stale link is
   * self-diagnosing.
   */
  location: string;
}

/**
 * The fallback for an unrecognised location — branded chrome and a way back.
 *
 * This is load-bearing, not decoration: CloudFront rewrites 403/404 to
 * `/index.html` with HTTP **200**, so every mistyped URL reaches the router and
 * this screen is the only thing that can say "not found".
 *
 * It lives in `screens/` rather than inside `src/app/+not-found.tsx` because
 * `src/app` is routes-only — the route file renders this.
 */
export function NotFound({ location }: NotFoundProps) {
  const theme = useTheme();
  const router = useRouter();

  return (
    <AppShell>
      <Heading level={1}>Page not found</Heading>
      <Text style={[styles.body, { color: theme.colors.muted, fontFamily: theme.typography.body }]}>
        Nothing lives at {location}.
      </Text>
      {/* size="lg": the package's md is 40dp, under the 44dp target-size floor. */}
      <Button
        size="lg"
        onPress={() => {
          router.replace('/');
        }}
      >
        Back to home
      </Button>
    </AppShell>
  );
}

const styles = StyleSheet.create({
  body: {
    fontSize: fontSizes.body,
    lineHeight: fontSizes.body * 1.5,
  },
});
