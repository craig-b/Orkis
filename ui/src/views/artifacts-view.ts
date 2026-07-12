import { css, html, LitElement } from "lit";
import { api, artifactUrl, Unauthorized } from "../api.js";
import { time } from "../format.js";
import type { ArtifactInfo } from "../types.js";

/** Promoted artifacts: list and download. */
export class ArtifactsView extends LitElement {
  static override properties = { artifacts: { state: true } };

  declare artifacts: ArtifactInfo[];

  constructor() {
    super();
    this.artifacts = [];
  }

  override connectedCallback(): void {
    super.connectedCallback();
    void this.refresh();
  }

  private async refresh(): Promise<void> {
    try {
      this.artifacts = await api.artifacts();
    } catch (error) {
      if (error instanceof Unauthorized) {
        this.dispatchEvent(new CustomEvent("orkis-unauthorized", { bubbles: true, composed: true }));
      }
    }
  }

  override render() {
    return html`
      <h2>Artifacts</h2>
      ${this.artifacts.length === 0 ? html`<p class="dim">no artifacts</p>` : ""}
      <table>
        ${this.artifacts.map(
          (artifact) => html`
            <tr>
              <td><a href=${artifactUrl(artifact.name)} download=${artifact.name}>${artifact.name}</a></td>
              <td class="dim">${artifact.length} bytes</td>
              <td class="dim">${time(artifact.createdAt)}</td>
            </tr>
          `,
        )}
      </table>
    `;
  }

  static override styles = css`
    :host { display: block; }
    table { width: 100%; border-collapse: collapse; }
    td { padding: 0.4rem 0.6rem; border-bottom: 1px solid var(--line); }
    .dim { color: var(--dim); }
    a { color: var(--accent); }
  `;
}

customElements.define("artifacts-view", ArtifactsView);
