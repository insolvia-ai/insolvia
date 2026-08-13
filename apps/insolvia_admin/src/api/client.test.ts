/**
 * Contract pins: method, URL, headers, and body against literal JSON copied
 * from the admin service's route handlers — the same seam discipline the
 * api-client package applies to the tenant API. When the service changes a
 * shape, exactly one of these fails, naming the divergence.
 */

import { afterEach, describe, expect, it, vi } from 'vitest';

import { AdminClient, AdminApiError, AdminUnauthorizedError } from './client';

const BASE = 'http://127.0.0.1:8090';

function clientWithToken(token: string | null = 'id.jwt') {
  return new AdminClient(BASE, () => token);
}

function jsonResponse(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status });
}

afterEach(() => {
  vi.restoreAllMocks();
});

describe('authentication seam', () => {
  it('refuses to send at all without a token — expiry means re-authenticate', async () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch');
    await expect(clientWithToken(null).listFirms()).rejects.toBeInstanceOf(AdminUnauthorizedError);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('maps a 401 to AdminUnauthorizedError', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(jsonResponse({ error: 'Unauthorized' }, 401));
    await expect(clientWithToken().listFirms()).rejects.toBeInstanceOf(AdminUnauthorizedError);
  });
});

describe('the six routes', () => {
  it('GET /v1/firms with the bearer header', async () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      // Literal shape from list_firms_route: firm_json + userCount.
      jsonResponse({
        firms: [
          {
            id: '00000000-0000-4000-8000-00000000f18a',
            name: 'Example & Partners',
            status: 'active',
            createdAt: '2026-08-10T00:00:00.000Z',
            updatedAt: '2026-08-10T00:00:00.000Z',
            createdBy: '100000000000000000001',
            createdByEmail: 'operator@example-workspace.test',
            userCount: 1,
          },
        ],
      }),
    );
    const firms = await clientWithToken().listFirms();
    expect(fetchMock).toHaveBeenCalledWith(
      `${BASE}/v1/firms`,
      expect.objectContaining({
        method: 'GET',
        headers: { Authorization: 'Bearer id.jwt' },
      }),
    );
    expect(firms[0]?.name).toBe('Example & Partners');
    expect(firms[0]?.createdByEmail).toBe('operator@example-workspace.test');
  });

  it('POST /v1/firms with the provision body', async () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      jsonResponse(
        {
          firm: {
            id: '00000000-0000-4000-8000-00000000f18a',
            name: 'Example & Partners',
            status: 'active',
            createdAt: '2026-08-10T00:00:00.000Z',
            updatedAt: '2026-08-10T00:00:00.000Z',
            createdBy: '100000000000000000001',
            createdByEmail: 'operator@example-workspace.test',
          },
          admin: {
            subject: '00000000-0000-4000-8000-00000000a11c',
            email: 'admin@example.test',
            firstName: 'Alice',
            lastName: 'Attorney',
            displayName: 'Alice Attorney',
            role: 'attorney',
            isAdmin: true,
            accessAllCases: false,
            permissions: { cases: 'add_edit' },
            status: 'active',
            createdAt: '2026-08-10T00:00:00.000Z',
            updatedAt: '2026-08-10T00:00:00.000Z',
          },
        },
        201,
      ),
    );
    const result = await clientWithToken().provisionFirm({
      name: 'Example & Partners',
      admin: { email: 'admin@example.test', firstName: 'Alice', lastName: 'Attorney' },
    });
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe(`${BASE}/v1/firms`);
    expect(init.method).toBe('POST');
    expect(JSON.parse(String(init.body))).toEqual({
      name: 'Example & Partners',
      admin: { email: 'admin@example.test', firstName: 'Alice', lastName: 'Attorney' },
    });
    expect(result.admin.isAdmin).toBe(true);
  });

  it('PATCH /v1/firms/<id> carries only the status', async () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      jsonResponse({
        id: 'f-1',
        name: 'Example',
        status: 'suspended',
        createdAt: '2026-08-10T00:00:00.000Z',
        updatedAt: '2026-08-10T00:00:01.000Z',
        createdBy: null,
        createdByEmail: null,
      }),
    );
    await clientWithToken().setFirmStatus('f-1', 'suspended');
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe(`${BASE}/v1/firms/f-1`);
    expect(init.method).toBe('PATCH');
    expect(JSON.parse(String(init.body))).toEqual({ status: 'suspended' });
  });

  it('GET /v1/firms/<id>/users unwraps the users array', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(jsonResponse({ users: [] }));
    await expect(clientWithToken().listFirmUsers('f-1')).resolves.toEqual([]);
  });

  it('POST resend-invite accepts the 204', async () => {
    const fetchMock = vi
      .spyOn(globalThis, 'fetch')
      .mockResolvedValue(new Response(null, { status: 204 }));
    await clientWithToken().resendInvite('f-1', 'sub-1');
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe(`${BASE}/v1/firms/f-1/users/sub-1/resend-invite`);
    expect(init.method).toBe('POST');
  });

  it('surfaces field errors from a validation 400', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      jsonResponse({ error: 'ValidationError', fields: { name: 'A name is required.' } }, 400),
    );
    await expect(
      clientWithToken().provisionFirm({
        name: '',
        admin: { email: 'a@b.test', firstName: 'Ada', lastName: 'Admin' },
      }),
    ).rejects.toMatchObject({
      statusCode: 400,
      fields: { name: 'A name is required.' },
    });
  });

  it('surfaces the 409 message the duplicate-address path answers', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      jsonResponse(
        {
          error: 'ConflictError',
          message: 'that email address already has an Insolvia account',
        },
        409,
      ),
    );
    await expect(
      clientWithToken().provisionFirm({
        name: 'Example',
        admin: { email: 'taken@example.test', firstName: 'Ada', lastName: 'Admin' },
      }),
    ).rejects.toMatchObject({
      statusCode: 409,
      message: 'that email address already has an Insolvia account',
    });
    try {
      await clientWithToken().provisionFirm({
        name: 'Example',
        admin: { email: 'taken@example.test', firstName: 'Ada', lastName: 'Admin' },
      });
    } catch (caught) {
      expect(caught).toBeInstanceOf(AdminApiError);
    }
  });
});
