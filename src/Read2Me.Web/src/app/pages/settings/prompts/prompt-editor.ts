import { TextFieldModule } from '@angular/cdk/text-field';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { PromptCatalogEntry, toApiError } from '@app/api';
import { ConfigEditorFrame } from '@app/ui/config-editor-frame/config-editor-frame';
import { ConfirmService } from '@app/ui/confirm-dialog/confirm-dialog';
import { ToastService } from '@app/ui/toast/toast.service';
import { canReset, insertToken, isDirty, tokenLiteral } from './prompt-editing';
import { PromptsStore } from './prompts-store';

/** How long the preview waits after the last keystroke before asking the host again. */
export const PREVIEW_DEBOUNCE_MS = 300;

/**
 * Detail side of the prompts page (ticket 23): one kind's description, compatibility warnings,
 * token chips that insert at the caret, the monospace template, the expected response and a
 * preview of the *unsaved* draft rendered by the host with its sample book. Save is dirty-gated;
 * Reset to default drops the stored override (or just the unsaved edits) after a confirm.
 */
@Component({
  selector: 'app-prompt-editor',
  imports: [
    MatButtonModule,
    MatExpansionModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressSpinnerModule,
    TextFieldModule,
    ConfigEditorFrame,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <r2m-config-editor-frame
      [title]="entry().title"
      [dirty]="dirty()"
      [saving]="saving()"
      (save)="save()"
      (cancel)="draft.set(entry().template)"
    >
      <p class="prompt-editor__description">{{ entry().description }}</p>

      @for (warning of entry().warnings; track warning) {
        <p class="prompt-editor__warning" role="alert" data-role="prompt-warning">
          <mat-icon aria-hidden="true">warning</mat-icon>
          {{ warning }}
        </p>
      }

      <div class="prompt-editor__tokens" role="group" aria-label="Available tokens">
        <span class="prompt-editor__label">Available tokens — click to insert:</span>
        @for (token of entry().tokens; track token) {
          <button
            type="button"
            class="prompt-editor__token"
            [attr.data-token]="token"
            [disabled]="saving()"
            (click)="insert(token)"
          >
            {{ literal(token) }}
          </button>
        }
      </div>

      <mat-form-field appearance="outline" subscriptSizing="dynamic" class="prompt-editor__field">
        <mat-label>Prompt template</mat-label>
        <textarea
          #template
          matInput
          cdkTextareaAutosize
          cdkAutosizeMinRows="12"
          cdkAutosizeMaxRows="32"
          class="prompt-editor__template"
          data-field="template"
          spellcheck="false"
          [disabled]="saving()"
          [value]="draft()"
          (input)="draft.set($any($event.target).value)"
        ></textarea>
      </mat-form-field>
      @if (entry().tokens.includes('narrator_identity')) {
        <p class="prompt-editor__note">
          <code>{{ literal('narrator_identity') }}</code> renders empty unless the book has a
          narrator link.
        </p>
      }
      @if (entry().tokens.includes('also_narrates')) {
        <p class="prompt-editor__note">
          <code>{{ literal('also_narrates') }}</code> renders empty unless this character has the
          book's narrator link.
        </p>
      }

      @if (entry().expectedResponse; as expected) {
        <section class="prompt-editor__expected">
          <span class="prompt-editor__label">
            Expected response (JSON) — displayed for reference, not editable:
          </span>
          <pre class="prompt-editor__pre" data-role="expected-response">{{ expected }}</pre>
        </section>
      }

      <mat-expansion-panel
        class="prompt-editor__preview"
        [expanded]="previewOpen()"
        (opened)="previewOpen.set(true)"
        (closed)="previewOpen.set(false)"
      >
        <mat-expansion-panel-header>
          <mat-panel-title>Preview with sample values</mat-panel-title>
        </mat-expansion-panel-header>
        @if (previewError(); as error) {
          <p class="prompt-editor__error" role="alert">The preview failed: {{ error }}</p>
        } @else if (preview() === null) {
          <mat-progress-spinner mode="indeterminate" diameter="20" />
        } @else {
          <pre class="prompt-editor__pre" data-role="preview">{{ preview() }}</pre>
        }
      </mat-expansion-panel>

      <div class="prompt-editor__actions">
        <button
          mat-stroked-button
          type="button"
          data-action="reset-prompt"
          [disabled]="!resettable() || saving()"
          (click)="reset()"
        >
          <mat-icon>restart_alt</mat-icon>
          Reset to default
        </button>
        @if (entry().isOverridden) {
          <span class="prompt-editor__note">A stored override is in use.</span>
        } @else {
          <span class="prompt-editor__note">The built-in default is in use.</span>
        }
      </div>

      @if (error(); as error) {
        <p class="prompt-editor__error" role="alert">{{ error }}</p>
      }
    </r2m-config-editor-frame>
  `,
  styles: `
    :host {
      display: block;
      min-width: 0;
    }
    .prompt-editor__description {
      margin: 0 0 var(--r2m-space-3);
      color: var(--r2m-text-muted);
    }
    .prompt-editor__warning {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-2);
      margin: 0 0 var(--r2m-space-3);
      padding: var(--r2m-space-2) var(--r2m-space-3);
      border-radius: var(--r2m-radius-md);
      background: color-mix(in srgb, var(--r2m-status-warn) 15%, transparent);
      color: var(--r2m-status-warn);
      font-size: var(--r2m-text-sm);
    }
    .prompt-editor__label {
      display: block;
      margin-bottom: var(--r2m-space-1);
      color: var(--r2m-text-muted);
      font-size: var(--r2m-text-sm);
    }
    .prompt-editor__tokens {
      display: flex;
      flex-wrap: wrap;
      gap: var(--r2m-space-1);
      margin-bottom: var(--r2m-space-3);
    }
    .prompt-editor__tokens .prompt-editor__label {
      flex-basis: 100%;
    }
    .prompt-editor__token {
      padding: 2px var(--r2m-space-2);
      border: 1px solid var(--r2m-outline);
      border-radius: 999px;
      background: transparent;
      color: inherit;
      font-family: var(--r2m-font-mono, monospace);
      font-size: var(--r2m-text-sm);
      cursor: pointer;
    }
    .prompt-editor__token:hover:not(:disabled) {
      background: var(--r2m-surface-high);
    }
    .prompt-editor__field {
      width: 100%;
    }
    .prompt-editor__template {
      font-family: var(--r2m-font-mono, monospace);
      font-size: var(--r2m-text-sm);
      line-height: 1.45;
    }
    .prompt-editor__note {
      margin: var(--r2m-space-1) 0 var(--r2m-space-3);
      color: var(--r2m-text-muted);
      font-size: var(--r2m-text-sm);
    }
    .prompt-editor__expected {
      margin-bottom: var(--r2m-space-3);
    }
    .prompt-editor__pre {
      margin: 0;
      padding: var(--r2m-space-3);
      border: 1px solid var(--r2m-outline);
      border-radius: var(--r2m-radius-md);
      background: var(--r2m-surface-low);
      font-family: var(--r2m-font-mono, monospace);
      font-size: var(--r2m-text-sm);
      white-space: pre-wrap;
      overflow-wrap: anywhere;
    }
    .prompt-editor__preview {
      margin-bottom: var(--r2m-space-3);
    }
    .prompt-editor__actions {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-3);
    }
    .prompt-editor__actions .prompt-editor__note {
      margin: 0;
    }
    .prompt-editor__error {
      margin: var(--r2m-space-3) 0 0;
      color: var(--r2m-status-error);
    }
  `,
})
export class PromptEditor {
  private readonly store = inject(PromptsStore);
  private readonly confirm = inject(ConfirmService);
  private readonly toast = inject(ToastService);

  readonly entry = input.required<PromptCatalogEntry>();

  private readonly textarea = viewChild<ElementRef<HTMLTextAreaElement>>('template');

  protected readonly draft = signal('');
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly previewOpen = signal(false);
  /** Null while a rendering is on its way. */
  protected readonly preview = signal<string | null>(null);
  protected readonly previewError = signal<string | null>(null);
  private previewSeq = 0;
  private previewTimer: ReturnType<typeof setTimeout> | null = null;

  readonly dirty = computed(() => isDirty(this.entry(), this.draft()));
  protected readonly resettable = computed(() => canReset(this.entry(), this.draft()));

  constructor() {
    // A new kind replaces the draft; a reload of the same kind replaces it only while the draft
    // is still the text this editor last showed — a save made in Blazor or over the API must not
    // wipe typing in progress, but must reach an untouched editor.
    let shown: { kind: string; template: string } | null = null;
    effect(() => {
      const entry = this.entry();
      untracked(() => {
        if (entry.kind !== shown?.kind) {
          this.draft.set(entry.template);
          this.error.set(null);
          this.previewOpen.set(false);
          this.preview.set(null);
        } else if (this.draft() === shown.template) {
          this.draft.set(entry.template);
        }
        shown = { kind: entry.kind, template: entry.template };
      });
    });
    // The preview follows the draft while the panel is open, after a short pause in typing.
    effect(() => {
      const open = this.previewOpen();
      const template = this.draft();
      const kind = this.entry().kind;
      untracked(() => this.schedulePreview(open, kind, template));
    });
    inject(DestroyRef).onDestroy(() => this.clearPreviewTimer());
  }

  protected literal(token: string): string {
    return tokenLiteral(token);
  }

  protected insert(token: string): void {
    const element = this.textarea()?.nativeElement;
    const start = element ? element.selectionStart : null;
    const end = element ? element.selectionEnd : null;
    const { text, caret } = insertToken(this.draft(), token, start, end);
    this.draft.set(text);
    if (!element) return;
    // The binding writes the value on the next change detection; place the caret after it.
    queueMicrotask(() => {
      element.value = text;
      element.setSelectionRange(caret, caret);
      element.focus();
    });
  }

  protected async save(): Promise<void> {
    this.saving.set(true);
    this.error.set(null);
    try {
      await this.store.save(this.entry().kind, this.draft());
      this.toast.success(`${this.entry().title} saved`);
    } catch (e) {
      this.error.set(toApiError(e).message);
    } finally {
      this.saving.set(false);
    }
  }

  protected async reset(): Promise<void> {
    const entry = this.entry();
    const message = entry.isOverridden
      ? 'The stored override and any unsaved edits are discarded; the built-in default is used again.'
      : 'Your unsaved edits are discarded.';
    const ok = await this.confirm.confirm({
      title: `Reset ${entry.title} to default?`,
      message,
      confirmLabel: 'Reset',
    });
    if (!ok) return;
    if (!entry.isOverridden) {
      this.draft.set(entry.defaultTemplate);
      return;
    }
    this.saving.set(true);
    this.error.set(null);
    try {
      await this.store.reset(entry.kind);
      this.draft.set(entry.defaultTemplate);
      this.toast.info(`${entry.title} reset to default`);
    } catch (e) {
      this.error.set(toApiError(e).message);
    } finally {
      this.saving.set(false);
    }
  }

  /** The last rendering stays on screen while the next one is on its way — no spinner per keystroke. */
  private schedulePreview(open: boolean, kind: PromptCatalogEntry['kind'], template: string): void {
    this.clearPreviewTimer();
    if (!open) return;
    this.previewError.set(null);
    const seq = ++this.previewSeq;
    this.previewTimer = setTimeout(() => {
      this.previewTimer = null;
      void this.fetchPreview(seq, kind, template);
    }, PREVIEW_DEBOUNCE_MS);
  }

  private async fetchPreview(
    seq: number,
    kind: PromptCatalogEntry['kind'],
    template: string,
  ): Promise<void> {
    try {
      const rendered = await this.store.preview(kind, template);
      if (seq === this.previewSeq) this.preview.set(rendered);
    } catch (e) {
      if (seq === this.previewSeq) this.previewError.set(toApiError(e).message);
    }
  }

  private clearPreviewTimer(): void {
    if (this.previewTimer !== null) clearTimeout(this.previewTimer);
    this.previewTimer = null;
  }
}
