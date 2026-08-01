// Direct unit tests for the shared aria-describedby rule. The DOM tests prove
// the wiring reaches the input; these pin the composition rule both leaves
// share — in particular that an absent part never leaves a dangling id behind.
import { describe, expect, it } from 'vitest';

import { composeDescribedBy, type FieldIds } from './field.props';

const ids: FieldIds = {
  labelId: ':r1:-label',
  controlId: ':r1:-control',
  descriptionId: ':r1:-description',
  errorId: ':r1:-error',
};

describe('composeDescribedBy', () => {
  it('is undefined when neither description nor error is rendered', () => {
    expect(composeDescribedBy(ids, false, false)).toBeUndefined();
  });

  it('names only the description when there is no error', () => {
    expect(composeDescribedBy(ids, true, false)).toBe(':r1:-description');
  });

  it('names only the error when there is no description', () => {
    expect(composeDescribedBy(ids, false, true)).toBe(':r1:-error');
  });

  it('joins both ids, description first, when both are rendered', () => {
    expect(composeDescribedBy(ids, true, true)).toBe(':r1:-description :r1:-error');
  });
});
