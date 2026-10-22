import type { DraftState, PageState, SourceContext } from "./types";

export function createDraft(requestId: string = crypto.randomUUID(), now = new Date()): DraftState {
  return { requestId, comment: "", updatedAt: now.toISOString() };
}

export function contextFingerprint(context?: SourceContext): string {
  if (!context) return "";
  const parsed = new URL(context.url);
  return [context.provider, context.work_id ?? parsed.pathname, context.chapter_id ?? context.chapter_title ?? ""].join("|");
}

export function hasDraftContent(draft: DraftState): boolean {
  return draft.comment.trim().length > 0;
}

export function bindWhenNeeded(draft: DraftState, page: PageState): DraftState {
  if (draft.boundContext || !hasDraftContent(draft) || !page.supported || !page.context) return draft;
  return { ...draft, boundContext: page.context, updatedAt: new Date().toISOString() };
}

export function hasSourceDrift(draft: DraftState, page: PageState): boolean {
  return hasDraftContent(draft) && Boolean(draft.boundContext) &&
    contextFingerprint(draft.boundContext) !== contextFingerprint(page.context);
}

export function rebindDraft(draft: DraftState, page: PageState, requestId: string = crypto.randomUUID()): DraftState {
  if (!page.supported || !page.context) return draft;
  return { ...draft, requestId, boundContext: page.context, updatedAt: new Date().toISOString() };
}
