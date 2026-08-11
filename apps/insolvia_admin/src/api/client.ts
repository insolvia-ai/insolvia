/**
 * A small typed client for the admin service's six routes.
 *
 * In-app rather than a published package: it has exactly one consumer, and
 * the api-client package's own lesson (contract pins at the seam) is applied
 * here directly — `client.test.ts` asserts method, URL, headers and body
 * against literal JSON copied from the service's route handlers.
 *
 * The token arrives through a PROVIDER read per request (the same seam the
 * app's client uses), and a 401 raises `AdminUnauthorizedError` so the
 * caller can route to sign-in — there is no refresh to attempt (#209:
 * memory-only tokens; expiry means re-authenticate).
 */

export interface FirmSummary {
  readonly id: string;
  readonly name: string;
  readonly status: "active" | "suspended";
  readonly createdAt: string;
  readonly updatedAt: string;
  readonly createdBy: string | null;
  readonly createdByEmail: string | null;
  readonly userCount: number;
}

export interface FirmUser {
  readonly subject: string;
  readonly email: string;
  readonly displayName: string;
  readonly role: string;
  readonly isAdmin: boolean;
  readonly accessAllCases: boolean;
  readonly permissions: Readonly<Record<string, string>>;
  readonly status: "active" | "disabled";
  readonly createdAt: string;
  readonly updatedAt: string;
}

export interface ProvisionRequest {
  readonly name: string;
  readonly admin: { readonly email: string; readonly displayName: string };
}

export class AdminApiError extends Error {
  readonly statusCode: number;
  /** Per-field messages when the service answered a field-validation 400. */
  readonly fields: Readonly<Record<string, string>> | null;

  constructor(
    statusCode: number,
    message: string,
    fields: Record<string, string> | null = null,
  ) {
    super(message);
    this.name = "AdminApiError";
    this.statusCode = statusCode;
    this.fields = fields;
  }
}

export class AdminUnauthorizedError extends AdminApiError {
  constructor() {
    super(401, "authentication required");
    this.name = "AdminUnauthorizedError";
  }
}

export type TokenProvider = () => string | null;

export class AdminClient {
  private readonly baseUrl: string;
  private readonly token: TokenProvider;

  // Explicit fields, not parameter properties: the root tsconfig's
  // erasableSyntaxOnly rejects the shorthand.
  constructor(baseUrl: string, token: TokenProvider) {
    this.baseUrl = baseUrl;
    this.token = token;
  }

  private async request<T>(
    method: string,
    path: string,
    body?: unknown,
  ): Promise<T> {
    const token = this.token();
    if (token === null) {
      throw new AdminUnauthorizedError();
    }
    const init: RequestInit = {
      method,
      headers: {
        Authorization: `Bearer ${token}`,
        ...(body !== undefined ? { "Content-Type": "application/json" } : {}),
      },
      // Assigned conditionally rather than `undefined`-valued: the root
      // tsconfig's exactOptionalPropertyTypes treats those differently.
      ...(body !== undefined ? { body: JSON.stringify(body) } : {}),
    };
    const response = await fetch(`${this.baseUrl}${path}`, init);

    if (response.status === 401) {
      throw new AdminUnauthorizedError();
    }
    if (response.status === 204) {
      return undefined as T;
    }
    const payload = (await response.json().catch(() => null)) as {
      message?: unknown;
      fields?: unknown;
    } | null;
    if (!response.ok) {
      const fields =
        payload !== null && typeof payload.fields === "object"
          ? (payload.fields as Record<string, string>)
          : null;
      const message =
        payload !== null && typeof payload.message === "string"
          ? payload.message
          : `request failed (${response.status})`;
      throw new AdminApiError(response.status, message, fields);
    }
    return payload as T;
  }

  async listFirms(): Promise<readonly FirmSummary[]> {
    const body = await this.request<{ firms: FirmSummary[] }>("GET", "/v1/firms");
    return body.firms;
  }

  async getFirm(firmId: string): Promise<FirmSummary> {
    return this.request<FirmSummary>("GET", `/v1/firms/${firmId}`);
  }

  async provisionFirm(
    request: ProvisionRequest,
  ): Promise<{ firm: Omit<FirmSummary, "userCount">; admin: FirmUser }> {
    return this.request("POST", "/v1/firms", request);
  }

  async setFirmStatus(
    firmId: string,
    status: "active" | "suspended",
  ): Promise<Omit<FirmSummary, "userCount">> {
    return this.request("PATCH", `/v1/firms/${firmId}`, { status });
  }

  async listFirmUsers(firmId: string): Promise<readonly FirmUser[]> {
    const body = await this.request<{ users: FirmUser[] }>(
      "GET",
      `/v1/firms/${firmId}/users`,
    );
    return body.users;
  }

  async resendInvite(firmId: string, subject: string): Promise<void> {
    await this.request<void>(
      "POST",
      `/v1/firms/${firmId}/users/${subject}/resend-invite`,
    );
  }
}
