import type { Case } from '@insolvia-ai/api-client';
import type { BadgeIntent } from '@insolvia-ai/design-system';

/**
 * How a case's own status reads, rather than the wire's snake_case, and the
 * badge it wears — shared by the case list and a client's record, which show
 * the same cases and should not disagree about what "ready_to_file" is called.
 */
export const CASE_STATUS_LABEL: Readonly<Record<Case['status'], string>> = {
  intake: 'In intake',
  ready_to_file: 'Ready to file',
  filed: 'Filed',
};

export const CASE_STATUS_INTENT: Readonly<Record<Case['status'], BadgeIntent>> = {
  intake: 'neutral',
  ready_to_file: 'success',
  filed: 'primary',
};
