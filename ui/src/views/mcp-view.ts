import { css, html, LitElement } from "lit";
import { api, Unauthorized } from "../api.js";
import type { McpServerResponse } from "../types.js";

/** MCP servers connected to the live daemon: list, connect, disconnect. */
export class McpView extends LitElement {
  static override properties = { servers: { state: true }, error: { state: true } };

  declare servers: McpServerResponse[];
  declare error: string;

  constructor() {
    super();
    this.servers = [];
    this.error = "";
  }

  override connectedCallback(): void {
    super.connectedCallback();
    void this.refresh();
  }

  private async refresh(): Promise<void> {
    try {
      this.servers = await api.mcpServers();
    } catch (error) {
      if (error instanceof Unauthorized) this.unauthorized();
    }
  }

  private unauthorized(): void {
    this.dispatchEvent(new CustomEvent("orkis-unauthorized", { bubbles: true, composed: true }));
  }

  private async add(event: Event): Promise<void> {
    event.preventDefault();
    const form = event.target as HTMLFormElement;
    const data = new FormData(form);
    const server = String(data.get("server") ?? "").trim();
    if (!server) return;
    this.error = "";
    try {
      await api.addMcpServer({
        server,
        name: String(data.get("name") ?? "").trim() || undefined,
      });
      form.reset();
      await this.refresh();
    } catch (error) {
      if (error instanceof Unauthorized) this.unauthorized();
      else this.error = error instanceof Error ? error.message : String(error);
    }
  }

  private async disconnect(name: string): Promise<void> {
    try {
      await api.removeMcpServer(name);
      await this.refresh();
    } catch (error) {
      if (error instanceof Unauthorized) this.unauthorized();
    }
  }

  override render() {
    return html`
      <h2>MCP servers</h2>
      <p class="dim">
        Connect an MCP server and its tools join the catalogue for new runs to search — no restart.
      </p>
      <form @submit=${(e: Event) => void this.add(e)}>
        <input name="server" placeholder="http(s):// endpoint or stdio command line" required />
        <input name="name" placeholder="name (optional)" />
        <button type="submit">connect</button>
      </form>
      ${this.error ? html`<p class="err">${this.error}</p>` : ""}
      ${this.servers.length === 0 ? html`<p class="dim">no mcp servers</p>` : ""}
      ${this.servers.map(
        (server) => html`
          <div class="row">
            <div class="who">
              <strong>${server.name}</strong>
              <code>${server.server}</code>
              <span class="tools">
                ${server.tools.length === 0
                  ? html`<span class="dim">no tools</span>`
                  : server.tools.map((tool) => html`<span class="tag">${tool}</span>`)}
              </span>
            </div>
            <button class="del" @click=${() => void this.disconnect(server.name)}>disconnect</button>
          </div>
        `,
      )}
    `;
  }

  static override styles = css`
    :host { display: block; }
    form { display: flex; gap: 0.4rem; flex-wrap: wrap; margin-bottom: 1rem; }
    input { padding: 0.4rem; }
    input[name="server"] { flex: 1; min-width: 16rem; }
    .row {
      display: flex; justify-content: space-between; align-items: flex-start;
      border-bottom: 1px solid var(--line); padding: 0.6rem 0; gap: 1rem;
    }
    .who { display: flex; flex-direction: column; gap: 0.3rem; min-width: 0; }
    code {
      background: var(--panel); padding: 0.1rem 0.4rem; border-radius: 0.3rem;
      word-break: break-all; color: var(--dim);
    }
    .tools { display: flex; gap: 0.3rem; flex-wrap: wrap; }
    .tag { background: var(--panel); border-radius: 0.3rem; padding: 0.1rem 0.4rem; font-size: 0.85em; }
    button.del { background: var(--err-bg); align-self: center; }
    .dim { color: var(--dim); }
    .err { color: var(--err); }
  `;
}

customElements.define("mcp-view", McpView);
