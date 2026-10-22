import { bindWhenNeeded, contextFingerprint, createDraft, hasDraftContent, hasSourceDrift, rebindDraft } from "./draft";
import type { DraftState, PageState, RuntimeReply, SaveResult, SourceContext } from "./types";

interface ActiveState {
  tabId?: number;
  state: PageState;
  paired: boolean;
  hostOnline: boolean;
}

const element = <T extends HTMLElement>(id: string): T => {
  const found = document.getElementById(id);
  if (!found) throw new Error(`Missing element: ${id}`);
  return found as T;
};

const sourceCard = element<HTMLElement>("sourceCard");
const sourceToggle = element<HTMLButtonElement>("sourceToggle");
const sourceSummary = element<HTMLElement>("sourceSummary");
const sourceDetails = element<HTMLElement>("sourceDetails");
const sourceTitle = element<HTMLElement>("sourceTitle");
const sourceMeta = element<HTMLElement>("sourceMeta");
const sourceLink = element<HTMLAnchorElement>("sourceLink");
const driftWarning = element<HTMLElement>("driftWarning");
const commentInput = element<HTMLTextAreaElement>("commentInput");
const statusText = element<HTMLElement>("statusText");
const saveButton = element<HTMLButtonElement>("saveButton");
const settingsPanel = element<HTMLElement>("settingsPanel");
const connectionDot = element<HTMLElement>("connectionDot");
const connectionText = element<HTMLElement>("connectionText");
const pairingForm = element<HTMLElement>("pairingForm");
const pairingCodeInput = element<HTMLInputElement>("pairingCodeInput");
const forgetButton = element<HTMLButtonElement>("forgetButton");

let active: ActiveState = { state: { supported: false }, paired: false, hostOnline: false };
let draft = createDraft();
let saving = false;
let lastSourceDraftKey: string | undefined;
let sourceCollapsed = false;

const sourceCollapsedKey = "pkws.ui.sourceCollapsed";

async function send<T>(message: Record<string, unknown>): Promise<RuntimeReply<T>> {
  return chrome.runtime.sendMessage(message) as Promise<RuntimeReply<T>>;
}

function draftKey(tabId = active.tabId): string | undefined {
  return tabId === undefined ? undefined : `pkws.draft.${tabId}`;
}

function sourceDraftKey(context?: SourceContext): string | undefined {
  const fingerprint = contextFingerprint(context);
  return fingerprint ? `pkws.sourceDraft.${encodeURIComponent(fingerprint)}` : undefined;
}

async function persistDraft(): Promise<void> {
  const key = draftKey();
  if (!key) return;
  const nextSourceKey = sourceDraftKey(draft.boundContext);
  if (!hasDraftContent(draft)) {
    await chrome.storage.local.remove([key, ...(lastSourceDraftKey ? [lastSourceDraftKey] : [])]);
    lastSourceDraftKey = undefined;
    return;
  }

  await chrome.storage.local.set({
    [key]: draft,
    ...(nextSourceKey ? { [nextSourceKey]: draft } : {})
  });
  if (lastSourceDraftKey && lastSourceDraftKey !== nextSourceKey) {
    await chrome.storage.local.remove(lastSourceDraftKey);
  }
  lastSourceDraftKey = nextSourceKey;
}

async function loadDraft(tabId?: number): Promise<void> {
  const key = draftKey(tabId);
  if (!key) {
    draft = createDraft();
    return;
  }
  const fallbackKey = sourceDraftKey(active.state.context);
  const values = await chrome.storage.local.get([key, ...(fallbackKey ? [fallbackKey] : [])]);
  const stored = (values[key] ?? (fallbackKey ? values[fallbackKey] : undefined)) as DraftState | undefined;
  draft = stored && typeof stored.requestId === "string" && typeof stored.comment === "string"
    ? { requestId: stored.requestId, comment: stored.comment, boundContext: stored.boundContext, updatedAt: stored.updatedAt }
    : createDraft();
  lastSourceDraftKey = sourceDraftKey(draft.boundContext);
}

function setStatus(message: string, kind: "normal" | "error" | "success" = "normal"): void {
  statusText.textContent = message;
  statusText.className = `status${kind === "normal" ? "" : ` ${kind}`}`;
}

function renderSourceCollapse(): void {
  sourceCard.classList.toggle("collapsed", sourceCollapsed);
  sourceDetails.hidden = sourceCollapsed;
  sourceSummary.hidden = !sourceCollapsed;
  sourceToggle.setAttribute("aria-expanded", String(!sourceCollapsed));
  sourceToggle.setAttribute("aria-label", sourceCollapsed ? "展开当前来源" : "收起当前来源");
  sourceToggle.title = sourceCollapsed ? "展开当前来源" : "收起当前来源";
}

