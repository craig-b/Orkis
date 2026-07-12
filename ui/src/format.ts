import { html, type TemplateResult } from "lit";
import type { RunEvent, RunStatus } from "./types.js";

export const shortId = (id: string): string => (id.length <= 8 ? id : id.slice(-8));

export const statusBadge = (status: RunStatus, active: boolean): TemplateResult => {
  const label = active ? "active" : status;
  return html`<span class="badge ${status}${active ? " active" : ""}">${label}</span>`;
};

export const time = (iso?: string | null): string =>
  iso ? new Date(iso).toLocaleTimeString() : "-";

const str = (value: unknown): string => (typeof value === "string" ? value : "");
const collapse = (text: string): string => text.replaceAll(/\s+/g, " ").trim();
const clip = (text: string, max: number): string =>
  text.length <= max ? text : `${text.slice(0, max)}…`;

/** One human line per event — the browser twin of the CLI's EventRenderer. */
export function eventLine(e: RunEvent): string {
  switch (e.$type) {
    case "run_started":
      return `run started (supervision: ${str(e["supervisorKey"])})`;
    case "run_resumed":
      return "run resumed";
    case "run_continued":
      return `user: ${clip(collapse(str(e["message"])), 100)}`;
    case "model_call_completed":
      return `model ${str(e["modelId"]) || "?"}: ${e["inputTokens"]} in / ${e["outputTokens"]} out tokens`;
    case "tool_call_proposed":
      return `→ ${str(e["toolName"])} ${clip(collapse(str(e["argumentsJson"])), 80)}`;
    case "supervision_decided":
      return `supervision: ${str(e["toolName"])} ${str(e["verdict"]).toLowerCase()}`;
    case "tool_call_completed":
      return `${e["isError"] ? "✗" : "✓"} ${str(e["toolName"])} ${clip(collapse(str(e["contentPreview"])), 100)}`;
    case "run_paused":
      return "run paused awaiting supervision";
    case "turn_completed":
      return `turn completed — ${clip(collapse(str(e["finalTextPreview"])), 100)}`;
    case "run_completed":
      return `run ended: ${str(e["status"])} — ${clip(collapse(str(e["finalTextPreview"])), 100)}`;
    default:
      return `unknown event '${e.$type}' (newer daemon?)`;
  }
}
