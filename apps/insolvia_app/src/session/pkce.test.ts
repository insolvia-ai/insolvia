import {
  base64UrlDecode,
  base64UrlEncode,
  CODE_CHALLENGE_METHOD,
  createCodeVerifier,
  createPkcePair,
  CryptoUnavailableError,
  deriveCodeChallenge,
  randomUrlSafeToken,
} from '@/session';

describe('base64url', () => {
  it('encodes without padding and with the URL-safe alphabet', () => {
    // 0xFB 0xFF 0xFE is chosen because standard base64 renders it "+//+",
    // exercising both characters base64url has to substitute.
    expect(base64UrlEncode(Uint8Array.from([0xfb, 0xff, 0xfe]))).toBe('-__-');
    expect(base64UrlEncode(Uint8Array.from([]))).toBe('');
  });

  it('encodes every partial trailing group', () => {
    // 1 and 2 leftover bytes are where an off-by-one in the bit shifting hides.
    expect(base64UrlEncode(Uint8Array.from([0x66]))).toBe('Zg');
    expect(base64UrlEncode(Uint8Array.from([0x66, 0x6f]))).toBe('Zm8');
    expect(base64UrlEncode(Uint8Array.from([0x66, 0x6f, 0x6f]))).toBe('Zm9v');
  });

  it('round-trips arbitrary bytes', () => {
    const bytes = Uint8Array.from(Array.from({ length: 64 }, (_, i) => (i * 7) % 256));
    expect(base64UrlDecode(base64UrlEncode(bytes))).toEqual(bytes);
  });

  it('also accepts standard base64 spelling, padding included', () => {
    expect(base64UrlDecode('-__-')).toEqual(base64UrlDecode('+//+'));
    expect(base64UrlDecode('Zm8=')).toEqual(Uint8Array.from([0x66, 0x6f]));
  });

  it('returns null rather than throwing on input that is not base64url', () => {
    // The only caller decodes a JWT that arrived over the network, where
    // malformed is an ordinary outcome to render around.
    expect(base64UrlDecode('!!!')).toBeNull();
    // A single leftover character cannot encode even one byte.
    expect(base64UrlDecode('Zm9vY')).toBeNull();
  });
});

describe('the PKCE code challenge', () => {
  it('derives the S256 challenge exactly as RFC 7636 appendix B specifies', async () => {
    // The RFC's own worked example, which is what makes this a real check of
    // the algorithm rather than a restatement of our implementation.
    const verifier = 'dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk';
    await expect(deriveCodeChallenge(verifier)).resolves.toBe(
      'E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM',
    );
  });

  it('produces a challenge that differs from its verifier', async () => {
    // `plain` is a legal PKCE method and the one this app must never use: it
    // sends the verifier itself, which a network observer can then replay.
    const { verifier, challenge } = await createPkcePair();
    expect(challenge).not.toBe(verifier);
    expect(challenge).toBe(await deriveCodeChallenge(verifier));
  });

  it('only ever announces S256', () => {
    expect(CODE_CHALLENGE_METHOD).toBe('S256');
  });
});

describe('the code verifier', () => {
  it('meets RFC 7636 section 4.1 — 43 to 128 unreserved characters', () => {
    const verifier = createCodeVerifier();
    expect(verifier).toHaveLength(43);
    expect(verifier).toMatch(/^[A-Za-z0-9\-._~]+$/);
  });

  it('is different every time', () => {
    const verifiers = new Set(Array.from({ length: 32 }, () => createCodeVerifier()));
    expect(verifiers.size).toBe(32);
  });
});

describe('when the runtime has no Web Crypto', () => {
  const realCrypto = Object.getOwnPropertyDescriptor(globalThis, 'crypto');

  afterEach(() => {
    if (realCrypto !== undefined) {
      Object.defineProperty(globalThis, 'crypto', realCrypto);
    }
  });

  function removeCrypto() {
    Object.defineProperty(globalThis, 'crypto', { value: undefined, configurable: true });
  }

  it('refuses to start rather than falling back to weak randomness', () => {
    // The important half of this test is the ABSENCE of a Math.random path: a
    // predictable verifier or `state` is worse than a sign-in that stops,
    // because it still looks like it worked.
    removeCrypto();
    expect(() => randomUrlSafeToken()).toThrow(CryptoUnavailableError);
  });

  it('refuses to derive a challenge', async () => {
    removeCrypto();
    await expect(deriveCodeChallenge('anything')).rejects.toThrow(CryptoUnavailableError);
  });
});
