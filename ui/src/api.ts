// The gateway is same-origin, so this is plain fetch — with one wrinkle: a 401 means
// "log in", handled by exchanging the token for a cookie session at /auth/session.

import type {
  ApprovalResponse,
  CapabilitiesResponse,
  RunEvent,
  RunResponse,
  TranscriptMessage,
} from "./types.js";

/** Raised on 401 so views can hand off to the login overlay. */
export class Unauthorized extends Error {
  constructor() {
    super("unauthorized");
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, init);
  if (response.status === 401) throw new Unauthorized();
  if (!response.ok) {
    let message = `${response.status}`;
    try {
      const body = (await response.json()) as { error?: string };
      if (body.error) message = body.error;
    } catch {
      // Non-JSON error body; the status is the message.
    }
    throw new Error(message);
  }
  if (response.status === 204) return undefined as T;
  return (await response.json()) as T;
}

const json = (body: unknown): RequestInit => ({
  method: "POST",
  headers: { "content-type": "application/json" },
  body: JSON.stringify(body),
});

export const api = {
  login: (token: string) => request<void>("/auth/session", json({ token })),
  capabilities: () => request<CapabilitiesResponse>("/v1/capabilities"),
  runs: () => request<RunResponse[]>("/v1/runs"),
  run: (id: string) => request<RunResponse>(`/v1/runs/${encodeURIComponent(id)}`),
  startRun: (body: {
    prompt: string;
    chat?: boolean;
    supervisorKey?: string;
    model?: string;
  }) => request<{ runId: string }>("/v1/runs", json(body)),
  resume: (id: string) =>
    request<{ runId: string }>(`/v1/runs/${encodeURIComponent(id)}/resume`, { method: "POST" }),
  continueRun: (id: string, message: string) =>
    request<{ runId: string }>(`/v1/runs/${encodeURIComponent(id)}/messages`, json({ message })),
  transcript: (id: string) =>
    request<TranscriptMessage[]>(`/v1/runs/${encodeURIComponent(id)}/transcript`),
  approvals: (runId?: string) =>
    request<ApprovalResponse[]>(runId ? `/v1/approvals?runId=${encodeURIComponent(runId)}` : "/v1/approvals"),
  decide: (
    runId: string,
    callId: string,
    body: { verdict: string; sandboxLevel?: string; network?: string; reason?: string },
  ) =>
    request<void>(
      `/v1/approvals/${encodeURIComponent(runId)}/${encodeURIComponent(callId)}`,
      json(body),
    ),
};

/**
 * Reads an SSE stream of run events via fetch (EventSource cannot send credentials
 * we may later need, and this parser matches the daemon's one-line-per-event
 * format). Yields until the server closes or the signal aborts.
 */
export async function* runEvents(path: string, signal: AbortSignal): AsyncGenerator<RunEvent> {
  const response = await fetch(path, { signal, headers: { accept: "text/event-stream" } });
  if (response.status === 401) throw new Unauthorized();
  if (!response.ok || response.body === null) throw new Error(`${response.status}`);

  const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
  let buffered = "";
  try {
    for (;;) {
      const { done, value } = await reader.read();
      if (done) return;
      buffered += value;
      let newline;
      while ((newline = buffered.indexOf("\n")) >= 0) {
        const line = buffered.slice(0, newline);
        buffered = buffered.slice(newline + 1);
        if (line.startsWith("data: ")) {
          yield JSON.parse(line.slice("data: ".length)) as RunEvent;
        }
      }
    }
  } finally {
    reader.releaseLock();
  }
}
