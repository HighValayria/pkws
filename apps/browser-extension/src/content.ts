import { extractWereadContext } from "./weread-adapter";
import type { PageState } from "./types";

let lastUrl = location.href;
let lastFingerprint = "";
let refreshTimer: number | undefined;

function currentState(): PageState {
  return extractWereadContext(document, location.href);
}

function scheduleRefresh(): void {
  window.clearTimeout(refreshTimer);
  refreshTimer = window.setTimeout(publishState, 250);
}

function publishState(): void {
  const state = currentState();
  const fingerprint = JSON.stringify(state.context
    ? { ...state, context: { ...state.context, captured_at: "" } }
    : state);
  if (fingerprint === lastFingerprint) return;
  lastFingerprint = fingerprint;
  void chrome.runtime.sendMessage({ type: "pkws:page-state", state }).catch(() => undefined);
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

chrome.runtime.onMessage.addListener((message: unknown, _sender, sendResponse) => {
  if (!message || typeof message !== "object") return;
  const type = (message as { type?: string }).type;
  if (type === "pkws:get-page-state") {
    sendResponse({ state: currentState() });
  }
});

publishState();
