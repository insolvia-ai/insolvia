import type {
  PortalAnswerValue,
  PortalQuestion,
  PortalQuestionInput,
} from '@insolvia-ai/api-client';

/**
 * The portal questionnaire's form state, and the two conversions between
 * it and an answer's wire `value` (ADR 0023 PR 4). Pure, so the rules are
 * tested without rendering anything.
 *
 * Every box holds text while it is being edited — a whole number typed as
 * "12" is still a string until it is sent — and an address is a map of its
 * members' text. What the server stores is decided by the server: this file
 * only shapes what is sent, and every refusal comes back as a field error
 * keyed `value.<key>`.
 */

export const ADDRESS_MEMBERS = [
  { key: 'line1', label: 'Street address' },
  { key: 'line2', label: 'Apartment, suite or unit' },
  { key: 'city', label: 'City' },
  { key: 'state', label: 'State' },
  { key: 'postal_code', label: 'ZIP code' },
  { key: 'county', label: 'County' },
] as const;

export type AddressDraft = Readonly<Record<string, string>>;
export type AnswerDraft = Readonly<Record<string, string | AddressDraft>>;

/** An empty form for `question`. */
export function emptyDraft(question: PortalQuestion): AnswerDraft {
  return Object.fromEntries(
    question.inputs.map((input) => [input.key, input.type === 'address' ? {} : '']),
  );
}

/** The form for an answer already given — numbers back to text. */
export function draftFrom(question: PortalQuestion, value: PortalAnswerValue): AnswerDraft {
  const draft: Record<string, string | AddressDraft> = { ...emptyDraft(question) };
  for (const input of question.inputs) {
    const given = value[input.key];
    if (input.type === 'address') {
      draft[input.key] =
        typeof given === 'object' && given !== null && !Array.isArray(given)
          ? Object.fromEntries(
              Object.entries(given as Record<string, unknown>).map(([k, v]) => [k, String(v)]),
            )
          : {};
    } else if (given !== undefined && given !== null) {
      draft[input.key] = String(given);
    }
  }
  return draft;
}

function inputValue(input: PortalQuestionInput, raw: string | AddressDraft): unknown {
  if (typeof raw !== 'string') {
    const members = Object.entries(raw)
      .map(([key, text]) => [key, text.trim()] as const)
      .filter(([, text]) => text !== '');
    return members.length === 0 ? undefined : Object.fromEntries(members);
  }
  const text = raw.trim();
  if (text === '') return undefined;
  if (input.type === 'whole_number') {
    // Digits become a number; anything else is sent as typed, so the
    // server's "Must be a whole number" lands beside the box.
    return /^\d+$/.test(text) ? Number(text) : text;
  }
  if (input.type === 'money') {
    // "$1,200" is how people write it; the API wants "1200".
    return text.replace(/[$,\s]/g, '');
  }
  return text;
}

/** The wire `value` for a draft: blank boxes are left out, never sent empty. */
export function valueFrom(question: PortalQuestion, draft: AnswerDraft): PortalAnswerValue {
  const value: Record<string, unknown> = {};
  for (const input of question.inputs) {
    const raw = draft[input.key];
    if (raw === undefined) continue;
    const converted = inputValue(input, raw);
    if (converted !== undefined) value[input.key] = converted;
  }
  return value;
}

/** A short line naming an answer, for its row in the list. */
export function summary(question: PortalQuestion, value: PortalAnswerValue): string {
  const parts: string[] = [];
  for (const input of question.inputs) {
    const given = value[input.key];
    if (given === undefined || given === null) continue;
    if (typeof given === 'object') {
      const address = given as Record<string, unknown>;
      parts.push(
        ['line1', 'city', 'state']
          .map((key) => address[key])
          .filter((part): part is string => typeof part === 'string')
          .join(', '),
      );
    } else if (input.type === 'money') {
      parts.push(`$${String(given)}`);
    } else {
      parts.push(String(given));
    }
  }
  return parts.filter((part) => part !== '').join(' · ');
}
