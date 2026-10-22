"use strict";
(() => {
  // src/draft.ts
  function createDraft(requestId = crypto.randomUUID(), now = /* @__PURE__ */ new Date()) {
    return { requestId, comment: "", updatedAt: now.toISOString() };
  }
  function contextFingerprint(context) {
    if (!context) return "";
    const parsed = new URL(context.url);
    return [context.provider, context.work_id ?? parsed.pathname, context.chapter_id ?? context.chapter_title ?? ""].join("|");
  }
  function hasDraftContent(draft2) {
    return draft2.comment.trim().length > 0;
  }
  function bindWhenNeeded(draft2, page) {
    if (draft2.boundContext || !hasDraftContent(draft2) || !page.supported || !page.context) return draft2;
    return { ...draft2, boundContext: page.context, updatedAt: (/* @__PURE__ */ new Date()).toISOString() };
  }
  function hasSourceDrift(draft2, page) {
    return hasDraftContent(draft2) && Boolean(draft2.boundContext) && contextFingerprint(draft2.boundContext) !== contextFingerprint(page.context);
  }
  function rebindDraft(draft2, page, requestId = crypto.randomUUID()) {
    if (!page.supported || !page.context) return draft2;
    return { ...draft2, requestId, boundContext: page.context, updatedAt: (/* @__PURE__ */ new Date()).toISOString() };
  }

  // src/sidepanel.ts
  var element = (id) => {
    const found = document.getElementById(id);
    if (!found) throw new Error(`Missing element: ${id}`);
    return found;
  };
  var sourceCard = element("sourceCard");
  var sourceToggle = element("sourceToggle");
  var sourceSummary = element("sourceSummary");
  var sourceDetails = element("sourceDetails");
  var sourceTitle = element("sourceTitle");
  var sourceMeta = element("sourceMeta");
  var sourceLink = element("sourceLink");
  var driftWarning = element("driftWarning");
  var commentInput = element("commentInput");
  var statusText = element("statusText");
  var saveButton = element("saveButton");
  var settingsPanel = element("settingsPanel");
  var connectionDot = element("connectionDot");
  var connectionText = element("connectionText");
  var pairingForm = element("pairingForm");
  var pairingCodeInput = element("pairingCodeInput");
  var forgetButton = element("forgetButton");
  var active = { state: { supported: false }, paired: false, hostOnline: false };
  var draft = createDraft();
  var saving = false;
  var lastSourceDraftKey;
  var sourceCollapsed = false;
  var sourceCollapsedKey = "pkws.ui.sourceCollapsed";
  async function send(message) {
    return chrome.runtime.sendMessage(message);
  }
  function draftKey(tabId = active.tabId) {
    return tabId === void 0 ? void 0 : `pkws.draft.${tabId}`;
  }
  function sourceDraftKey(context) {
    const fingerprint = contextFingerprint(context);
    return fingerprint ? `pkws.sourceDraft.${encodeURIComponent(fingerprint)}` : void 0;
  }
  async function persistDraft() {
    const key = draftKey();
    if (!key) return;
    const nextSourceKey = sourceDraftKey(draft.boundContext);
    if (!hasDraftContent(draft)) {
      await chrome.storage.local.remove([key, ...lastSourceDraftKey ? [lastSourceDraftKey] : []]);
      lastSourceDraftKey = void 0;
      return;
    }
    await chrome.storage.local.set({
      [key]: draft,
      ...nextSourceKey ? { [nextSourceKey]: draft } : {}
    });
    if (lastSourceDraftKey && lastSourceDraftKey !== nextSourceKey) {
      await chrome.storage.local.remove(lastSourceDraftKey);
    }
    lastSourceDraftKey = nextSourceKey;
  }
  async function loadDraft(tabId) {
    const key = draftKey(tabId);
    if (!key) {
      draft = createDraft();
      return;
    }
    const fallbackKey = sourceDraftKey(active.state.context);
    const values = await chrome.storage.local.get([key, ...fallbackKey ? [fallbackKey] : []]);
    const stored = values[key] ?? (fallbackKey ? values[fallbackKey] : void 0);
    draft = stored && typeof stored.requestId === "string" && typeof stored.comment === "string" ? { requestId: stored.requestId, comment: stored.comment, boundContext: stored.boundContext, updatedAt: stored.updatedAt } : createDraft();
    lastSourceDraftKey = sourceDraftKey(draft.boundContext);
  }
  function setStatus(message, kind = "normal") {
    statusText.textContent = message;
    statusText.className = `status${kind === "normal" ? "" : ` ${kind}`}`;
  }
  function renderSourceCollapse() {
    sourceCard.classList.toggle("collapsed", sourceCollapsed);
    sourceDetails.hidden = sourceCollapsed;
    sourceSummary.hidden = !sourceCollapsed;
    sourceToggle.setAttribute("aria-expanded", String(!sourceCollapsed));
    sourceToggle.setAttribute("aria-label", sourceCollapsed ? "\u5C55\u5F00\u5F53\u524D\u6765\u6E90" : "\u6536\u8D77\u5F53\u524D\u6765\u6E90");
    sourceToggle.title = sourceCollapsed ? "\u5C55\u5F00\u5F53\u524D\u6765\u6E90" : "\u6536\u8D77\u5F53\u524D\u6765\u6E90";
  }
  function render() {
    const context = active.state.context;
    const displayTitle = active.state.supported ? context?.work_title || "\u5FAE\u4FE1\u8BFB\u4E66\uFF08\u4E66\u540D\u6682\u672A\u8BC6\u522B\uFF09" : active.state.reason || "\u8BF7\u6253\u5F00\u5FAE\u4FE1\u8BFB\u4E66\u9605\u8BFB\u9875";
    sourceCard.classList.toggle("unsupported", !active.state.supported);
    sourceTitle.textContent = displayTitle;
    sourceSummary.textContent = displayTitle;
    sourceMeta.textContent = active.state.supported ? [context?.chapter_title, context?.authors.join("\u3001")].filter(Boolean).join(" \xB7 ") || "\u6765\u6E90\u5DF2\u8BC6\u522B" : "\u63D2\u4EF6\u4E0D\u4F1A\u5728\u5176\u4ED6\u7F51\u7AD9\u8BFB\u53D6\u9875\u9762\u5185\u5BB9";
    sourceLink.hidden = !active.state.supported || !context?.url;
    sourceLink.href = active.state.supported && context?.url ? context.url : "#";
    renderSourceCollapse();
    const drifted = hasSourceDrift(draft, active.state);
    driftWarning.hidden = !drifted;
    if (commentInput.value !== draft.comment) commentInput.value = draft.comment;
    connectionDot.className = `dot ${active.hostOnline && active.paired ? "online" : active.hostOnline ? "unpaired" : ""}`;
    connectionText.textContent = !active.hostOnline ? "\u672A\u53D1\u73B0 PKWS \u684C\u9762\u7AEF\u3002\u8BF7\u5148\u542F\u52A8\u684C\u9762\u5E94\u7528\u3002" : active.paired ? "\u5DF2\u8FDE\u63A5\u672C\u673A PKWS\uFF0C\u4FDD\u5B58\u5185\u5BB9\u53EA\u53D1\u9001\u5230 127.0.0.1\u3002" : "\u684C\u9762\u7AEF\u5DF2\u542F\u52A8\uFF0C\u4F46\u63D2\u4EF6\u5C1A\u672A\u914D\u5BF9\u3002";
    pairingForm.hidden = active.paired;
    forgetButton.hidden = !active.paired;
    saveButton.disabled = saving || !active.paired || !draft.boundContext || !hasDraftContent(draft);
  }
  async function refresh() {
    const reply = await send({ type: "pkws:get-active-state" });
    if (!reply.ok || !reply.data) {
      setStatus(reply.error || "\u65E0\u6CD5\u8BFB\u53D6\u9875\u9762\u72B6\u6001", "error");
      return;
    }
    const previousTab = active.tabId;
    active = reply.data;
    if (active.tabId !== previousTab) await loadDraft(active.tabId);
    render();
  }
  commentInput.addEventListener("input", () => {
    draft = { ...draft, comment: commentInput.value, updatedAt: (/* @__PURE__ */ new Date()).toISOString() };
    draft = hasDraftContent(draft) ? bindWhenNeeded(draft, active.state) : createDraft();
    void persistDraft();
    render();
  });
  element("rebindButton").addEventListener("click", () => void (async () => {
    draft = rebindDraft(draft, active.state);
    await persistDraft();
    setStatus("\u8349\u7A3F\u5DF2\u6539\u7ED1\u5230\u5F53\u524D\u6765\u6E90\u3002");
    render();
  })());
  saveButton.addEventListener("click", () => void (async () => {
    if (saving || !hasDraftContent(draft)) return;
    if (!draft.boundContext) draft = bindWhenNeeded(draft, active.state);
    saving = true;
    setStatus("\u6B63\u5728\u5B89\u5168\u4FDD\u5B58\u2026");
    render();
    const reply = await send({ type: "pkws:capture", draft });
    saving = false;
    if (!reply.ok || !reply.data) {
      setStatus(reply.error || "\u4FDD\u5B58\u5931\u8D25", "error");
      render();
      return;
    }
    if (reply.data.kind === "committed") {
      draft = createDraft();
      setStatus(reply.data.response.duplicate ? "\u5DF2\u786E\u8BA4\u4FDD\u5B58\uFF0C\u6CA1\u6709\u4EA7\u751F\u91CD\u590D\u8BB0\u5F55\u3002" : "\u5DF2\u4FDD\u5B58\u5230\u4ECA\u5929\u3002", "success");
    } else if (reply.data.kind === "uncertain") {
      setStatus(reply.data.message, "error");
    } else {
      setStatus(reply.data.message, "error");
      if (reply.data.code === "AUTH_FAILED" || reply.data.code === "NOT_PAIRED") active.paired = false;
    }
    await persistDraft();
    render();
  })());
  element("settingsButton").addEventListener("click", () => {
    settingsPanel.hidden = false;
  });
  element("closeSettingsButton").addEventListener("click", () => {
    settingsPanel.hidden = true;
  });
  sourceToggle.addEventListener("click", () => void (async () => {
    sourceCollapsed = !sourceCollapsed;
    renderSourceCollapse();
    await chrome.storage.local.set({ [sourceCollapsedKey]: sourceCollapsed });
  })());
  element("pairButton").addEventListener("click", () => void (async () => {
    const reply = await send({ type: "pkws:pair", code: pairingCodeInput.value });
    if (!reply.ok) {
      setStatus(reply.error || "\u914D\u5BF9\u5931\u8D25", "error");
      return;
    }
    pairingCodeInput.value = "";
    settingsPanel.hidden = true;
    setStatus("\u684C\u9762\u7AEF\u914D\u5BF9\u6210\u529F\u3002", "success");
    await refresh();
  })());
  forgetButton.addEventListener("click", () => void (async () => {
    await send({ type: "pkws:forget-pairing" });
    active.paired = false;
    setStatus("\u5DF2\u89E3\u9664\u63D2\u4EF6\u4E2D\u7684\u672C\u673A\u914D\u5BF9\u3002\u6CE8\uFF1A\u684C\u9762\u7AEF\u4FDD\u5B58\u7684\u65E7\u51ED\u8BC1\u8BB0\u5F55\u4F1A\u5728\u4E0B\u6B21\u914D\u5BF9\u65F6\u66FF\u6362\u3002");
    render();
  })());
  chrome.runtime.onMessage.addListener((message) => {
    const type = message && typeof message === "object" ? message.type : void 0;
    if (type === "pkws:state-changed") void refresh();
  });
  chrome.tabs.onActivated.addListener(() => void refresh());
  chrome.tabs.onUpdated.addListener((_tabId, changeInfo) => {
    if (changeInfo.url || changeInfo.status === "complete") void refresh();
  });
  void (async () => {
    const storedUi = await chrome.storage.local.get(sourceCollapsedKey);
    sourceCollapsed = storedUi[sourceCollapsedKey] === true;
    renderSourceCollapse();
    await refresh();
  })();
})();
