import { useCallback, useEffect, useState } from 'react';
import { useParams } from 'react-router';
import { Alert, Badge, Button, Spinner, Table } from '@insolvia-ai/design-system';

import {
  AdminApiError,
  AdminUnauthorizedError,
  type FirmSummary,
  type FirmUser,
} from '../api/client';
import { useClient } from '../api/use-client';
import { useSession } from '../session/session';

/** One firm: its status and people, with the three operator actions —
 * suspend, reactivate, resend a stranded invite. */
export function FirmDetailScreen() {
  const { firmId } = useParams<{ firmId: string }>();
  const client = useClient();
  const session = useSession();
  const [firm, setFirm] = useState<FirmSummary | null>(null);
  const [users, setUsers] = useState<readonly FirmUser[] | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const fail = useCallback(
    (caught: unknown, fallback: string) => {
      if (caught instanceof AdminUnauthorizedError) {
        session.signOut();
        return;
      }
      setError(caught instanceof AdminApiError ? caught.message : fallback);
    },
    [session],
  );

  const load = useCallback(() => {
    if (firmId === undefined) return;
    client
      .getFirm(firmId)
      .then(setFirm)
      .catch((caught: unknown) => fail(caught, 'This firm could not be loaded.'));
    client
      .listFirmUsers(firmId)
      .then(setUsers)
      .catch((caught: unknown) => fail(caught, "The firm's people could not be loaded."));
  }, [client, firmId, fail]);

  useEffect(() => {
    load();
  }, [load]);

  async function setStatus(status: 'active' | 'suspended') {
    if (firmId === undefined) return;
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      await client.setFirmStatus(firmId, status);
      setNotice(
        status === 'suspended'
          ? "Suspended. Every request from this firm's users now answers 403 — enforcement is immediate."
          : 'Reactivated.',
      );
      load();
    } catch (caught: unknown) {
      fail(caught, 'The status change did not apply.');
    } finally {
      setBusy(false);
    }
  }

  async function resend(subject: string, email: string) {
    if (firmId === undefined) return;
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      await client.resendInvite(firmId, subject);
      setNotice(`A fresh invitation is on its way to ${email}.`);
    } catch (caught: unknown) {
      // The 409 here is informative, not an error state: they already signed
      // in, and forgot-password is their way back — the message says so.
      fail(caught, 'The invitation could not be re-sent.');
    } finally {
      setBusy(false);
    }
  }

  if (firm === null) {
    return error !== null ? (
      <Alert.Root intent="danger">
        <Alert.Description>{error}</Alert.Description>
      </Alert.Root>
    ) : (
      <Spinner aria-label="Loading firm" />
    );
  }

  return (
    <div>
      <div className="mb-6 flex items-center justify-between">
        <div>
          <h1 className="font-serif text-2xl font-bold">{firm.name}</h1>
          <p className="text-sm text-muted">
            {firm.createdByEmail !== null
              ? `Provisioned by ${firm.createdByEmail} on ${firm.createdAt.slice(0, 10)}`
              : 'Seeded before the portal existed'}
          </p>
        </div>
        <div className="flex items-center gap-3">
          <Badge intent={firm.status === 'active' ? 'success' : 'danger'}>{firm.status}</Badge>
          {firm.status === 'active' ? (
            <Button intent="secondary" disabled={busy} onClick={() => void setStatus('suspended')}>
              Suspend
            </Button>
          ) : (
            <Button intent="primary" disabled={busy} onClick={() => void setStatus('active')}>
              Reactivate
            </Button>
          )}
        </div>
      </div>

      {notice !== null ? (
        <div className="mb-4">
          <Alert.Root intent="success">
            <Alert.Description>{notice}</Alert.Description>
          </Alert.Root>
        </div>
      ) : null}
      {error !== null ? (
        <div className="mb-4">
          <Alert.Root intent="danger">
            <Alert.Description>{error}</Alert.Description>
          </Alert.Root>
        </div>
      ) : null}

      <h2 className="mb-3 font-serif text-lg font-bold">People</h2>
      {users === null ? (
        <Spinner aria-label="Loading people" />
      ) : (
        <Table.Root>
          <Table.Head>
            <Table.Row>
              <Table.HeaderCell>Name</Table.HeaderCell>
              <Table.HeaderCell>Email</Table.HeaderCell>
              <Table.HeaderCell>Role</Table.HeaderCell>
              <Table.HeaderCell>Status</Table.HeaderCell>
              <Table.HeaderCell>Invitation</Table.HeaderCell>
            </Table.Row>
          </Table.Head>
          <Table.Body>
            {users.map((user) => (
              <Table.Row key={user.subject}>
                <Table.Cell>
                  {user.displayName}
                  {user.isAdmin ? (
                    <span className="ml-2 text-xs text-muted">administrator</span>
                  ) : null}
                </Table.Cell>
                <Table.Cell>{user.email}</Table.Cell>
                <Table.Cell>{user.role}</Table.Cell>
                <Table.Cell>
                  <Badge intent={user.status === 'active' ? 'success' : 'neutral'}>
                    {user.status}
                  </Badge>
                </Table.Cell>
                <Table.Cell>
                  <Button
                    intent="ghost"
                    size="sm"
                    disabled={busy}
                    onClick={() => void resend(user.subject, user.email)}
                  >
                    Resend invite
                  </Button>
                </Table.Cell>
              </Table.Row>
            ))}
          </Table.Body>
        </Table.Root>
      )}
    </div>
  );
}
