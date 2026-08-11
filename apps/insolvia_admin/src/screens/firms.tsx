import { useEffect, useState } from "react";
import { Link } from "react-router";
import { Alert, Badge, Button, Spinner, Table } from "@insolvia-ai/design-system";

import { AdminUnauthorizedError, type FirmSummary } from "../api/client";
import { useClient } from "../api/use-client";
import { useSession } from "../session/session";

/** The index view #212 built the Scan for: every firm, status, seats, and
 * who provisioned it — "seeded" for rows that predate the portal. */
export function FirmsScreen() {
  const client = useClient();
  const session = useSession();
  const [firms, setFirms] = useState<readonly FirmSummary[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    client
      .listFirms()
      .then((result) => {
        if (!cancelled) setFirms(result);
      })
      .catch((caught: unknown) => {
        if (cancelled) return;
        if (caught instanceof AdminUnauthorizedError) {
          session.signOut();
          return;
        }
        setError("The firm list could not be loaded.");
      });
    return () => {
      cancelled = true;
    };
  }, [client, session]);

  return (
    <div>
      <div className="mb-6 flex items-center justify-between">
        <h1 className="font-serif text-2xl font-bold">Firms</h1>
        <Link to="/firms/new">
          <Button intent="primary">Provision a firm</Button>
        </Link>
      </div>

      {error !== null ? <Alert.Root intent="danger"><Alert.Description>{error}</Alert.Description></Alert.Root> : null}
      {firms === null && error === null ? (
        <Spinner aria-label="Loading firms" />
      ) : null}

      {firms !== null && firms.length === 0 ? (
        <p className="text-muted">
          No firms yet. Provisioning the first one is the whole point — the
          button above is #178 closed.
        </p>
      ) : null}

      {firms !== null && firms.length > 0 ? (
        <Table.Root>
          <Table.Head>
            <Table.Row>
              <Table.HeaderCell>Firm</Table.HeaderCell>
              <Table.HeaderCell>Status</Table.HeaderCell>
              <Table.HeaderCell>Seats</Table.HeaderCell>
              <Table.HeaderCell>Provisioned by</Table.HeaderCell>
            </Table.Row>
          </Table.Head>
          <Table.Body>
            {firms.map((firm) => (
              <Table.Row key={firm.id}>
                <Table.Cell>
                  <Link
                    to={`/firms/${firm.id}`}
                    className="text-primary hover:underline"
                  >
                    {firm.name}
                  </Link>
                </Table.Cell>
                <Table.Cell>
                  <Badge
                    intent={firm.status === "active" ? "success" : "danger"}
                  >
                    {firm.status}
                  </Badge>
                </Table.Cell>
                <Table.Cell>{firm.userCount}</Table.Cell>
                <Table.Cell>
                  {/* null provenance is a REAL state (pre-portal rows), and
                      the honest rendering is "seeded", not a blank. */}
                  {firm.createdByEmail ?? (
                    <span className="text-muted">seeded</span>
                  )}
                </Table.Cell>
              </Table.Row>
            ))}
          </Table.Body>
        </Table.Root>
      ) : null}
    </div>
  );
}
