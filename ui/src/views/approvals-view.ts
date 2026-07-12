import { css, html, LitElement } from "lit";
import { api, Unauthorized } from "../api.js";
import { shortId, time } from "../format.js";
import type { ApprovalResponse } from "../types.js";

/** The approval inbox: the same grant lattice as the CLI prompt, as buttons. */
export class ApprovalsView extends LitElement {
  static override properties = { approvals: { state: true }, note: { state: true } };

  declare approvals: ApprovalResponse[];
  declare note: string;

  private refreshTimer?: number;

  constructor() {
    super();
    this.approvals = [];
    this.note = "";
  }

  override connectedCallback(): void {
    super.connectedCallback();
    void this.refresh();
    this.refreshTimer = window.setInterval(() => void this.refresh(), 3000);
  }

  override disconnectedCallback(): void {
    super.disconnectedCallback();
    window.clearInterval(this.refreshTimer);
  }

  private async refresh(): Promise<void> {
    try {
      this.approvals = await api.approvals();
    } catch (error) {
      if (error instanceof Unauthorized) {
        this.dispatchEvent(
          new CustomEvent("orkis-unauthorized", { bubbles: true, composed: true }),
        );
      }
    }
  }

  private async decide(
    approval: ApprovalResponse,
    body: { verdict: string; sandboxLevel?: string; network?: string; reason?: string },
  ): Promise<void> {
    try {
      await api.decide(approval.runId, approval.callId, body);
      // Resume once nothing is pending for the run — mirroring the CLI's flow.
      const remaining = await api.approvals(approval.runId);
      if (remaining.length === 0) {
        await api.resume(approval.runId);
        this.note = `${body.verdict}d and resumed ${shortId(approval.runId)}`;
      } else {
        this.note = `${body.verdict}d ${approval.callId}; ${remaining.length} still pending`;
      }
    } catch (error) {
      this.note = error instanceof Error ? error.message : String(error);
    }
    await this.refresh();
  }

  override render() {
    return html`
      <h2>Approvals</h2>
      ${this.note ? html`<p class="note">${this.note}</p>` : ""}
      ${this.approvals.length === 0 ? html`<p class="dim">no pending approvals</p>` : ""}
      ${this.approvals.map(
        (approval) => html`
          <div class="card">
            <div class="what">
              <code>${shortId(approval.runId)}</code>
              <strong>${approval.toolName}</strong>
              <span class="dim">risk: ${approval.risk} · ${time(approval.requestedAt)}</span>
            </div>
            <pre>${JSON.stringify(approval.arguments, null, 2)}</pre>
            <div class="actions">
              <button @click=${() => this.decide(approval, { verdict: "approve" })}>
                approve (host)
              </button>
              <button @click=${() => this.decide(approval, { verdict: "approve", sandboxLevel: "standard" })}>
                approve (sandboxed)
              </button>
              <button
                @click=${() =>
                  this.decide(approval, {
                    verdict: "approve",
                    sandboxLevel: "standard",
                    network: "restrictedEgress",
                  })}
              >
                approve (sandboxed + network)
              </button>
              <button class="deny" @click=${() => this.decide(approval, { verdict: "deny" })}>
                deny
              </button>
            </div>
          </div>
        `,
      )}
    `;
  }

  static override styles = css`
    :host { display: block; }
    .card { border: 1px solid var(--line); border-radius: 0.5rem; padding: 0.8rem; margin: 0.8rem 0; }
    .what { display: flex; gap: 0.8rem; align-items: baseline; }
    pre { background: var(--panel); padding: 0.5rem; overflow-x: auto; }
    .actions { display: flex; gap: 0.5rem; flex-wrap: wrap; }
    button.deny { background: var(--err-bg); }
    .dim { color: var(--dim); }
    .note { color: var(--ok); }
  `;
}

customElements.define("approvals-view", ApprovalsView);
