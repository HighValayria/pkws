"use strict";
(() => {
  // src/api-client.ts
  var API_BASE = "http://127.0.0.1:27123/v1";
  async function responseError(response) {
    let body = {};
    try {
      body = await response.json();
    } catch {
    }
    return {
      kind: "error",
      code: body.code ?? `HTTP_${response.status}`,
      message: body.message ?? `\u672C\u5730\u670D\u52A1\u8FD4\u56DE ${response.status}`,
      retryable: body.retryable ?? response.status >= 500
    };
  }
  function headers(token) {
    return {
      "Content-Type": "application/json",
      ...token ? { Authorization: `Bearer ${token}` } : {}
    };
  }
  async function pairExtension(code, extensionId) {
    const response = await fetch(`${API_BASE}/pairings`, {
      method: "POST",
      headers: headers(),
      body: JSON.stringify({ code, extension_id: extensionId })
    });
    if (!response.ok) {
      const failure = await responseError(response);
      throw new Error(failure.kind === "error" ? failure.message : "\u914D\u5BF9\u5931\u8D25");
    }
    const body = await response.json();
    return body.access_token;
  }
  async function getHealth() {
    try {
      const response = await fetch(`${API_BASE}/health`, { cache: "no-store" });
      return response.ok;
    } catch {
      return false;
    }
  }
  async function saveCapture(request, token, timeoutMs = 8e3) {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), timeoutMs);
    try {
      const response = await fetch(`${API_BASE}/captures`, {
        method: "POST",
        headers: headers(token),
        body: JSON.stringify(request),
        signal: controller.signal
      });
      if (!response.ok) return responseError(response);
      return { kind: "committed", response: await response.json() };
    } catch (error) {
      const status = await queryStatus(request.request_id, token, 3e3);
      if (status?.status === "committed") {
        return {
          kind: "committed",
          response: {
            request_id: request.request_id,
            entry_id: status.entry_id,
            status: "committed",
            relative_path: status.relative_path ?? "",
            duplicate: true
          }
        };
      }
      return {
        kind: "uncertain",
        message: error instanceof DOMException && error.name === "AbortError" ? "\u4FDD\u5B58\u54CD\u5E94\u8D85\u65F6\uFF0C\u7ED3\u679C\u5C1A\u672A\u786E\u8BA4\u3002\u8BF7\u4FDD\u7559\u8349\u7A3F\u5E76\u7528\u540C\u4E00\u8BF7\u6C42\u91CD\u8BD5\u3002" : "\u65E0\u6CD5\u786E\u8BA4\u4FDD\u5B58\u7ED3\u679C\u3002\u8BF7\u68C0\u67E5\u684C\u9762\u7AEF\u540E\u7528\u540C\u4E00\u8BF7\u6C42\u91CD\u8BD5\u3002"
      };
    } finally {
      clearTimeout(timeout);
    }
  }
  async function queryStatus(requestId, token, timeoutMs) {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), timeoutMs);
    try {
      const response = await fetch(`${API_BASE}/captures/${encodeURIComponent(requestId)}`, {
        headers: headers(token),
        signal: controller.signal,
        cache: "no-store"
      });
      return response.ok ? await response.json() : void 0;
    } catch {
      return void 0;
    } finally {
      clearTimeout(timeout);
    }
  }

  // src/background.ts
  var TOKEN_KEY = "pkws.extensionToken";
  async function setup() {
    await chrome.sidePanel.setPanelBehavior({ openPanelOnActionClick: true });
  }
  var setupPromise;
  function ensureSetup() {
    setupPromise ??= setup().catch((error) => {
      setupPromise = void 0;
      throw error;
    });
    return setupPromise;
  }
  chrome.runtime.onInstalled.addListener(() => void ensureSetup());
  chrome.runtime.onStartup.addListener(() => void ensureSetup());
  void ensureSetup().catch((error) => console.error("PKWS extension setup failed", error));
  async function getActiveTab() {
    return (await chrome.tabs.query({ active: true, currentWindow: true }))[0];
  }
  async function readPageState(tabId) {
    try {
      return await chrome.tabs.sendMessage(tabId, { type: "pkws:get-page-state" });
    } catch {
      return { state: { supported: false, reason: "\u65E0\u6CD5\u8BFB\u53D6\u5F53\u524D\u9875\u9762\uFF0C\u8BF7\u5237\u65B0\u5FAE\u4FE1\u8BFB\u4E66\u540E\u91CD\u8BD5" } };
    }
  }
  async function activeState() {
    const tab = await getActiveTab();
    const page = tab?.id === void 0 ? { state: { supported: false, reason: "\u6CA1\u6709\u6D3B\u52A8\u6807\u7B7E\u9875" } } : await readPageState(tab.id);
    const token = (await chrome.storage.local.get(TOKEN_KEY))[TOKEN_KEY];
    return { tabId: tab?.id, ...page, paired: Boolean(token), hostOnline: await getHealth() };
  }
  function validContext(draft) {
    if (!draft.boundContext || draft.boundContext.provider !== "weread") return false;
    try {
      const url = new URL(draft.boundContext.url);
      return url.protocol === "https:" && url.hostname === "weread.qq.com";
    } catch {
      return false;
    }
  }
  async function captureDraft(draft) {
    const token = (await chrome.storage.local.get(TOKEN_KEY))[TOKEN_KEY];
    if (!token) return { kind: "error", code: "NOT_PAIRED", message: "\u8BF7\u5148\u4E0E PKWS \u684C\u9762\u7AEF\u914D\u5BF9\u3002", retryable: false };
    if (!validContext(draft)) return { kind: "error", code: "SOURCE_INVALID", message: "\u8349\u7A3F\u6CA1\u6709\u6709\u6548\u7684\u5FAE\u4FE1\u8BFB\u4E66\u6765\u6E90\u3002", retryable: false };
    const source = {
      ...draft.boundContext,
      captured_at: (/* @__PURE__ */ new Date()).toISOString()
    };
    const request = {
      schema_version: 1,
      request_id: draft.requestId,
      source_type: "weread",
      content: draft.comment,
      source_context: source
    };
    return saveCapture(request, token);
  }
  async function handleMessage(message, sender) {
    switch (message.type) {
      case "pkws:page-state": {
        if (sender.tab?.id === void 0) return { ok: false, error: "\u7F3A\u5C11\u6807\u7B7E\u9875\u6765\u6E90" };
        await chrome.storage.session.set({ [`pkws.page.${sender.tab.id}`]: message.state });
        void chrome.runtime.sendMessage({ type: "pkws:state-changed", tabId: sender.tab.id }).catch(() => void 0);
        return { ok: true };
      }
      case "pkws:get-active-state":
        return { ok: true, data: await activeState() };
      case "pkws:pair": {
        const code = typeof message.code === "string" ? message.code.trim() : "";
        if (!/^\d{6}$/u.test(code)) return { ok: false, error: "\u8BF7\u8F93\u5165 6 \u4F4D\u914D\u5BF9\u7801" };
        try {
          const token = await pairExtension(code, chrome.runtime.id);
          await chrome.storage.local.set({ [TOKEN_KEY]: token });
          return { ok: true };
        } catch (error) {
          return { ok: false, error: error instanceof Error ? error.message : "\u914D\u5BF9\u5931\u8D25" };
        }
      }
      case "pkws:forget-pairing":
        await chrome.storage.local.remove(TOKEN_KEY);
        return { ok: true };
      case "pkws:capture":
        return { ok: true, data: await captureDraft(message.draft) };
      default:
        return { ok: false, error: "\u672A\u77E5\u6D88\u606F" };
    }
  }
  chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
    if (!message || typeof message !== "object") return;
    void handleMessage(message, sender).then(sendResponse).catch((error) => sendResponse({ ok: false, error: error instanceof Error ? error.message : "\u540E\u53F0\u5904\u7406\u5931\u8D25" }));
    return true;
  });
  chrome.tabs.onRemoved.addListener((tabId) => {
    void chrome.storage.session.remove(`pkws.page.${tabId}`);
  });
})();
