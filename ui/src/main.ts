import { css, html, LitElement } from "lit";
import { api } from "./api.js";
import "./views/runs-view.js";
import "./views/approvals-view.js";
import "./views/chat-view.js";
import "./views/info-view.js";

type Tab = "runs" | "approvals" | "chat" | "info";
const tabs: Tab[] = ["runs", "approvals", "chat", "info"];

/** The shell: tabs, plus the login overlay any view can summon on a 401. */
export class OrkisApp extends LitElement {
  static override properties = { tab: { state: true }, needsLogin: { state: true }, loginError: { state: true } };

  declare tab: Tab;
  declare needsLogin: boolean;
  declare loginError: string;

  constructor() {
    super();
    this.tab = "runs";
    this.needsLogin = false;
    this.loginError = "";
    this.addEventListener("orkis-unauthorized", () => {
      this.needsLogin = true;
    });
  }

  private async login(event: Event): Promise<void> {
    event.preventDefault();
    const input = this.renderRoot.querySelector("input") as HTMLInputElement;
    try {
      await api.login(input.value.trim());
      this.needsLogin = false;
      this.loginError = "";
      // Re-mount the current view so it refetches with the session cookie.
      const current = this.tab;
      this.tab = current === "runs" ? "info" : "runs";
      await this.updateComplete;
      this.tab = current;
    } catch {
      this.loginError = "that token was not accepted";
    }
  }

  override render() {
    return html`
      <header>
        <h1>orkis</h1>
        <nav>
          ${tabs.map(
            (tab) => html`
              <button class=${this.tab === tab ? "current" : ""} @click=${() => (this.tab = tab)}>
                ${tab}
              </button>
            `,
          )}
        </nav>
      </header>
      <main>
        ${this.tab === "runs" ? html`<runs-view></runs-view>` : ""}
        ${this.tab === "approvals" ? html`<approvals-view></approvals-view>` : ""}
        ${this.tab === "chat" ? html`<chat-view></chat-view>` : ""}
        ${this.tab === "info" ? html`<info-view></info-view>` : ""}
      </main>
      ${this.needsLogin
        ? html`
            <div class="scrim">
              <form class="login" @submit=${(e: Event) => void this.login(e)}>
                <h2>Sign in</h2>
                <p>Paste the gateway's token (printed at startup, or ORKIS_TOKEN).</p>
                <input type="password" placeholder="token" autocomplete="off" />
                ${this.loginError ? html`<p class="err">${this.loginError}</p>` : ""}
                <button type="submit">create session</button>
              </form>
            </div>
          `
        : ""}
    `;
  }

  static override styles = css`
    :host { display: block; max-width: 60rem; margin: 0 auto; padding: 1rem; }
    header { display: flex; align-items: baseline; gap: 2rem; border-bottom: 2px solid var(--line); }
    nav { display: flex; gap: 0.3rem; }
    nav button { border: none; background: none; padding: 0.5rem 0.9rem; cursor: pointer; font: inherit; }
    nav button.current { border-bottom: 2px solid var(--accent); font-weight: 600; }
    .scrim {
      position: fixed; inset: 0; background: rgb(0 0 0 / 40%);
      display: grid; place-items: center;
    }
    .login {
      background: var(--bg); padding: 2rem; border-radius: 0.8rem;
      display: flex; flex-direction: column; gap: 0.8rem; min-width: 22rem;
    }
    .err { color: var(--err); }
  `;
}

customElements.define("orkis-app", OrkisApp);
