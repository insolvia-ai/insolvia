// Every route of the admin service (backend/src/Insolvia.Admin), for its
// OpenAPI document. The shape of the table and the checks that hold it to the
// server are described in api-routes.ts; the types come from the portal's
// client, packages/insolvia_admin_client/src/client.ts, which has no doc
// comments of its own — so each entry carries its summary here.

import type { Route } from '../render-openapi.ts';

export const ADMIN_ROUTES: readonly Route[] = [
  {
    method: 'GET',
    path: '/health',
    auth: 'none',
    summary: 'Liveness, and which build answered.',
    responses: { 200: 'Health' },
  },
  {
    method: 'GET',
    path: '/v1/firms',
    call: 'listFirms',
    auth: 'google',
    summary: 'Every firm, with its user count.',
    responses: { 200: { firms: ['FirmSummary'] } },
  },
  {
    method: 'POST',
    path: '/v1/firms',
    call: 'provisionFirm',
    auth: 'google',
    summary: 'Provision a firm and invite its first admin.',
    description:
      'Creates the firm and its first user, an admin with every permission, and sends that person a sign-in invitation. A 409 means a firm or user with that identity already exists.',
    body: 'ProvisionRequest',
    responses: { 201: { firm: 'FirmRecord', admin: 'FirmUser' } },
  },
  {
    method: 'GET',
    path: '/v1/firms/{firmId}',
    call: 'getFirm',
    auth: 'google',
    summary: 'One firm, with its user count.',
    responses: { 200: 'FirmSummary' },
  },
  {
    method: 'PATCH',
    path: '/v1/firms/{firmId}',
    call: 'setFirmStatus',
    auth: 'google',
    summary: 'Suspend or reactivate a firm.',
    description:
      'A suspended firm stays in place, but none of its users can reach the API until it is reactivated.',
    body: { status: "'active'|'suspended'" },
    responses: { 200: 'FirmRecord' },
  },
  {
    method: 'GET',
    path: '/v1/firms/{firmId}/users',
    call: 'listFirmUsers',
    auth: 'google',
    summary: "A firm's users.",
    responses: { 200: { users: ['FirmUser'] } },
  },
  {
    method: 'POST',
    path: '/v1/firms/{firmId}/users/{subject}/resend-invite',
    call: 'resendInvite',
    auth: 'google',
    summary: 'Send a user their sign-in invitation again.',
    responses: { 204: null },
  },
];
