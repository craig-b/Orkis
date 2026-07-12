import { css, html, LitElement } from "lit";
import { api, Unauthorized } from "../api.js";
import { shortId, time } from "../format.js";
import type { ScheduleResponse } from "../types.js";

/** Cron-triggered run templates: list, create, remove. */
export class SchedulesView extends LitElement {
  static override properties = { schedules: { state: true }, error: { state: true } };

  declare schedules: ScheduleResponse[];
  declare error: string;

  constructor() {
    super();
    this.schedules = [];
    this.error = "";
  }

  override connectedCallback(): void {
    super.connectedCallback();
    void this.refresh();
  }

  private async refresh(): Promise<void> {
    try {
      this.schedules = await api.schedules();
    } catch (error) {
      if (error instanceof Unauthorized) this.unauthorized();
    }
  }

  private unauthorized(): void {
    this.dispatchEvent(new CustomEvent("orkis-unauthorized", { bubbles: true, composed: true }));
  }

  private async create(event: Event): Promise<void> {
    event.preventDefault();
    const form = event.target as HTMLFormElement;
    const data = new FormData(form);
    const cron = String(data.get("cron") ?? "").trim();
    const prompt = String(data.get("prompt") ?? "").trim();
    if (!cron || !prompt) return;
    this.error = "";
    try {
      await api.createSchedule({
        name: String(data.get("name") ?? "").trim() || prompt,
        cron,
        prompt,
        supervisorKey: String(data.get("supervisor") ?? "queue"),
        continuity: String(data.get("continuity") ?? "fresh"),
      });
      form.reset();
      await this.refresh();
    } catch (error) {
      if (error instanceof Unauthorized) this.unauthorized();
      else this.error = error instanceof Error ? error.message : String(error);
    }
  }

  private async deleteSchedule(id: string): Promise<void> {
    try {
      await api.deleteSchedule(id);
      await this.refresh();
    } catch (error) {
      if (error instanceof Unauthorized) this.unauthorized();
    }
  }

  private async toggleEnabled(id: string, enabled: boolean): Promise<void> {
    try {
      await api.updateSchedule(id, { enabled });
      await this.refresh();
    } catch (error) {
      if (error instanceof Unauthorized) this.unauthorized();
    }
  }

  override render() {
    return html`
      <h2>Schedules</h2>
      <form @submit=${(e: Event) => void this.create(e)}>
        <input name="cron" placeholder="cron (e.g. 0 7 * * *)" required />
        <input name="prompt" placeholder="prompt" required />
        <input name="name" placeholder="name (optional)" />
        <select name="supervisor">
          <option value="queue">queue</option>
          <option value="yolo">yolo</option>
          <option value="ai">ai</option>
        </select>
        <select name="continuity">
          <option value="fresh">fresh</option>
          <option value="sharedStorage">sharedStorage</option>
          <option value="sharedStorageWithHandoff">sharedStorageWithHandoff</option>
        </select>
        <button type="submit">add</button>
      </form>
      ${this.error ? html`<p class="err">${this.error}</p>` : ""}
      ${this.schedules.length === 0 ? html`<p class="dim">no schedules</p>` : ""}
      ${this.schedules.map(
        (schedule) => html`
          <div class="row ${schedule.enabled ? "" : "paused"}">
            <div>
              <strong>${schedule.name}</strong>
              <code>${schedule.cron}</code>
              <span class="dim">${schedule.supervisorKey} · ${schedule.continuity}</span>
              ${schedule.enabled ? "" : html`<span class="tag">paused</span>`}
            </div>
            <div class="meta">
              <span class="dim">
                ${schedule.lastFiredAt ? `last fired ${time(schedule.lastFiredAt)}` : "never fired"}
                ${schedule.lastRunId ? html`(${shortId(schedule.lastRunId)})` : ""}
              </span>
              <button @click=${() => void this.toggleEnabled(schedule.id, !schedule.enabled)}>
                ${schedule.enabled ? "pause" : "resume"}
              </button>
              <button class="del" @click=${() => void this.deleteSchedule(schedule.id)}>remove</button>
            </div>
          </div>
        `,
      )}
    `;
  }

  static override styles = css`
    :host { display: block; }
    form { display: flex; gap: 0.4rem; flex-wrap: wrap; margin-bottom: 1rem; }
    input { padding: 0.4rem; }
    input[name="cron"] { width: 12rem; }
    input[name="prompt"] { flex: 1; min-width: 12rem; }
    .row {
      display: flex; justify-content: space-between; align-items: center;
      border-bottom: 1px solid var(--line); padding: 0.6rem 0; gap: 1rem;
    }
    code { background: var(--panel); padding: 0.1rem 0.4rem; border-radius: 0.3rem; margin: 0 0.5rem; }
    .row.paused { opacity: 0.6; }
    .tag { background: var(--panel); border-radius: 0.3rem; padding: 0.1rem 0.4rem; margin-left: 0.5rem; font-size: 0.85em; }
    .meta { display: flex; gap: 0.8rem; align-items: center; }
    button.del { background: var(--err-bg); }
    .dim { color: var(--dim); }
    .err { color: var(--err); }
  `;
}

customElements.define("schedules-view", SchedulesView);
