import { css, html, LitElement } from "lit";
import { api, Unauthorized } from "../api.js";
import { onRunEvent } from "../bus.js";
import { eventLine, shortId } from "../format.js";
import type { RunEvent, TranscriptMessage } from "../types.js";

interface ChatItem {
  kind: "message" | "activity";
  role?: string;
  text: string;
}

/**
 * The chat pane: pick or start a conversational run, render its transcript, send
 * messages, and follow its live events (tool activity inline, replies as messages).
 */
export class ChatView extends LitElement {
  static override properties = {
    runId: { state: true },
    items: { state: true },
    chats: { state: true },
    busy: { state: true },
  };

  declare runId: string | null;
  declare items: ChatItem[];
  declare chats: { runId: string; status: string }[];
  declare busy: boolean;

  private unsubscribe?: () => void;

  constructor() {
    super();
    this.runId = null;
    this.items = [];
    this.chats = [];
    this.busy = false;
  }

  override connectedCallback(): void {
    super.connectedCallback();
    void this.loadChats();
    // Live events come from the shell's single stream, filtered to this chat.
    this.unsubscribe = onRunEvent((event) => {
      if (event.runId === this.runId) this.appendEvent(event);
    });
  }

  override disconnectedCallback(): void {
    super.disconnectedCallback();
    this.unsubscribe?.();
  }

  private async loadChats(): Promise<void> {
    try {
      const runs = await api.runs();
      this.chats = runs
        .filter((run) => run.status === "awaitingUser" || (run.active && run.status === "running"))
        .map((run) => ({ runId: run.runId, status: run.status }));
    } catch (error) {
      if (error instanceof Unauthorized) this.dispatchEvent(unauthorized());
    }
  }

  private async open(runId: string): Promise<void> {
    this.runId = runId;
    const transcript = (await api.transcript(runId)) ?? [];
    this.items = transcript
      .filter((message: TranscriptMessage) => message.role !== "system")
      .map((message) => ({ kind: "message" as const, role: message.role, text: message.text }));
    // Live events for this run now flow via the bus subscription.
  }

  private appendEvent(event: RunEvent): void {
    switch (event.$type) {
      case "turn_completed":
        this.items = [
          ...this.items,
          { kind: "message", role: "assistant", text: String(event["finalTextPreview"] ?? "") },
        ];
        this.busy = false;
        break;
      case "run_completed":
        this.items = [...this.items, { kind: "activity", text: eventLine(event) }];
        this.busy = false;
        break;
      case "tool_call_proposed":
      case "supervision_decided":
      case "tool_call_completed":
      case "run_paused":
        this.items = [...this.items, { kind: "activity", text: eventLine(event) }];
        break;
      default:
        break;
    }
  }

  private async send(event: Event): Promise<void> {
    event.preventDefault();
    const input = this.renderRoot.querySelector("input") as HTMLInputElement;
    const text = input.value.trim();
    if (!text) return;
    input.value = "";
    this.busy = true;
    try {
      if (this.runId === null) {
        const accepted = await api.startRun({ prompt: text, chat: true, supervisorKey: "yolo" });
        this.items = [{ kind: "message", role: "user", text }];
        this.runId = accepted.runId; // bus subscription now matches this run
        void this.loadChats();
      } else {
        this.items = [...this.items, { kind: "message", role: "user", text }];
        await api.continueRun(this.runId, text);
      }
    } catch (error) {
      this.busy = false;
      if (error instanceof Unauthorized) this.dispatchEvent(unauthorized());
      else this.items = [...this.items, { kind: "activity", text: String(error) }];
    }
  }

  override render() {
    return html`
      <div class="head">
        <h2>Chat</h2>
        <select @change=${(e: Event) => {
          const value = (e.target as HTMLSelectElement).value;
          if (value) void this.open(value);
        }}>
          <option value="">new chat…</option>
          ${this.chats.map(
            (chat) => html`
              <option value=${chat.runId} ?selected=${chat.runId === this.runId}>
                ${shortId(chat.runId)} (${chat.status})
              </option>
            `,
          )}
        </select>
      </div>
      <div class="messages">
        ${this.items.map((item) =>
          item.kind === "message"
            ? html`<div class="msg ${item.role}"><span class="role">${item.role}</span>${item.text}</div>`
            : html`<div class="activity">${item.text}</div>`,
        )}
        ${this.busy ? html`<div class="activity">…</div>` : ""}
      </div>
      <form @submit=${(e: Event) => void this.send(e)}>
        <input type="text" placeholder=${this.runId ? "reply…" : "start a new chat…"} autocomplete="off" />
        <button type="submit">send</button>
      </form>
    `;
  }

  static override styles = css`
    :host { display: block; }
    .head { display: flex; gap: 1rem; align-items: baseline; }
    .messages { min-height: 40vh; max-height: 60vh; overflow-y: auto; padding: 0.5rem 0; }
    .msg { margin: 0.5rem 0; padding: 0.6rem 0.8rem; border-radius: 0.6rem; white-space: pre-wrap; }
    .msg.user { background: var(--info-bg); margin-left: 15%; }
    .msg.assistant { background: var(--panel); margin-right: 15%; }
    .role { display: block; font-size: 0.75em; color: var(--dim); }
    .activity { font-family: monospace; font-size: 0.85em; color: var(--dim); padding: 0.1rem 0; }
    form { display: flex; gap: 0.5rem; }
    input { flex: 1; padding: 0.5rem; }
  `;
}

const unauthorized = () => new CustomEvent("orkis-unauthorized", { bubbles: true, composed: true });

customElements.define("chat-view", ChatView);
