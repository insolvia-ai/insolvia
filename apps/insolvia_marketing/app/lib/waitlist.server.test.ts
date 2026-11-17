import { afterEach, describe, expect, it, vi } from "vitest";

import {
  parseWaitlistForm,
  putWaitlistSubmission,
  WaitlistValidationError,
  type WaitlistFields,
} from "./waitlist.server";

/**
 * The waitlist path is the only place this marketing site writes anything
 * anywhere, so it is the first thing worth testing here.
 *
 * Two halves, tested differently: `parseWaitlistForm` is pure and gets
 * table-driven cases; `putWaitlistSubmission` talks to the API and gets a
 * stubbed `fetch` asserting the exact request and the exact handling of each
 * documented response.
 */

function formOf(fields: Partial<WaitlistFields>): FormData {
  const form = new FormData();
  for (const [key, value] of Object.entries(fields)) form.append(key, value);
  return form;
}

const VALID: WaitlistFields = {
  name: "Dana Okafor",
  firm: "Okafor Legal",
  email: "dana@example.test",
  currentSoftware: "",
  message: "",
};

describe("parseWaitlistForm", () => {
  it("accepts a valid submission and reports no errors", () => {
    const { values, errors } = parseWaitlistForm(formOf(VALID));

    expect(errors).toBeNull();
    expect(values).toEqual(VALID);
  });

  it("trims every field so whitespace cannot satisfy a required field", () => {
    const { values, errors } = parseWaitlistForm(
      formOf({ ...VALID, name: "  Dana Okafor  ", message: "  hi  " }),
    );

    expect(errors).toBeNull();
    expect(values.name).toBe("Dana Okafor");
    expect(values.message).toBe("hi");
  });

  it("treats a whitespace-only required field as missing", () => {
    const { errors } = parseWaitlistForm(formOf({ ...VALID, firm: "   " }));

    expect(errors?.firm).toBeTruthy();
  });

  it("returns the trimmed values even when invalid, so the form can re-render them", () => {
    // Losing what the visitor typed on a validation bounce is the classic form
    // bug; the function's contract is that `values` is always populated.
    const { values, errors } = parseWaitlistForm(
      formOf({ ...VALID, name: "  Dana  ", email: "not-an-email" }),
    );

    expect(errors).not.toBeNull();
    expect(values.name).toBe("Dana");
    expect(values.email).toBe("not-an-email");
  });

  it.each([
    ["name", { name: "" }],
    ["firm", { firm: "" }],
    ["email", { email: "" }],
  ] as const)("requires %s", (field, override) => {
    const { errors } = parseWaitlistForm(formOf({ ...VALID, ...override }));

    expect(errors?.[field]).toBeTruthy();
  });

  it("reports every missing required field at once, not just the first", () => {
    const { errors } = parseWaitlistForm(
      formOf({ name: "", firm: "", email: "" }),
    );

    expect(Object.keys(errors ?? {}).sort()).toEqual(["email", "firm", "name"]);
  });

  it.each([
    "no-at-sign",
    "no@dot",
    "spaces in@example.test",
    "two@@example.test",
    "@example.test",
    "dana@",
  ])("rejects %j as an email", (email) => {
    const { errors } = parseWaitlistForm(formOf({ ...VALID, email }));

    expect(errors?.email).toBeTruthy();
  });

  it.each(["dana@example.test", "dana+waitlist@sub.example.co.uk"])(
    "accepts %j as an email",
    (email) => {
      const { errors } = parseWaitlistForm(formOf({ ...VALID, email }));

      expect(errors?.email).toBeUndefined();
    },
  );

  /**
   * These caps are documented in waitlist.server.ts as mirroring
   * `services/api` `core/waitlist.py` EXACTLY. Nothing mechanical keeps the two
   * in step, so this table is the drift alarm: if the API's limits move and
   * these do not, a submission this layer accepts gets rejected by the API and
   * the visitor sees a generic failure on a form that looked fine.
   */
  describe("length caps (mirrors services/api core/waitlist.py)", () => {
    const CAPS = {
      name: 200,
      firm: 200,
      email: 320,
      currentSoftware: 100,
      message: 2000,
    } as const;

    it.each(Object.entries(CAPS))("accepts %s at exactly %d characters", (field, cap) => {
      const value =
        field === "email"
          ? `${"a".repeat(cap - "@example.test".length)}@example.test`
          : "a".repeat(cap);
      const { errors } = parseWaitlistForm(
        formOf({ ...VALID, [field]: value } as Partial<WaitlistFields>),
      );

      expect(errors?.[field as keyof WaitlistFields]).toBeUndefined();
    });

    it.each(Object.entries(CAPS))("rejects %s at %d + 1 characters", (field, cap) => {
      const value =
        field === "email"
          ? `${"a".repeat(cap + 1 - "@example.test".length)}@example.test`
          : "a".repeat(cap + 1);
      const { errors } = parseWaitlistForm(
        formOf({ ...VALID, [field]: value } as Partial<WaitlistFields>),
      );

      expect(errors?.[field as keyof WaitlistFields]).toBeTruthy();
    });
  });

  it("keeps the format error for an over-long invalid email rather than replacing it", () => {
    // The cap loop skips fields that already have an error, so the more
    // specific message wins.
    const { errors } = parseWaitlistForm(
      formOf({ ...VALID, email: "x".repeat(400) }),
    );

    expect(errors?.email).toContain("valid email");
  });

  it("ignores a non-string field value instead of throwing", () => {
    const form = formOf(VALID);
    form.set("name", new Blob(["x"]));

    const { values, errors } = parseWaitlistForm(form);

    expect(values.name).toBe("");
    expect(errors?.name).toBeTruthy();
  });
});

