import { css, html, LitElement } from "lit";
import { api, Unauthorized } from "../api.js";
import { onConnection, onRunEvent } from "../bus.js";
import { eventLine, shortId, statusBadge, time } from "../format.js";
import type { RunEvent, RunResponse } from "../types.js";

/**
 * Runs table plus a rolling cross-run event feed, driven by the daemon-wide stream
 * on top of snapshots — the dashboard pattern shared with the TUI.
 */
export class RunsView extends LitElement {
  static override properties = {
    runs: { state: true },
    feed: { state: true },
    connected: { state: true },
  };

  declare runs: RunResponse[];
  declare feed: RunEvent[];
  declare connected: boolean;

  private refreshTimer?: number;
  private unsubscribe: Array<() => void> = [];

  constructor() {
    super();
    this.runs = [];
    this.feed = [];
    this.connected = false;
  }

  override connectedCallback(): void {
    super.connectedCallback();
    void this.refresh();
    // Events come from the shell's single subscription, not a second connection.
    this.unsubscribe = [
      onRunEvent((event) => {
        this.feed = [...this.feed.slice(-19), event];
        void this.refresh();
      }),
      onConnection((connected) => (this.connected = connected)),
    ];
    this.refreshTimer = window.setInterval(() => void this.refresh(), 5000);
  }

  override disconnectedCallback(): void {
    super.disconnectedCallback();
    for (const off of this.unsubscribe) off();
    window.clearInterval(this.refreshTimer);
  }

  private async refresh(): Promise<void> {
    try {
      this.runs = await api.runs();
    } catch (error) {
      if (error instanceof Unauthorized) this.dispatchEvent(unauthorized());
    }
  }

  override render() {
    return html`
      <div class="head">
        <h2>Runs</h2>
        <span class="conn ${this.connected ? "on" : "off"}">
          ${this.connected ? "live" : "reconnecting…"}
        </span>
      </div>
      <table>
        <thead>
          <tr>
            <th>run</th><th>status</th><th>supervisor</th><th>tokens</th><th>tools</th><th>updated</th>
          </tr>
        </thead>
        <tbody>
          ${this.runs.map(
            (run) => html`
              <tr>
                <td title=${run.runId}>${shortId(run.runId)}</td>
                <td>${statusBadge(run.status, run.active)}</td>
                <td>${run.supervisorKey ?? "?"}</td>
                <td>${run.inputTokens}/${run.outputTokens}</td>
                <td>${run.toolCalls}</td>
                <td>${time(run.updatedAt)}</td>
              </tr>
            `,
          )}
          ${this.runs.length === 0 ? html`<tr><td colspan="6" class="dim">no runs yet</td></tr>` : ""}
        </tbody>
      </table>
      <h3>Events</h3>
      <ul class="feed">
        ${this.feed.map(
          (event) => html`<li><code>${shortId(event.runId)}</code> ${eventLine(event)}</li>`,
        )}
        ${this.feed.length === 0 ? html`<li class="dim">no events yet</li>` : ""}
      </ul>
    `;
  }

  static override styles = css`
    :host { display: block; }
    .head { display: flex; align-items: baseline; gap: 1rem; }
    .conn.on { color: var(--ok); }
    .conn.off { color: var(--warn); }
    table { width: 100%; border-collapse: collapse; }
    th, td { text-align: left; padding: 0.3rem 0.6rem; border-bottom: 1px solid var(--line); }
    .badge { padding: 0.1rem 0.5rem; border-radius: 0.6rem; font-size: 0.85em; background: var(--line); }
    .badge.completed { background: var(--ok-bg); }
    .badge.awaitingSupervision { background: var(--warn-bg); }
    .badge.awaitingUser { background: var(--info-bg); }
    .badge.budgetExceeded { background: var(--err-bg); }
    .feed { list-style: none; padding: 0; font-family: monospace; font-size: 0.9em; }
    .feed li { padding: 0.15rem 0; border-bottom: 1px dotted var(--line); }
    .dim { color: var(--dim); }
  `;
}

const unauthorized = () => new CustomEvent("orkis-unauthorized", { bubbles: true, composed: true });

customElements.define("runs-view", RunsView);