function render(): void {
  const context = active.state.context;
  const displayTitle = active.state.supported
    ? context?.work_title || "微信读书（书名暂未识别）"
    : active.state.reason || "请打开微信读书阅读页";
  sourceCard.classList.toggle("unsupported", !active.state.supported);
  sourceTitle.textContent = displayTitle;
  sourceSummary.textContent = displayTitle;
  sourceMeta.textContent = active.state.supported
    ? [context?.chapter_title, context?.authors.join("、")].filter(Boolean).join(" · ") || "来源已识别"
    : "插件不会在其他网站读取页面内容";
  sourceLink.hidden = !active.state.supported || !context?.url;
  sourceLink.href = active.state.supported && context?.url ? context.url : "#";
  renderSourceCollapse();

  const drifted = hasSourceDrift(draft, active.state);
  driftWarning.hidden = !drifted;
  if (commentInput.value !== draft.comment) commentInput.value = draft.comment;

  connectionDot.className = `dot ${active.hostOnline && active.paired ? "online" : active.hostOnline ? "unpaired" : ""}`;
  connectionText.textContent = !active.hostOnline
    ? "未发现 PKWS 桌面端。请先启动桌面应用。"
    : active.paired
      ? "已连接本机 PKWS，保存内容只发送到 127.0.0.1。"
      : "桌面端已启动，但插件尚未配对。";
  pairingForm.hidden = active.paired;
  forgetButton.hidden = !active.paired;
  saveButton.disabled = saving || !active.paired || !draft.boundContext || !hasDraftContent(draft);
}

async function refresh(): Promise<void> {
  const reply = await send<ActiveState>({ type: "pkws:get-active-state" });
  if (!reply.ok || !reply.data) {
    setStatus(reply.error || "无法读取页面状态", "error");
    return;
  }
  const previousTab = active.tabId;
  active = reply.data;
  if (active.tabId !== previousTab) await loadDraft(active.tabId);
  render();
}

commentInput.addEventListener("input", () => {
  draft = { ...draft, comment: commentInput.value, updatedAt: new Date().toISOString() };
  draft = hasDraftContent(draft) ? bindWhenNeeded(draft, active.state) : createDraft();
  void persistDraft();
  render();
});

element("rebindButton").addEventListener("click", () => void (async () => {
  draft = rebindDraft(draft, active.state);
  await persistDraft();
  setStatus("草稿已改绑到当前来源。");
  render();
})());

saveButton.addEventListener("click", () => void (async () => {
  if (saving || !hasDraftContent(draft)) return;
  if (!draft.boundContext) draft = bindWhenNeeded(draft, active.state);
  saving = true;
  setStatus("正在安全保存…");
  render();
  const reply = await send<SaveResult>({ type: "pkws:capture", draft });
  saving = false;
  if (!reply.ok || !reply.data) {
    setStatus(reply.error || "保存失败", "error");
    render();
    return;
  }
  if (reply.data.kind === "committed") {
    draft = createDraft();
    setStatus(reply.data.response.duplicate ? "已确认保存，没有产生重复记录。" : "已保存到今天。", "success");
  } else if (reply.data.kind === "uncertain") {
    setStatus(reply.data.message, "error");
  } else {
    setStatus(reply.data.message, "error");
    if (reply.data.code === "AUTH_FAILED" || reply.data.code === "NOT_PAIRED") active.paired = false;
  }
  await persistDraft();
  render();
})());

element("settingsButton").addEventListener("click", () => { settingsPanel.hidden = false; });
element("closeSettingsButton").addEventListener("click", () => { settingsPanel.hidden = true; });
sourceToggle.addEventListener("click", () => void (async () => {
  sourceCollapsed = !sourceCollapsed;
  renderSourceCollapse();
  await chrome.storage.local.set({ [sourceCollapsedKey]: sourceCollapsed });
})());

element("pairButton").addEventListener("click", () => void (async () => {
  const reply = await send({ type: "pkws:pair", code: pairingCodeInput.value });
  if (!reply.ok) {
    setStatus(reply.error || "配对失败", "error");
    return;
  }
  pairingCodeInput.value = "";
  settingsPanel.hidden = true;
  setStatus("桌面端配对成功。", "success");
  await refresh();
})());

forgetButton.addEventListener("click", () => void (async () => {
  await send({ type: "pkws:forget-pairing" });
  active.paired = false;
  setStatus("已解除插件中的本机配对。注：桌面端保存的旧凭证记录会在下次配对时替换。");
  render();
})());

chrome.runtime.onMessage.addListener((message: unknown) => {
  const type = message && typeof message === "object" ? (message as { type?: string }).type : undefined;
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
