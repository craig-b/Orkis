// Hand-written mirrors of the daemon's wire records (camelCase JSON). The protocol
// is the contract; these stay small and honest rather than generated.

export type RunStatus =
  | "running"
  | "awaitingSupervision"
  | "completed"
  | "budgetExceeded"
  | "awaitingUser";

export interface RunResponse {
  runId: string;
  status: RunStatus;
  active: boolean;
  supervisorKey?: string | null;
  inputTokens: number;
  outputTokens: number;
  cost: number;
  toolCalls: number;
  updatedAt?: string | null;
  lastError?: string | null;
}

export interface ApprovalResponse {
  runId: string;
  callId: string;
  toolName: string;
  risk: string;
  requestedAt: string;
  arguments: unknown;
}

export interface CapabilitiesResponse {
  supervisors: string[];
  defaultSupervisor: string;
  models: string[];
  defaultModel?: string | null;
  sandbox: string;
  memory: boolean;
  corpusRetrieval: boolean;
  tools: string[];
  catalogTools: string[];
}

export interface TranscriptMessage {
  role: string;
  text: string;
}

export interface ArtifactInfo {
  name: string;
  length: number;
  createdAt: string;
}

// Run events arrive with a $type discriminator; unknown types must not break the
// stream (same forward-compat rule as the .NET client).
export interface RunEvent {
  $type: string;
  runId: string;
  sequence: number;
  timestamp: string;
  [key: string]: unknown;
}
