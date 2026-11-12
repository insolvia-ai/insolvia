import type { PortalQuestion } from '@insolvia-ai/api-client';

import { draftFrom, emptyDraft, summary, valueFrom } from './answer-form';

const VEHICLE: PortalQuestion = {
  id: 'property.vehicle',
  text: 'Do you own a car, truck, motorcycle or other vehicle?',
  repeats: true,
  perDebtor: false,
  inputs: [
    { key: 'year', label: 'Year', type: 'whole_number', required: false },
    { key: 'make', label: 'Make', type: 'text', required: true },
    { key: 'valueEntire', label: 'What it is worth today', type: 'money', required: false },
  ],
};

const HOME: PortalQuestion = {
  id: 'personal_information.residence_address',
  text: 'Where do you live?',
  repeats: false,
  perDebtor: true,
  inputs: [{ key: 'residence_address', label: 'Home address', type: 'address', required: false }],
};

describe('the answer form', () => {
  it('sends blank boxes as absent, never as empty strings', () => {
    expect(valueFrom(VEHICLE, { ...emptyDraft(VEHICLE), make: ' Examplecar ' })).toEqual({
      make: 'Examplecar',
    });
  });

  it('sends a whole number and money as numbers', () => {
    expect(valueFrom(VEHICLE, { year: '2099', make: 'X', valueEntire: '$1,200' })).toEqual({
      year: 2099,
      make: 'X',
      valueEntire: 1200,
    });
  });

  it('leaves a non-number as typed so the server can say what is wrong', () => {
    expect(valueFrom(VEHICLE, { year: 'new', make: 'X', valueEntire: '' })).toEqual({
      year: 'new',
      make: 'X',
    });
  });

  it('sends only the filled members of an address', () => {
    expect(
      valueFrom(HOME, { residence_address: { line1: '1 Example Way', city: ' ', state: 'FL' } }),
    ).toEqual({ residence_address: { line1: '1 Example Way', state: 'FL' } });
  });

  it('reopens a saved answer with every box as text', () => {
    expect(draftFrom(VEHICLE, { year: 2099, make: 'X' })).toEqual({
      year: '2099',
      make: 'X',
      valueEntire: '',
    });
  });

  it('summarises an answer for its row', () => {
    expect(summary(VEHICLE, { year: 2099, make: 'Examplecar', valueEntire: 1200 })).toBe(
      '2099 · Examplecar · $1200.00',
    );
  });
});
