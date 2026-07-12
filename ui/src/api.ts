// The gateway is same-origin, so this is plain fetch — with one wrinkle: a 401 means
// "log in", handled by exchanging the token for a cookie session at /auth/session.

import type {
  AddMcpServerRequest,
  ApprovalResponse,
  ArtifactInfo,
  CapabilitiesResponse,
  CreateScheduleRequest,
  McpServerResponse,
  RunResponse,
  ScheduleResponse,
  TranscriptMessage,
  UpdateScheduleRequest,
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
  artifacts: () => request<ArtifactInfo[]>("/v1/artifacts"),
  schedules: () => request<ScheduleResponse[]>("/v1/schedules"),
  createSchedule: (body: CreateScheduleRequest) => request<ScheduleResponse>("/v1/schedules", json(body)),
  updateSchedule: (id: string, body: UpdateScheduleRequest) =>
    request<ScheduleResponse>(`/v1/schedules/${encodeURIComponent(id)}`, {
      method: "PATCH",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(body),
    }),
  deleteSchedule: (id: string) =>
    request<void>(`/v1/schedules/${encodeURIComponent(id)}`, { method: "DELETE" }),
  mcpServers: () => request<McpServerResponse[]>("/v1/mcp-servers"),
  addMcpServer: (body: AddMcpServerRequest) => request<McpServerResponse>("/v1/mcp-servers", json(body)),
  removeMcpServer: (name: string) =>
    request<void>(`/v1/mcp-servers/${encodeURIComponent(name)}`, { method: "DELETE" }),
};

/** Same-origin download URL for an artifact (the session cookie authenticates it). */
export function artifactUrl(name: string): string {
  return `/v1/artifacts/${encodeURIComponent(name)}`;
}

// The live event stream is opened once, in the app shell, with the browser's native
// EventSource (see main.ts), and fanned out in-page via bus.ts — a single long-lived
// connection for the whole UI. No streaming helper lives here.
