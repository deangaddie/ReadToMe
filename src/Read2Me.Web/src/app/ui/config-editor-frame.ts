import { html, nothing } from 'lit-html';
import { spinner } from './partials';

export interface ConfigEditorFrameOptions {
  title: string;
  subtitle?: string | null;
  dirty: boolean;
  saving: boolean;
  /** Extra gate on Save beyond dirty (a form that knows it is invalid); defaults to true. */
  canSave?: boolean;
  onSave: () => void;
  onCancel: () => void;
}

/**
 * Detail-side frame of the settings master–detail template (design §6.4, §7): title, the form
 * body, and a Save / Cancel footer gated on the dirty flag. Stateless, so a partial.
 */
export function configEditorFrame(o: ConfigEditorFrameOptions, body: unknown) {
  const saveEnabled = o.dirty && (o.canSave ?? true) && !o.saving;
  return html`<section class="r2m-config-editor-frame">
    <header class="r2m-config-editor-frame__head">
      <div>
        <h2 class="r2m-config-editor-frame__title">${o.title}</h2>
        ${o.subtitle ? html`<p class="r2m-config-editor-frame__subtitle">${o.subtitle}</p>` : nothing}
      </div>
      ${
        o.dirty
          ? html`<span class="r2m-config-editor-frame__dirty" role="status">Unsaved changes</span>`
          : nothing
      }
    </header>
    <div class="r2m-config-editor-frame__body">${body}</div>
    <footer class="r2m-config-editor-frame__foot">
      <button
        type="button"
        class="r2m-button"
        ?disabled=${!o.dirty || o.saving}
        @click=${o.onCancel}
      >
        Cancel
      </button>
      <button
        type="button"
        class="r2m-button r2m-button--filled"
        ?disabled=${!saveEnabled}
        @click=${o.onSave}
      >
        ${o.saving ? spinner(16) : nothing} ${o.saving ? 'Saving…' : 'Save'}
      </button>
    </footer>
  </section>`;
}
