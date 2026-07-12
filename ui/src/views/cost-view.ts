import { css, html, LitElement } from "lit";
import { api, Unauthorized } from "../api.js";
import { shortId, time } from "../format.js";
import type { RunResponse } from "../types.js";

const money = (n: number): string => `$${n.toFixed(4)}`;
const count = (n: number): string => n.toLocaleString();

/** Token and cost accounting across all runs — no new backend; it aggregates /v1/runs. */
export class CostView extends LitElement {
  static override properties = { runs: { state: true } };

  declare runs: RunResponse[];

  constructor() {
    super();
    this.runs = [];
  }

  override connectedCallback(): void {
    super.connectedCallback();
    void this.refresh();
  }

  private async refresh(): Promise<void> {
    try {
      this.runs = await api.runs();
    } catch (error) {
      if (error instanceof Unauthorized)
        this.dispatchEvent(new CustomEvent("orkis-unauthorized", { bubbles: true, composed: true }));
    }
  }

  private totalsFor(runs: RunResponse[]) {
    return runs.reduce(
      (sum, run) => ({
        cost: sum.cost + run.cost,
        input: sum.input + run.inputTokens,
        output: sum.output + run.outputTokens,
        tools: sum.tools + run.toolCalls,
        runs: sum.runs + 1,
      }),
      { cost: 0, input: 0, output: 0, tools: 0, runs: 0 },
    );
  }

  private static isScheduled(run: RunResponse): boolean {
    return (run.origin ?? "").startsWith("schedule:");
  }

  override render() {
    const all = this.totalsFor(this.runs);
    // Scheduled runs accumulate cost unattended, so it is worth splitting them out.
    const scheduled = this.totalsFor(this.runs.filter((run) => CostView.isScheduled(run)));
    const direct = this.totalsFor(this.runs.filter((run) => !CostView.isScheduled(run)));
    const byCost = [...this.runs].sort((a, b) => b.cost - a.cost);

    return html`
      <h2>Cost</h2>
      <div class="cards">
        <div class="card">
          <span class="label">total cost</span><strong>${money(all.cost)}</strong>
        </div>
        <div class="card"><span class="label">runs</span><strong>${count(all.runs)}</strong></div>
        <div class="card">
          <span class="label">input tokens</span><strong>${count(all.input)}</strong>
        </div>
        <div class="card">
          <span class="label">output tokens</span><strong>${count(all.output)}</strong>
        </div>
        <div class="card">
          <span class="label">tool calls</span><strong>${count(all.tools)}</strong>
        </div>
      </div>

      <div class="split">
        <span>direct: <strong>${money(direct.cost)}</strong> <span class="dim">(${count(direct.runs)} runs)</span></span>
        <span>
          scheduled: <strong>${money(scheduled.cost)}</strong>
          <span class="dim">(${count(scheduled.runs)} runs)</span>
        </span>
      </div>

      ${this.runs.length === 0
        ? html`<p class="dim">no runs yet</p>`
        : html`
            <table>
              <thead>
                <tr><th>run</th><th>origin</th><th class="n">cost</th><th class="n">in / out</th>
                  <th class="n">tools</th><th>updated</th></tr>
              </thead>
              <tbody>
                ${byCost.map(
                  (run) => html`
                    <tr>
                      <td title=${run.runId}><code>${shortId(run.runId)}</code></td>
                      <td>${CostView.isScheduled(run) ? "scheduled" : "direct"}</td>
                      <td class="n">${money(run.cost)}</td>
                      <td class="n">${count(run.inputTokens)} / ${count(run.outputTokens)}</td>
                      <td class="n">${run.toolCalls}</td>
                      <td>${time(run.updatedAt)}</td>
                    </tr>
                  `,
                )}
              </tbody>
            </table>
          `}
    `;
  }

  static override styles = css`
    :host { display: block; }
    .cards { display: flex; gap: 0.6rem; flex-wrap: wrap; margin-bottom: 1rem; }
    .card {
      display: flex; flex-direction: column; gap: 0.2rem;
      background: var(--panel); border-radius: 0.4rem; padding: 0.6rem 0.9rem; min-width: 7rem;
    }
    .card strong { font-size: 1.3rem; }
    .label { color: var(--dim); font-size: 0.85em; }
    .split { display: flex; gap: 1.5rem; margin-bottom: 1rem; flex-wrap: wrap; }
    table { width: 100%; border-collapse: collapse; }
    th, td { text-align: left; padding: 0.4rem 0.6rem; border-bottom: 1px solid var(--line); }
    th.n, td.n { text-align: right; font-variant-numeric: tabular-nums; }
    code { background: var(--panel); padding: 0.1rem 0.4rem; border-radius: 0.3rem; }
    .dim { color: var(--dim); }
  `;
}

customElements.define("cost-view", CostView);
