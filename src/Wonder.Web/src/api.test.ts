import { describe, it, expect, vi, afterEach } from "vitest";
import { api, ApiError, fieldError } from "./api";
afterEach(() => vi.unstubAllGlobals());
describe("API client", () => {
  it("keeps cookies and the CSRF header on writes", async () => {
    const fetch = vi
      .fn()
      .mockResolvedValue(new Response(null, { status: 204 }));
    vi.stubGlobal("fetch", fetch);
    await api("/bots", "POST", { name: "Wonder" });
    expect(fetch).toHaveBeenCalledWith(
      "/api/v1/bots",
      expect.objectContaining({
        credentials: "same-origin",
        headers: expect.objectContaining({ "X-Wonder": "1" }),
      }),
    );
  });
  it("maps FluentValidation fields case-insensitively", () => {
    expect(
      fieldError(
        new ApiError(400, "Invalid", { Email: ["Invalid email"] }),
        "email",
      ),
    ).toBe("Invalid email");
  });
});
