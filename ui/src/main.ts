import { css, html, LitElement } from "lit";
import { api, Unauthorized } from "./api.js";
import { publishConnection, publishRunEvent } from "./bus.js";
import { shortId } from "./format.js";
import type { RunEvent } from "./types.js";
import { fireNotification, notifyPermission, requestNotifyPermission, type NotifyPermission } from "./notifications.js";
import "./views/runs-view.js";
import "./views/approvals-view.js";
import "./views/chat-view.js";
import "./views/info-view.js";

type Tab = "runs" | "approvals" | "chat" | "info";
const tabs: Tab[] = ["runs", "approvals", "chat", "info"];

interface Toast {
  id: number;
  text: string;
}

/**
 * The shell: tabs, the login overlay, and the notification layer — a single global
 * event subscription toasts (and desktop-notifies, when permitted) on run_paused,
 * and the approvals tab carries a live pending count.
 */
export class OrkisApp extends LitElement {
  static override properties = {
    tab: { state: true },
    needsLogin: { state: true },
    loginError: { state: true },
    toasts: { state: true },
    pending: { state: true },
    permission: { state: true },
  };

  declare tab: Tab;
  declare needsLogin: boolean;
  declare loginError: string;
  declare toasts: Toast[];
  declare pending: number;
  declare permission: NotifyPermission;

  private source?: EventSource;
  private pendingTimer?: number;
  private nextToastId = 0;

  constructor() {
    super();
    this.tab = "runs";
    this.needsLogin = false;
    this.loginError = "";
    this.toasts = [];
    this.pending = 0;
    this.permission = notifyPermission();
    this.addEventListener("orkis-unauthorized", () => {
      this.needsLogin = true;
    });
  }

  override connectedCallback(): void {
    super.connectedCallback();
    // The shell owns the single daemon-wide event stream and republishes to views.
    // EventSource is native SSE: robust chunking, and it reconnects on its own.
    this.source = new EventSource("/v1/events");
    this.source.onopen = () => publishConnection(true);
    this.source.onmessage = (message) => {
      let event: RunEvent;
      try {
        event = JSON.parse(message.data) as RunEvent;
      } catch {
        return;
      }
      publishConnection(true);
      publishRunEvent(event);
      if (event.$type === "run_paused") {
        this.announce(event.runId);
      }
      if (event.$type === "supervision_decided" || event.$type === "run_resumed") {
        void this.refreshPending();
      }
    };
    this.source.onerror = () => publishConnection(false);

    void this.refreshPending();
    this.pendingTimer = window.setInterval(() => void this.refreshPending(), 3000);
  }

  override disconnectedCallback(): void {
    super.disconnectedCallback();
    this.source?.close();
    window.clearInterval(this.pendingTimer);
  }

  private announce(runId: string): void {
    const text = `Approval needed — ${shortId(runId)}`;
    const id = this.nextToastId++;
    this.toasts = [...this.toasts, { id, text }];
    window.setTimeout(() => (this.toasts = this.toasts.filter((t) => t.id !== id)), 8000);
    fireNotification("Orkis: approval needed", `Run ${shortId(runId)} is waiting.`, () => {
      this.tab = "approvals";
    });
    void this.refreshPending();
  }

  private async refreshPending(): Promise<void> {
    try {
      this.pending = (await api.approvals()).length;
    } catch (error) {
      if (error instanceof Unauthorized) this.needsLogin = true;
    }
  }

  private async enableNotifications(): Promise<void> {
    this.permission = await requestNotifyPermission();
  }

  private async login(event: Event): Promise<void> {
    event.preventDefault();
    const input = this.renderRoot.querySelector(".login input") as HTMLInputElement;
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
                ${tab === "approvals" && this.pending > 0 ? html`<span class="badge">${this.pending}</span>` : ""}
              </button>
            `,
          )}
        </nav>
        ${this.permission === "default"
          ? html`<button class="bell" @click=${() => void this.enableNotifications()}>🔔 enable</button>`
          : ""}
      </header>
      <main>
        ${this.tab === "runs" ? html`<runs-view></runs-view>` : ""}
        ${this.tab === "approvals" ? html`<approvals-view></approvals-view>` : ""}
        ${this.tab === "chat" ? html`<chat-view></chat-view>` : ""}
        ${this.tab === "info" ? html`<info-view></info-view>` : ""}
      </main>
      <div class="toasts">
        ${this.toasts.map(
          (toast) => html`
            <button class="toast" @click=${() => (this.tab = "approvals")}>${toast.text}</button>
          `,
        )}
      </div>
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
    nav { display: flex; gap: 0.3rem; flex: 1; }
    nav button { border: none; background: none; padding: 0.5rem 0.9rem; cursor: pointer; font: inherit; }
    nav button.current { border-bottom: 2px solid var(--accent); font-weight: 600; }
    .badge {
      display: inline-block; margin-left: 0.35rem; padding: 0 0.4rem; border-radius: 0.7rem;
      background: var(--warn-bg); color: var(--warn); font-size: 0.8em;
    }
    .bell { border: 1px solid var(--line); background: none; border-radius: 0.4rem; cursor: pointer; font: inherit; padding: 0.3rem 0.6rem; }
    .toasts { position: fixed; top: 1rem; right: 1rem; display: flex; flex-direction: column; gap: 0.5rem; z-index: 10; }
    .toast {
      background: var(--warn-bg); color: var(--warn); border: 1px solid var(--warn);
      border-radius: 0.5rem; padding: 0.7rem 1rem; cursor: pointer; font: inherit; text-align: left;
    }
    .scrim { position: fixed; inset: 0; background: rgb(0 0 0 / 40%); display: grid; place-items: center; }
    .login {
      background: var(--bg); padding: 2rem; border-radius: 0.8rem;
      display: flex; flex-direction: column; gap: 0.8rem; min-width: 22rem;
    }
    .err { color: var(--err); }
  `;
}

customElements.define("orkis-app", OrkisApp);
