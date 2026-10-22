import { describe, expect, it } from "vitest";
import { bindWhenNeeded, createDraft, hasSourceDrift, rebindDraft } from "../src/draft";
import type { PageState, SourceContext } from "../src/types";

function page(book: string, chapter: string): PageState {
  const context: SourceContext = {
    provider: "weread",
    work_id: book,
    work_title: book,
    authors: [],
    chapter_id: chapter,
    chapter_title: chapter,
    url: `https://weread.qq.com/web/reader/${book}?chapterUid=${chapter}`,
    captured_at: "2026-10-22T00:00:00Z",
    extractor_version: "test"
  };
  return { supported: true, context };
}

describe("draft source snapshot", () => {
  it("binds the first non-empty edit and detects chapter drift", () => {
    const original = page("book-a", "chapter-1");
    const draft = bindWhenNeeded({ ...createDraft("request-a"), comment: "想法" }, original);

    expect(draft.boundContext?.chapter_id).toBe("chapter-1");
    expect(hasSourceDrift(draft, page("book-a", "chapter-2"))).toBe(true);
    expect(hasSourceDrift(draft, original)).toBe(false);
  });

  it("requires a new request id when the user explicitly rebinds", () => {
    const draft = bindWhenNeeded({ ...createDraft("request-a"), comment: "想法" }, page("book-a", "chapter-1"));
    const rebound = rebindDraft(draft, page("book-b", "chapter-9"), "request-b");

    expect(rebound.requestId).toBe("request-b");
    expect(rebound.boundContext?.work_id).toBe("book-b");
  });
});
