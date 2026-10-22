import type { PageState, SourceContext } from "./types";

export const EXTRACTOR_VERSION = "weread-dom-v1";

const TITLE_SELECTORS = [
  ".readerTopBar_title_link",
  ".readerBookInfo_title",
  "[class*='readerTopBar'] [class*='title']",
  "[class*='bookInfo'] [class*='title']"
];
const AUTHOR_SELECTORS = [
  ".readerTopBar_title_author",
  ".readerBookInfo_author",
  "[class*='bookInfo'] [class*='author']"
];
const CHAPTER_SELECTORS = [
  ".readerChapterContent_title",
  ".readerTopBar_title_chapter",
  "[class*='chapterTitle']",
  "[class*='chapter'] [class*='title']"
];

function textFrom(document: Document, selectors: string[]): string | undefined {
  for (const selector of selectors) {
    const text = document.querySelector(selector)?.textContent?.trim();
    if (text) return text;
  }
  return undefined;
}

function meta(document: Document, selector: string): string | undefined {
  const value = document.querySelector<HTMLMetaElement>(selector)?.content?.trim();
  return value || undefined;
}

function cleanTitle(value?: string): string | undefined {
  const cleaned = value?.replace(/\s*[-—|]\s*微信读书\s*$/u, "").trim();
  return cleaned || undefined;
}

function splitAuthors(value?: string): string[] {
  if (!value) return [];
  return value.split(/[、,，;/；]/u).map((part) => part.trim()).filter(Boolean).slice(0, 20);
}

export function extractWereadContext(document: Document, href: string, now = new Date()): PageState {
  let url: URL;
  try {
    url = new URL(href);
  } catch {
    return { supported: false, reason: "页面地址无效" };
  }

  if (url.protocol !== "https:" || url.hostname !== "weread.qq.com") {
    return { supported: false, reason: "请打开微信读书网页版" };
  }

  const match = url.pathname.match(/^\/web\/(?:reader|bookDetail)\/([^/?#]+)/u);
  if (!match?.[1]) {
    return { supported: false, reason: "当前不是可识别的微信读书阅读页" };
  }

  const workTitle = cleanTitle(
    meta(document, "meta[property='og:title']") ??
    textFrom(document, TITLE_SELECTORS) ??
    document.title
  );
  const authorText = meta(document, "meta[name='author']") ?? textFrom(document, AUTHOR_SELECTORS);
  const chapterTitle = textFrom(document, CHAPTER_SELECTORS);
  const chapterId = url.searchParams.get("chapterUid") ??
    url.searchParams.get("chapterId") ??
    document.querySelector<HTMLElement>("[data-chapter-uid]")?.dataset.chapterUid;

  const context: SourceContext = {
    provider: "weread",
    work_id: decodeURIComponent(match[1]),
    work_title: workTitle,
    authors: splitAuthors(authorText),
    chapter_id: chapterId || undefined,
    chapter_title: chapterTitle,
    url: url.href,
    captured_at: now.toISOString(),
    extractor_version: EXTRACTOR_VERSION
  };
  return { supported: true, context };
}

