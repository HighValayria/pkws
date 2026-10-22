import { afterEach, describe, expect, it, vi } from "vitest";
import { saveCapture } from "../src/api-client";
import type { CaptureRequest } from "../src/types";

const request: CaptureRequest = {
  schema_version: 1,
  request_id: "018f0000-0000-7000-8000-000000000001",
  source_type: "weread",
  content: "笔记",
  source_context: {
    provider: "weread",
    work_id: "book",
    authors: [],
    url: "https://weread.qq.com/web/reader/book",
    captured_at: "2026-10-22T00:00:00Z",
    extractor_version: "test"
  }
};

afterEach(() => vi.unstubAllGlobals());

describe("capture API client", () => {
  it("returns a committed response", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(JSON.stringify({
      request_id: request.request_id,
      entry_id: "entry",
      status: "committed",
      relative_path: "Daily/2026/2026-10-22.md",
      duplicate: false
    }), { status: 200, headers: { "Content-Type": "application/json" } })));

    const result = await saveCapture(request, "token", 100);
    expect(result.kind).toBe("committed");
  });

  it("queries status with the same request id after an unknown POST result", async () => {
    const fetchMock = vi.fn()
      .mockImplementationOnce((_url: string, init: RequestInit) => new Promise((_resolve, reject) => {
        init.signal?.addEventListener("abort", () => reject(new DOMException("aborted", "AbortError")));
      }))
      .mockResolvedValueOnce(new Response(JSON.stringify({
        request_id: request.request_id,
        entry_id: "entry",
        status: "committed",
        relative_path: "Daily/2026/2026-10-22.md"
      }), { status: 200, headers: { "Content-Type": "application/json" } }));
    vi.stubGlobal("fetch", fetchMock);

    const result = await saveCapture(request, "token", 5);

    expect(result.kind).toBe("committed");
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(String(fetchMock.mock.calls[1]?.[0])).toContain(request.request_id);
  });
});
