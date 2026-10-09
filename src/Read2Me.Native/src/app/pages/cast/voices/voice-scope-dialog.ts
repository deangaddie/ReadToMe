import { html } from 'lit-html';
import { openDialog } from '@app/core/dialog';
import { R2mElement, define } from '@app/core/element';
import { icon } from '@app/ui/partials';
import type { PromptBatchScope } from './voice-logic';

/**
 * The generate-voices scope choice: shown before the prompt batch when any character already
 * has voices. "Clear and regenerate all" is destructive (every voice, its audio and rules go first).
 */
export class VoiceScopeDialog extends R2mElement {
  protected template() {
    return html`
      <h2 class="r2m-dialog__title">Generate voice prompts</h2>
      <div class="r2m-dialog__content voice-scope">
        <p>Some characters already have voices. Choose what to generate.</p>
        <p class="voice-scope__warning">
          <strong>Clear and regenerate all</strong> deletes every character's existing voices (and
          their audio and rules) first. This cannot be undone.
        </p>
      </div>
      <div class="r2m-dialog__actions">
        <button type="button" class="r2m-button" @click=${() => this.emit('r2m-close', null)}>
          Cancel
        </button>
        <button
          type="button"
          class="r2m-button r2m-button--stroked"
          data-scope="only-without"
          @click=${() => this.choose('only-without')}
        >
          Only characters without voices
        </button>
        <button
          type="button"
          class="r2m-button r2m-button--filled r2m-button--danger"
          data-scope="regenerate-all"
          @click=${() => this.choose('regenerate-all')}
        >
          ${icon('delete_sweep')} Clear and regenerate all
        </button>
      </div>
    `;
  }

  choose(scope: PromptBatchScope): void {
    this.emit<PromptBatchScope>('r2m-close', scope);
  }
}
define('r2m-voice-scope-dialog', VoiceScopeDialog);

/** Resolves the chosen scope, or null on Cancel / Escape / backdrop. */
export async function openVoiceScopeDialog(): Promise<PromptBatchScope | null> {
  const dialog = document.createElement('r2m-voice-scope-dialog');
  return (await openDialog<PromptBatchScope | null>(dialog)) ?? null;
}

declare global {
  interface HTMLElementTagNameMap {
    'r2m-voice-scope-dialog': VoiceScopeDialog;
  }
}
