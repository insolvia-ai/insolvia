import type { PortalQuestionnaire } from '@insolvia-ai/api-client';
import { Button, Card } from '@insolvia-ai/design-system';
import { useRouter } from 'expo-router';
import { useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { usePortalApi } from '@/api/use-portal-api';
import { Heading } from '@/components/heading';
import { fontSizes, spacing, useTheme } from '@/theme';

type State =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly questionnaire: PortalQuestionnaire }
  | { readonly kind: 'error' };

/**
 * The questionnaire's sections as the client's firm configured them (ADR
 * 0023 PR 3), read from `GET /v1/portal/questionnaire`: each section's title
 * and the firm's instructions for it, in order. A section the firm switched
 * off never arrives, so this screen has nothing to hide and says nothing
 * about it.
 *
 * The questions, and the answers they write, are on `/portal/questionnaire`
 * (#363), which the button opens. Its own request rather than part of
 * `/v1/portal/me`, so a questionnaire that will not load costs this card and
 * not the landing screen.
 */
export function QuestionnaireOverview({ firmName }: { firmName: string }) {
  const theme = useTheme();
  const router = useRouter();
  const { call } = usePortalApi();
  const [state, setState] = useState<State>({ kind: 'loading' });

  useEffect(() => {
    let live = true;
    void (async () => {
      try {
        const result = await call((client) => client.getPortalQuestionnaire());
        if (live && result.ok) setState({ kind: 'ready', questionnaire: result.value });
      } catch {
        if (live) setState({ kind: 'error' });
      }
    })();
    return () => {
      live = false;
    };
  }, [call]);

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const ink = { color: theme.colors.ink, fontFamily: theme.typography.body };

  return (
    <Card.Root style={styles.card}>
      <Heading level={2} size="section">
        Your questionnaire
      </Heading>
      {state.kind === 'ready' ? (
        <>
          <Text style={[styles.body, muted]}>
            {firmName} will ask you about each of these. Have the documents they mention to hand.
          </Text>
          <View role="list" style={styles.sections}>
            {state.questionnaire.sections.map((section) => (
              <View role="listitem" key={section.id} style={styles.section}>
                <Heading level={3}>{section.title}</Heading>
                <Text style={[styles.body, ink]}>{section.instructions}</Text>
              </View>
            ))}
          </View>
          {state.questionnaire.sections.length === 0 ? null : (
            <Button
              size="lg"
              style={styles.start}
              onPress={() => router.push('/portal/questionnaire')}
            >
              Answer your questionnaire
            </Button>
          )}
        </>
      ) : (
        <Text
          aria-live={state.kind === 'error' ? 'assertive' : 'polite'}
          style={[styles.body, muted]}
        >
          {state.kind === 'error'
            ? 'Your questionnaire could not be loaded. Reload the page to try again.'
            : 'Loading your questionnaire…'}
        </Text>
      )}
    </Card.Root>
  );
}

const styles = StyleSheet.create({
  body: {
    fontSize: fontSizes.body,
    lineHeight: fontSizes.body * 1.5,
  },
  card: {
    gap: spacing.md,
    padding: spacing.lg,
  },
  section: {
    gap: spacing.xs,
  },
  sections: {
    gap: spacing.md,
  },
  start: {
    alignSelf: 'flex-start',
  },
});
