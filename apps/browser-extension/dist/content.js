"use strict";
(() => {
  // src/weread-adapter.ts
  var EXTRACTOR_VERSION = "weread-dom-v1";
  var TITLE_SELECTORS = [
    ".readerTopBar_title_link",
    ".readerBookInfo_title",
    "[class*='readerTopBar'] [class*='title']",
    "[class*='bookInfo'] [class*='title']"
  ];
  var AUTHOR_SELECTORS = [
    ".readerTopBar_title_author",
    ".readerBookInfo_author",
    "[class*='bookInfo'] [class*='author']"
  ];
  var CHAPTER_SELECTORS = [
    ".readerChapterContent_title",
    ".readerTopBar_title_chapter",
    "[class*='chapterTitle']",
    "[class*='chapter'] [class*='title']"
  ];
  function textFrom(document2, selectors) {
    for (const selector of selectors) {
      const text = document2.querySelector(selector)?.textContent?.trim();
      if (text) return text;
    }
    return void 0;
  }
  function meta(document2, selector) {
    const value = document2.querySelector(selector)?.content?.trim();
    return value || void 0;
  }
  function cleanTitle(value) {
    const cleaned = value?.replace(/\s*[-—|]\s*微信读书\s*$/u, "").trim();
    return cleaned || void 0;
  }
  function splitAuthors(value) {
    if (!value) return [];
    return value.split(/[、,，;/；]/u).map((part) => part.trim()).filter(Boolean).slice(0, 20);
  }
  function extractWereadContext(document2, href, now = /* @__PURE__ */ new Date()) {
    let url;
    try {
      url = new URL(href);
    } catch {
      return { supported: false, reason: "\u9875\u9762\u5730\u5740\u65E0\u6548" };
    }
    if (url.protocol !== "https:" || url.hostname !== "weread.qq.com") {
      return { supported: false, reason: "\u8BF7\u6253\u5F00\u5FAE\u4FE1\u8BFB\u4E66\u7F51\u9875\u7248" };
    }
    const match = url.pathname.match(/^\/web\/(?:reader|bookDetail)\/([^/?#]+)/u);
    if (!match?.[1]) {
      return { supported: false, reason: "\u5F53\u524D\u4E0D\u662F\u53EF\u8BC6\u522B\u7684\u5FAE\u4FE1\u8BFB\u4E66\u9605\u8BFB\u9875" };
    }
    const workTitle = cleanTitle(
      meta(document2, "meta[property='og:title']") ?? textFrom(document2, TITLE_SELECTORS) ?? document2.title
    );
    const authorText = meta(document2, "meta[name='author']") ?? textFrom(document2, AUTHOR_SELECTORS);
    const chapterTitle = textFrom(document2, CHAPTER_SELECTORS);
    const chapterId = url.searchParams.get("chapterUid") ?? url.searchParams.get("chapterId") ?? document2.querySelector("[data-chapter-uid]")?.dataset.chapterUid;
    const context = {
      provider: "weread",
      work_id: decodeURIComponent(match[1]),
      work_title: workTitle,
      authors: splitAuthors(authorText),
      chapter_id: chapterId || void 0,
      chapter_title: chapterTitle,
      url: url.href,
      captured_at: now.toISOString(),
      extractor_version: EXTRACTOR_VERSION
    };
    return { supported: true, context };
  }

  // src/content.ts
  var lastUrl = location.href;
  var lastFingerprint = "";
  var refreshTimer;
  function currentState() {
    return extractWereadContext(document, location.href);
  }
  function scheduleRefresh() {
    window.clearTimeout(refreshTimer);
    refreshTimer = window.setTimeout(publishState, 250);
  }
  function publishState() {
    const state = currentState();
    const fingerprint = JSON.stringify(state.context ? { ...state, context: { ...state.context, captured_at: "" } } : state);
    if (fingerprint === lastFingerprint) return;
    lastFingerprint = fingerprint;
    void chrome.runtime.sendMessage({ type: "pkws:page-state", state }).catch(() => void 0);
  }
  window.addEventListener("popstate", scheduleRefresh);
  window.addEventListener("hashchange", scheduleRefresh);
  new MutationObserver(scheduleRefresh).observe(document.documentElement, {
    subtree: true,
    childList: true,
    attributes: true,
    attributeFilter: ["class", "data-chapter-uid"]
  });
  window.setInterval(() => {
    if (location.href !== lastUrl) {
      lastUrl = location.href;
      scheduleRefresh();
    }
  }, 750);
  chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
    if (!message || typeof message !== "object") return;
    const type = message.type;
    if (type === "pkws:get-page-state") {
      sendResponse({ state: currentState() });
    }
  });
  publishState();
})();
