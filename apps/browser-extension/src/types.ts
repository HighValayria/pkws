export interface SourceContext {
  provider: "weread";
  work_id?: string;
  work_title?: string;
  authors: string[];
  chapter_id?: string;
  chapter_title?: string;
  url: string;
  selected_text?: string;
  selection_prefix?: string;
  selection_suffix?: string;
  captured_at: string;
  extractor_version: string;
}

export interface PageState {
  supported: boolean;
  reason?: string;
  context?: SourceContext;
}

export interface DraftState {
  requestId: string;
  comment: string;
  boundContext?: SourceContext;
  updatedAt: string;
}

export interface CaptureRequest {
  schema_version: 1;
  request_id: string;
  source_type: "weread";
  content: string;
  source_context: SourceContext;
}

export interface CaptureResponse {
  request_id: string;
  entry_id: string;
  status: "committed";
  relative_path: string;
  duplicate: boolean;
}

export type SaveResult =
  | { kind: "committed"; response: CaptureResponse }
  | { kind: "uncertain"; message: string }
  | { kind: "error"; code: string; message: string; retryable: boolean };

export interface RuntimeReply<T = unknown> {
  ok: boolean;
  data?: T;
  error?: string;
}
