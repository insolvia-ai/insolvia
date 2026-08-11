import { useState } from "react";
import { useNavigate } from "react-router";
import { Alert, Button, Card, Field, Input } from "@insolvia-ai/design-system";

import { AdminApiError, AdminUnauthorizedError } from "../api/client";
import { useClient } from "../api/use-client";
import { useSession } from "../session/session";

/**
 * Provision a firm and its first administrator — the form that replaces the
 * shell session #178 refused to be.
 *
 * Server field errors render verbatim next to their inputs (the service's
 * FieldValidationError shape); nothing is validated twice here beyond
 * required-ness, because two validators drift and the service's answer is
 * the real one.
 */
export function ProvisionScreen() {
  const client = useClient();
  const session = useSession();
  const navigate = useNavigate();
  const [submitting, setSubmitting] = useState(false);
  const [fields, setFields] = useState<Readonly<Record<string, string>>>({});
  const [error, setError] = useState<string | null>(null);

  async function submit(form: FormData) {
    setSubmitting(true);
    setFields({});
    setError(null);
    try {
      const result = await client.provisionFirm({
        name: String(form.get("name") ?? ""),
        admin: {
          email: String(form.get("email") ?? ""),
          displayName: String(form.get("displayName") ?? ""),
        },
      });
      navigate(`/firms/${result.firm.id}`, { replace: true });
    } catch (caught: unknown) {
      if (caught instanceof AdminUnauthorizedError) {
        session.signOut();
        return;
      }
      if (caught instanceof AdminApiError) {
        if (caught.fields !== null) {
          setFields(caught.fields);
        } else {
          // The 409 that matters: the address already has an account.
          setError(caught.message);
        }
      } else {
        setError("Provisioning failed — nothing was created. Try again.");
      }
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <Card.Root className="mx-auto max-w-lg p-8">
      <h1 className="mb-1 font-serif text-2xl font-bold">Provision a firm</h1>
      <p className="mb-6 text-sm text-muted">
        The first administrator receives the invitation email with their
        temporary password — the address is also their sign-in name. They can
        add their colleagues themselves once in.
      </p>

      {error !== null ? (
        <div className="mb-4">
          <Alert.Root intent="danger"><Alert.Description>{error}</Alert.Description></Alert.Root>
        </div>
      ) : null}

      <form
        onSubmit={(event) => {
          event.preventDefault();
          void submit(new FormData(event.currentTarget));
        }}
        className="flex flex-col gap-5"
      >
        <Field.Root name="name" invalid={fields.name !== undefined}>
          <Field.Label>Firm name</Field.Label>
          <Input required />
          {fields.name !== undefined ? (
            <Field.Error>{fields.name}</Field.Error>
          ) : null}
        </Field.Root>

        <Field.Root
          name="displayName"
          invalid={fields.displayName !== undefined}
        >
          <Field.Label>First administrator's name</Field.Label>
          <Input required />
          {fields.displayName !== undefined ? (
            <Field.Error>{fields.displayName}</Field.Error>
          ) : null}
        </Field.Root>

        <Field.Root name="email" invalid={fields.email !== undefined}>
          <Field.Label>First administrator's work email</Field.Label>
          <Input type="email" required />
          {fields.email !== undefined ? (
            <Field.Error>{fields.email}</Field.Error>
          ) : null}
        </Field.Root>

        <Button intent="primary" type="submit" disabled={submitting}>
          {submitting ? "Provisioning…" : "Provision firm"}
        </Button>
      </form>
    </Card.Root>
  );
}
