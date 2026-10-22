import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import { extractWereadContext } from "../src/weread-adapter";

const html = readFileSync(resolve(process.cwd(), "tests/fixtures/weread-reader.html"), "utf8");
const fallbackHtml = readFileSync(resolve(process.cwd(), "tests/fixtures/weread-reader-fallback.html"), "utf8");

describe("WeRead DOM adapter", () => {
  it("extracts stable context from a sanitized reader fixture", () => {
    document.documentElement.innerHTML = html;
    const state = extractWereadContext(
      document,
      "https://weread.qq.com/web/reader/book-42?chapterUid=chapter-7",
      new Date("2026-10-22T02:00:00Z")
    );

    expect(state.supported).toBe(true);
    expect(state.context).toMatchObject({
      provider: "weread",
      work_id: "book-42",
      work_title: "架构示例",
      authors: ["甲", "乙"],
      chapter_id: "chapter-7",
      chapter_title: "第七章 可恢复写入",
      extractor_version: "weread-dom-v1"
    });
  });

  it("rejects unsupported pages instead of guessing source context", () => {
    expect(extractWereadContext(document, "https://example.com/web/reader/42")).toEqual({
      supported: false,
      reason: "请打开微信读书网页版"
    });
    expect(extractWereadContext(document, "https://weread.qq.com/web/shelf").supported).toBe(false);
  });

  it("degrades to alternate selectors and empty optional fields", () => {
    document.documentElement.innerHTML = fallbackHtml;
    const state = extractWereadContext(document, "https://weread.qq.com/web/reader/fallback-book");

    expect(state.context?.work_title).toBe("只有标题");
    expect(state.context?.chapter_title).toBe("加载中的章节");
    expect(state.context?.authors).toEqual([]);
    expect(state.context?.chapter_id).toBeUndefined();
  });
});
