import { Button } from '@insolvia-ai/design-system';
import { useLocalSearchParams, useRouter } from 'expo-router';
import { useEffect, useRef, useState } from 'react';

import { StatusScreen } from '@/components/status-screen';
import { useSession } from '@/session';

/**
 * The return leg of sign-in: the hosted UI redirects here with `?code=…&state=…`,
 * and this screen turns that into a session.
 *
 * **Its URL is pinned by infrastructure.** `web_callback_urls` in
 * `infra/modules/auth/main.tf` is `"${origin}/auth/callback"`, matched by
 * Cognito exactly, and under file-based routing the path *is* the route file's
 * location — so `src/app/auth/callback.tsx` cannot move without breaking
 * sign-in. `auth-callback.test.tsx` asserts the file exists at that path for
 * that reason.
 *
 * The exchange runs **once**, guarded by a ref rather than by an effect
 * dependency list: an authorization code is single-use, and a second exchange
 * of a code Cognito has already redeemed is not merely wasteful — a replayed
 * code is a signal Cognito treats as an attack and answers by invalidating the
 * tokens it just issued.
 *
 * Reading `code`/`state` out of the URL is deliberate and complete: validating
 * `state`, clearing the stored attempt, and exchanging the code all happen in
 * `completeSignIn`, so this file holds no security logic of its own — only the
 * three things a user sees.
 */
export function AuthCallback() {
  const router = useRouter();
  const { completeSignIn } = useSession();
  const params = useLocalSearchParams();
  const [failure, setFailure] = useState<string | null>(null);
  const started = useRef(false);

  useEffect(() => {
    if (started.current) {
      return;
    }
    started.current = true;

    const complete = async () => {
      const result = await completeSignIn({
        code: firstValue(params.code),
        state: firstValue(params.state),
        error: firstValue(params.error),
      });

      if (result.ok) {
        // `replace`, not `push`: the callback URL carries a spent code, and a
        // back-button entry that returns to it would land on this screen with
        // nothing left to exchange.
        router.replace(result.returnTo);
        return;
      }
      setFailure(result.message);
    };

    void complete();
  }, [completeSignIn, params, router]);

  if (failure !== null) {
    return (
      <StatusScreen
        tone="error"
        title="Sign-in could not be completed"
        message={failure}
        actions={
          // size="lg" for the 44dp WCAG 2.5.5 floor; the visible text is the
          // whole accessible name.
          <Button size="lg" onPress={() => router.replace('/sign-in')}>
            Back to sign in
          </Button>
        }
      />
    );
  }

  return (
    <StatusScreen title="Signing you in" message="Completing your sign-in. This takes a moment." />
  );
}

/**
 * A query parameter as a single string.
 *
 * `useLocalSearchParams` types every value `string | string[]`, because a
 * parameter can legally repeat (`?code=a&code=b`). Taking the first occurrence
 * is not a workaround: a duplicated `state` still has to match the stored one,
 * so a crafted repeat gains nothing.
 */
function firstValue(value: string | string[] | undefined): string | undefined {
  if (Array.isArray(value)) {
    return value[0];
  }
  return value;
}
