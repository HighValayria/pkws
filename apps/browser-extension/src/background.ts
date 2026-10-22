import { getHealth, pairExtension, saveCapture } from "./api-client";
import type { CaptureRequest, DraftState, PageState, RuntimeReply, SaveResult } from "./types";

const TOKEN_KEY = "pkws.extensionToken";

async function setup(): Promise<void> {
  await chrome.sidePanel.setPanelBehavior({ openPanelOnActionClick: true });
}

let setupPromise: Promise<void> | undefined;

function ensureSetup(): Promise<void> {
  setupPromise ??= setup().catch((error: unknown) => {
    setupPromise = undefined;
    throw error;
  });
  return setupPromise;
}

chrome.runtime.onInstalled.addListener(() => void ensureSetup());
chrome.runtime.onStartup.addListener(() => void ensureSetup());
void ensureSetup().catch((error: unknown) => console.error("PKWS extension setup failed", error));

async function getActiveTab(): Promise<chrome.tabs.Tab | undefined> {
  return (await chrome.tabs.query({ active: true, currentWindow: true }))[0];
}

async function readPageState(tabId: number): Promise<{ state: PageState }> {
  try {
    return await chrome.tabs.sendMessage(tabId, { type: "pkws:get-page-state" }) as { state: PageState };
  } catch {
    return { state: { supported: false, reason: "无法读取当前页面，请刷新微信读书后重试" } };
  }
}

async function activeState(): Promise<{ tabId?: number; state: PageState; paired: boolean; hostOnline: boolean }> {
  const tab = await getActiveTab();
  const page = tab?.id === undefined
    ? { state: { supported: false, reason: "没有活动标签页" } as PageState }
    : await readPageState(tab.id);
  const token = (await chrome.storage.local.get(TOKEN_KEY))[TOKEN_KEY] as string | undefined;
  return { tabId: tab?.id, ...page, paired: Boolean(token), hostOnline: await getHealth() };
}

function validContext(draft: DraftState): boolean {
  if (!draft.boundContext || draft.boundContext.provider !== "weread") return false;
  try {
    const url = new URL(draft.boundContext.url);
    return url.protocol === "https:" && url.hostname === "weread.qq.com";
  } catch {
    return false;
  }
}

async function captureDraft(draft: DraftState): Promise<SaveResult> {
  const token = (await chrome.storage.local.get(TOKEN_KEY))[TOKEN_KEY] as string | undefined;
  if (!token) return { kind: "error", code: "NOT_PAIRED", message: "请先与 PKWS 桌面端配对。", retryable: false };
  if (!validContext(draft)) return { kind: "error", code: "SOURCE_INVALID", message: "草稿没有有效的微信读书来源。", retryable: false };

  const source = {
    ...draft.boundContext!,
    captured_at: new Date().toISOString()
  };
  const request: CaptureRequest = {
    schema_version: 1,
    request_id: draft.requestId,
    source_type: "weread",
    content: draft.comment,
    source_context: source
  };
  return saveCapture(request, token);
}

async function handleMessage(message: Record<string, unknown>, sender: chrome.runtime.MessageSender): Promise<RuntimeReply> {
  switch (message.type) {
    case "pkws:page-state": {
      if (sender.tab?.id === undefined) return { ok: false, error: "缺少标签页来源" };
      await chrome.storage.session.set({ [`pkws.page.${sender.tab.id}`]: message.state });
      void chrome.runtime.sendMessage({ type: "pkws:state-changed", tabId: sender.tab.id }).catch(() => undefined);
      return { ok: true };
    }
    case "pkws:get-active-state":
      return { ok: true, data: await activeState() };
    case "pkws:pair": {
      const code = typeof message.code === "string" ? message.code.trim() : "";
      if (!/^\d{6}$/u.test(code)) return { ok: false, error: "请输入 6 位配对码" };
      try {
        const token = await pairExtension(code, chrome.runtime.id);
        await chrome.storage.local.set({ [TOKEN_KEY]: token });
        return { ok: true };
      } catch (error) {
        return { ok: false, error: error instanceof Error ? error.message : "配对失败" };
      }
    }
    case "pkws:forget-pairing":
      await chrome.storage.local.remove(TOKEN_KEY);
      return { ok: true };
    case "pkws:capture":
      return { ok: true, data: await captureDraft(message.draft as DraftState) };
    default:
      return { ok: false, error: "未知消息" };
  }
}

chrome.runtime.onMessage.addListener((message: unknown, sender, sendResponse) => {
  if (!message || typeof message !== "object") return;
  void handleMessage(message as Record<string, unknown>, sender)
    .then(sendResponse)
    .catch((error: unknown) => sendResponse({ ok: false, error: error instanceof Error ? error.message : "后台处理失败" }));
  return true;
});

chrome.tabs.onRemoved.addListener((tabId) => {
  void chrome.storage.session.remove(`pkws.page.${tabId}`);
});
