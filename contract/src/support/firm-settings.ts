import type {
  DocumentKind,
  FirmDocumentChecklist,
  FirmQuestionnaire,
} from '@insolvia-ai/api-client';

import { firmAdmin } from './actors.ts';

// Firm-wide settings have no scratch copy: a spec that changes one puts it
// back exactly as it found it — reset if the firm was on the defaults, saved
// again with what it had otherwise.
export async function restoreQuestionnaire(before: FirmQuestionnaire): Promise<void> {
  const api = firmAdmin().api;
  if (before.isDefault) {
    await api.resetFirmQuestionnaire();
    return;
  }
  await api.saveFirmQuestionnaire({
    sections: before.sections.map((s) => ({
      id: s.id,
      enabled: s.enabled,
      instructions: s.instructions,
    })),
  });
}

export async function restoreChecklist(before: FirmDocumentChecklist): Promise<void> {
  const api = firmAdmin().api;
  if (before.isDefault) {
    await api.resetFirmDocumentChecklist();
    return;
  }
  await api.saveFirmDocumentChecklist({
    items: before.items.map((i) => ({
      title: i.title,
      kind: i.kind as DocumentKind,
      ...(i.description === null ? {} : { description: i.description }),
    })),
  });
}
