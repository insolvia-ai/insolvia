import { useEffect, useRef, useState } from "react";
import { useNavigate } from "react-router";
import { Alert, Spinner } from "@insolvia-ai/design-system";

import { useSession } from "../session/session";

/** Completes the Google redirect. The exchange is single-use — the ref guard
 * stops React strict-mode's double effect from spending the code twice. */
export function AuthCallbackScreen() {
  const session = useSession();
  const navigate = useNavigate();
  const [error, setError] = useState<string | null>(null);
  const started = useRef(false);

  useEffect(() => {
    if (started.current) return;
    started.current = true;
    session
      .completeSignIn(new URLSearchParams(window.location.search))
      .then((returnTo) => navigate(returnTo, { replace: true }))
      .catch(() =>
        setError(
          "Sign-in could not be completed. Go back and try again — a stale or reused sign-in link does this.",
        ),
      );
  }, [session, navigate]);

  return (
    <div className="flex min-h-screen items-center justify-center px-6">
      {error === null ? (
        <Spinner aria-label="Completing sign-in" />
      ) : (
        <Alert.Root intent="danger"><Alert.Description>{error}</Alert.Description></Alert.Root>
      )}
    </div>
  );
}