describe("putWaitlistSubmission", () => {
  afterEach(() => {
    vi.unstubAllEnvs();
    vi.unstubAllGlobals();
  });

  // Params are declared even though the body ignores them: that is what gives
  // `mock.calls` a real tuple type, so the assertions below read the request
  // without casting.
  function stubFetch(response: Response | Error) {
    const fetchMock = vi.fn((_input: string, _init?: RequestInit) =>
      response instanceof Error ? Promise.reject(response) : Promise.resolve(response),
    );
    vi.stubGlobal("fetch", fetchMock);
    return fetchMock;
  }

  /** The request the client actually sent — mirrors the api-client tests' `lastRequest()`. */
  function lastCall(mock: ReturnType<typeof stubFetch>) {
    const call = mock.mock.calls.at(-1);
    if (!call) throw new Error("fetch was never called");
    return { url: call[0], body: JSON.parse(String(call[1]?.body)) as Record<string, string> };
  }

  function jsonResponse(body: unknown, status: number): Response {
    return new Response(JSON.stringify(body), {
      status,
      headers: { "Content-Type": "application/json" },
    });
  }

  it("POSTs to the API's /v1/waitlist with the required fields and the host", async () => {
    vi.stubEnv("INSOLVIA_API_BASE_URL", "https://api.example.test");
    const fetchMock = stubFetch(jsonResponse({ id: "wl_123" }, 201));

    await putWaitlistSubmission(VALID, "www.example.test");

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(fetchMock.mock.calls[0]?.[1]?.method).toBe("POST");
    const { url, body } = lastCall(fetchMock);
    expect(url).toBe("https://api.example.test/v1/waitlist");
    expect(body).toEqual({
      name: VALID.name,
      firm: VALID.firm,
      email: VALID.email,
      host: "www.example.test",
    });
  });

  it("omits empty optional fields rather than sending empty strings", async () => {
    // The API's storage convention is omit-when-empty; sending "" would store
    // a meaningless key.
    vi.stubEnv("INSOLVIA_API_BASE_URL", "https://api.example.test");
    const fetchMock = stubFetch(jsonResponse({ id: "wl_123" }, 201));

    await putWaitlistSubmission(VALID, "www.example.test");

    const { body } = lastCall(fetchMock);
    expect(body).not.toHaveProperty("currentSoftware");
    expect(body).not.toHaveProperty("message");
  });

  it("includes optional fields when they are present", async () => {
    vi.stubEnv("INSOLVIA_API_BASE_URL", "https://api.example.test");
    const fetchMock = stubFetch(jsonResponse({ id: "wl_123" }, 201));

    await putWaitlistSubmission(
      { ...VALID, currentSoftware: "Best Case", message: "Interested." },
      "www.example.test",
    );

    const { body } = lastCall(fetchMock);
    expect(body.currentSoftware).toBe("Best Case");
    expect(body.message).toBe("Interested.");
  });

  it("does not double the slash when the base URL has a trailing one", async () => {
    vi.stubEnv("INSOLVIA_API_BASE_URL", "https://api.example.test/");
    const fetchMock = stubFetch(jsonResponse({ id: "wl_123" }, 201));

    await putWaitlistSubmission(VALID, "www.example.test");

    expect(lastCall(fetchMock).url).toBe("https://api.example.test/v1/waitlist");
  });

  it("resolves on 201", async () => {
    vi.stubEnv("INSOLVIA_API_BASE_URL", "https://api.example.test");
    stubFetch(jsonResponse({ id: "wl_123" }, 201));

    await expect(
      putWaitlistSubmission(VALID, "www.example.test"),
    ).resolves.toBeUndefined();
  });

  it("raises WaitlistValidationError carrying the API's per-field messages on 400", async () => {
    vi.stubEnv("INSOLVIA_API_BASE_URL", "https://api.example.test");
    stubFetch(jsonResponse({ fields: { email: "Already on the list." } }, 400));

    await expect(
      putWaitlistSubmission(VALID, "www.example.test"),
    ).rejects.toBeInstanceOf(WaitlistValidationError);
  });

  it("falls back to a generic error on a 400 with no fields object", async () => {
    vi.stubEnv("INSOLVIA_API_BASE_URL", "https://api.example.test");
    stubFetch(jsonResponse({ error: "ValidationError" }, 400));

    await expect(
      putWaitlistSubmission(VALID, "www.example.test"),
    ).rejects.not.toBeInstanceOf(WaitlistValidationError);
  });

  it.each([500, 502, 403])("raises on a %d so the route renders its retry state", async (status) => {
    vi.stubEnv("INSOLVIA_API_BASE_URL", "https://api.example.test");
    stubFetch(new Response("upstream failed", { status }));

    await expect(
      putWaitlistSubmission(VALID, "www.example.test"),
    ).rejects.toThrow(String(status));
  });

  it("lets a transport failure propagate untouched", async () => {
    vi.stubEnv("INSOLVIA_API_BASE_URL", "https://api.example.test");
    stubFetch(new TypeError("network down"));

    await expect(
      putWaitlistSubmission(VALID, "www.example.test"),
    ).rejects.toBeInstanceOf(TypeError);
  });

  it("does not call the API at all when the base URL is unset", async () => {
    // Local dev without the API running: the documented behaviour is to log
    // and treat as accepted rather than crash the form.
    vi.stubEnv("INSOLVIA_API_BASE_URL", "");
    const fetchMock = stubFetch(jsonResponse({}, 201));
    vi.spyOn(console, "log").mockImplementation(() => {});

    await expect(
      putWaitlistSubmission(VALID, "www.example.test"),
    ).resolves.toBeUndefined();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("never logs the visitor's details on success — only the server-generated id", async () => {
    // GLBA-adjacent discipline: this Lambda's logs must not carry PII, and a
    // waitlist row is a real person's name, firm and work email.
    vi.stubEnv("INSOLVIA_API_BASE_URL", "https://api.example.test");
    stubFetch(jsonResponse({ id: "wl_123" }, 201));
    const log = vi.spyOn(console, "log").mockImplementation(() => {});

    await putWaitlistSubmission(VALID, "www.example.test");

    const logged = log.mock.calls.flat().join(" ");
    expect(logged).toContain("wl_123");
    expect(logged).not.toContain(VALID.email);
    expect(logged).not.toContain(VALID.name);
    expect(logged).not.toContain(VALID.firm);
  });
});
