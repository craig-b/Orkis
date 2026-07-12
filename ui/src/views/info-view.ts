import { css, html, LitElement } from "lit";
import { api, Unauthorized } from "../api.js";
import type { CapabilitiesResponse } from "../types.js";

/** The capabilities page — enumeration, never hardcoding. */
export class InfoView extends LitElement {
  static override properties = { capabilities: { state: true } };

  declare capabilities: CapabilitiesResponse | null;

  constructor() {
    super();
    this.capabilities = null;
  }

  override connectedCallback(): void {
    super.connectedCallback();
    void this.load();
  }

  private async load(): Promise<void> {
    try {
      this.capabilities = await api.capabilities();
    } catch (error) {
      if (error instanceof Unauthorized) {
        this.dispatchEvent(
          new CustomEvent("orkis-unauthorized", { bubbles: true, composed: true }),
        );
      }
    }
  }

  override render() {
    const c = this.capabilities;
    if (c === null) return html`<p class="dim">loading…</p>`;
    return html`
      <h2>Daemon</h2>
      <dl>
        <dt>supervisors</dt>
        <dd>${c.supervisors.map((s) => (s === c.defaultSupervisor ? `${s} (default)` : s)).join(", ")}</dd>
        <dt>models</dt>
        <dd>default (${c.defaultModel ?? "offline script"})${c.models.length > 0 ? `, ${c.models.join(", ")}` : ""}</dd>
        <dt>sandbox</dt>
        <dd>${c.sandbox}</dd>
        <dt>memory</dt>
        <dd>${c.memory ? "on" : "off"}</dd>
        <dt>corpus retrieval</dt>
        <dd>${c.corpusRetrieval ? "on" : "off"}</dd>
        <dt>tools</dt>
        <dd>${c.tools.join(", ")}</dd>
        <dt>catalogue</dt>
        <dd>${c.catalogTools.length > 0 ? c.catalogTools.join(", ") : html`<span class="dim">empty</span>`}</dd>
      </dl>
    `;
  }

  static override styles = css`
    :host { display: block; }
    dl { display: grid; grid-template-columns: max-content 1fr; gap: 0.4rem 1.2rem; }
    dt { color: var(--dim); }
    dd { margin: 0; }
    .dim { color: var(--dim); }
  `;
}

customElements.define("info-view", InfoView);
