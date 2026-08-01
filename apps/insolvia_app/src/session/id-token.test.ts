import { readIdTokenClaims } from '@/session';
import { fakeJwt, TEST_EMAIL } from '@/session/testing';

describe('reading display identity from the ID token', () => {
  it('reads the email and subject claims', () => {
    const token = fakeJwt({ email: TEST_EMAIL, sub: '00000000-0000-4000-8000-000000000001' });

    expect(readIdTokenClaims(token)).toEqual({
      email: TEST_EMAIL,
      subject: '00000000-0000-4000-8000-000000000001',
    });
  });

  it('ignores the signature entirely', () => {
    // Stated as a test because it is a deliberate limitation, not an oversight:
    // this is decoding for display, and the API is what verifies. A test that
    // passes with a nonsense signature is the honest expression of that.
    const token = `${fakeJwt({ email: TEST_EMAIL }).split('.').slice(0, 2).join('.')}.garbage`;

    expect(readIdTokenClaims(token).email).toBe(TEST_EMAIL);
  });

  it.each([
    ['null', null],
    ['undefined', undefined],
    ['an empty string', ''],
    ['a string with no segments', 'not-a-jwt'],
    ['a token with too few segments', 'header.payload'],
    ['a payload that is not base64url', 'header.!!!.signature'],
    ['a payload that is not JSON', `header.${'bm90LWpzb24'}.signature`],
  ])('yields no claims for %s', (_label, token) => {
    // Every failure collapses to the same shape: the caller's fallback is
    // identical in all of them, and a sign-in that succeeded must not be undone
    // by a cosmetic decode.
    expect(readIdTokenClaims(token)).toEqual({ email: null, subject: null });
  });

  it('yields null for claims the token does not carry', () => {
    expect(readIdTokenClaims(fakeJwt({ token_use: 'id' }))).toEqual({ email: null, subject: null });
  });

  it('does not mistake a JSON array payload for claims', () => {
    expect(readIdTokenClaims(fakeJwt([] as unknown as Record<string, unknown>))).toEqual({
      email: null,
      subject: null,
    });
  });
});
