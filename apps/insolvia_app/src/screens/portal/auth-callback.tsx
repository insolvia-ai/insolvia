import { Button } from '@insolvia-ai/design-system';
import { useLocalSearchParams, useRouter } from 'expo-router';
import { useEffect, useRef, useState } from 'react';

import { PortalShell } from '@/components/portal-shell';
import { StatusScreen } from '@/components/status-screen';
import { usePortalSession } from '@/session';

/**
 * `/portal/auth/callback` — the portal's OAuth return leg (ADR 0023). The
 * staff callback's shape exactly (one exchange per mount, a failure that says
 * so with a way back), over the portal session and the portal frame.
 */
export function PortalAuthCallback() {
  const router = useRouter();
  const { completeSignIn } = usePortalSession();
  const params = useLocalSearchParams();
  const [failure, setFailure] = useState<string | null>(null);
  // The code is single-use: React 19's development double-effect must not
  // present it twice, or the second exchange fails and paints an error over
  // a sign-in that worked.
  const started = useRef(false);

  useEffect(() => {
    if (started.current) {
      return;
    }
    started.current = true;
    void (async () => {
      const result = await completeSignIn({
        code: firstValue(params.code),
        state: firstValue(params.state),
        error: firstValue(params.error),
      });
      if (result.ok) {
        router.replace(result.returnTo);
        return;
      }
      setFailure(result.message);
    })();
  }, [completeSignIn, params, router]);

  if (failure !== null) {
    return (
      <StatusScreen
        tone="error"
        shell={PortalShell}
        title="Sign-in could not be completed"
        message={failure}
        actions={
          <Button size="lg" onPress={() => router.replace('/portal/sign-in')}>
            Back to sign in
          </Button>
        }
      />
    );
  }
  return (
    <StatusScreen
      shell={PortalShell}
      title="Signing you in"
      message="Completing your sign-in. This takes a moment."
    />
  );
}

function firstValue(value: string | string[] | undefined): string | undefined {
  return Array.isArray(value) ? value[0] : value;
}
