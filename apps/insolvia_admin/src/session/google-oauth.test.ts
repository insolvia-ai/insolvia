/**
 * The authorize URL and token exchange, pinned.
 *
 * The PKCE assertions matter the same way the app's do: Google enforces PKCE
 * only when a challenge is sent, so "we send one" is an assertion about OUR
 * code, checkable nowhere else. The no-refresh-token assertions pin #209's
 * memory-only decision — a future scope addition of offline access should
 * have to change a test that says why not.
 */

import { afterEach, describe, expect, it, vi } from "vitest";

import type { AdminConfig } from "../config/environment";
import {
  AUTHORIZE_ENDPOINT,
  OAuthError,
  TOKEN_ENDPOINT,
  authorizeUrl,
  callbackUrlFor,
  exchangeCodeForTokens,
} from "./google-oauth";

const CONFIG: AdminConfig = {
  environment: "local",
  googleClientId: "000000000000-fake.apps.googleusercontent.com",
  apiBaseUrl: "http://127.0.0.1:8090",
};

afterEach(() => {
  vi.restoreAllMocks();
});

describe("authorizeUrl", () => {
  const url = new URL(
    authorizeUrl(CONFIG, {
      redirectUri: callbackUrlFor("http://localhost:3100"),
      state: "state-token",
      codeChallenge: "challenge-value",
    }),
  );

  it("targets Google's authorize endpoint", () => {
    expect(`${url.origin}${url.pathname}`).toBe(AUTHORIZE_ENDPOINT);
  });

  it("carries the code flow, client id, and exact callback", () => {
    expect(url.searchParams.get("response_type")).toBe("code");
    expect(url.searchParams.get("client_id")).toBe(CONFIG.googleClientId);
    expect(url.searchParams.get("redirect_uri")).toBe(
      "http://localhost:3100/auth/callback",
    );
  });

  it("sends PKCE S256 — the assertion nothing server-side can make", () => {
    expect(url.searchParams.get("code_challenge")).toBe("challenge-value");
    expect(url.searchParams.get("code_challenge_method")).toBe("S256");
  });

  it("asks for openid email and NOTHING offline — no refresh token exists", () => {
    expect(url.searchParams.get("scope")).toBe("openid email");
    expect(url.searchParams.get("access_type")).toBeNull();
    expect(url.searchParams.get("prompt")).toBeNull();
  });

  it("hints the Workspace domain (a hint, never the enforcement)", () => {
    expect(url.searchParams.get("hd")).toBe("insolvia.ai");
  });
});

describe("exchangeCodeForTokens", () => {
  it("posts the code and verifier, keeps the id token, drops the access token", async () => {
    const fetchMock = vi
      .spyOn(globalThis, "fetch")
      .mockResolvedValue(
        new Response(
          JSON.stringify({
            id_token: "id.jwt.value",
            access_token: "dropped",
            expires_in: 3600,
          }),
          { status: 200 },
        ),
      );

    const tokens = await exchangeCodeForTokens(CONFIG, {
      code: "auth-code",
      codeVerifier: "verifier-value",
      redirectUri: "http://localhost:3100/auth/callback",
    });

    expect(fetchMock).toHaveBeenCalledWith(
      TOKEN_ENDPOINT,
      expect.objectContaining({
        method: "POST",
        headers: { "Content-Type": "application/x-www-form-urlencoded" },
      }),
    );
    const body = String((fetchMock.mock.calls[0]?.[1] as RequestInit).body);
    expect(body).toContain("grant_type=authorization_code");
    expect(body).toContain("code=auth-code");
    expect(body).toContain("code_verifier=verifier-value");

    expect(tokens.idToken).toBe("id.jwt.value");
    expect(tokens.expiresAt).toBeGreaterThan(Date.now());
    expect(Object.keys(tokens)).not.toContain("accessToken");
    expect(Object.keys(tokens)).not.toContain("refreshToken");
  });

  it("surfaces the OAuth error code on refusal, nothing else", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValue(
      new Response(JSON.stringify({ error: "invalid_grant" }), { status: 400 }),
    );
    await expect(
      exchangeCodeForTokens(CONFIG, {
        code: "spent-code",
        codeVerifier: "verifier",
        redirectUri: "http://localhost:3100/auth/callback",
      }),
    ).rejects.toMatchObject({ code: "invalid_grant", statusCode: 400 });
  });

  it("refuses a 200 with no id token", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValue(
      new Response(JSON.stringify({ access_token: "only" }), { status: 200 }),
    );
    await expect(
      exchangeCodeForTokens(CONFIG, {
        code: "code",
        codeVerifier: "verifier",
        redirectUri: "http://localhost:3100/auth/callback",
      }),
    ).rejects.toBeInstanceOf(OAuthError);
  });
});
