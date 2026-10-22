import type { CaptureRequest, CaptureResponse, SaveResult } from "./types";

const API_BASE = "http://127.0.0.1:27123/v1";

interface ApiErrorBody { code?: string; message?: string; retryable?: boolean }

async function responseError(response: Response): Promise<SaveResult> {
  let body: ApiErrorBody = {};
  try { body = await response.json() as ApiErrorBody; } catch { /* empty/non-JSON response */ }
  return {
    kind: "error",
    code: body.code ?? `HTTP_${response.status}`,
    message: body.message ?? `本地服务返回 ${response.status}`,
    retryable: body.retryable ?? response.status >= 500
  };
}

function headers(token?: string): HeadersInit {
  return {
    "Content-Type": "application/json",
    ...(token ? { Authorization: `Bearer ${token}` } : {})
  };
}

export async function pairExtension(code: string, extensionId: string): Promise<string> {
  const response = await fetch(`${API_BASE}/pairings`, {
    method: "POST",
    headers: headers(),
    body: JSON.stringify({ code, extension_id: extensionId })
  });
  if (!response.ok) {
    const failure = await responseError(response);
    throw new Error(failure.kind === "error" ? failure.message : "配对失败");
  }
  const body = await response.json() as { access_token: string };
  return body.access_token;
}

export async function getHealth(): Promise<boolean> {
  try {
    const response = await fetch(`${API_BASE}/health`, { cache: "no-store" });
    return response.ok;
  } catch {
    return false;
  }
}

export async function saveCapture(request: CaptureRequest, token: string, timeoutMs = 8000): Promise<SaveResult> {
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
    return { kind: "committed", response: await response.json() as CaptureResponse };
  } catch (error) {
    const status = await queryStatus(request.request_id, token, 3000);
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
      message: error instanceof DOMException && error.name === "AbortError"
        ? "保存响应超时，结果尚未确认。请保留草稿并用同一请求重试。"
        : "无法确认保存结果。请检查桌面端后用同一请求重试。"
    };
  } finally {
    clearTimeout(timeout);
  }
}

async function queryStatus(requestId: string, token: string, timeoutMs: number): Promise<{
  status: string; entry_id: string; relative_path?: string;
} | undefined> {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const response = await fetch(`${API_BASE}/captures/${encodeURIComponent(requestId)}`, {
      headers: headers(token), signal: controller.signal, cache: "no-store"
    });
    return response.ok ? await response.json() : undefined;
  } catch {
    return undefined;
  } finally {
    clearTimeout(timeout);
  }
}
