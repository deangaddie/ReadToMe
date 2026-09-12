import {
  ChangeDetectionStrategy,
  Component,
  computed,
  input,
  output,
  signal,
  booleanAttribute,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';

export type FileRejectReason = 'type' | 'size';

export interface RejectedFile {
  file: File;
  reason: FileRejectReason;
}

/**
 * Drag-and-drop + button file input with accept/max-size validation (design §7). `accept` takes
 * the same syntax as the native attribute (`.epub,.txt`, `audio/*`, `image/png`). Accepted files
 * go out on `files`, the rest on `rejected` with a reason.
 */
@Component({
  selector: 'r2m-file-drop',
  imports: [MatButtonModule, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'r2m-file-drop',
    '[class.r2m-file-drop--over]': 'over()',
    '(dragover)': 'onDragOver($event)',
    '(dragleave)': 'over.set(false)',
    '(drop)': 'onDrop($event)',
  },
  template: `
    <mat-icon class="r2m-file-drop__icon" aria-hidden="true">upload_file</mat-icon>
    <p class="r2m-file-drop__label">{{ label() }}</p>
    @if (hint()) {
      <p class="r2m-file-drop__hint">{{ hint() }}</p>
    }
    <button mat-stroked-button type="button" (click)="picker.click()">
      {{ multiple() ? 'Choose files' : 'Choose file' }}
    </button>
    <input
      #picker
      class="r2m-file-drop__input"
      type="file"
      [accept]="accept()"
      [multiple]="multiple()"
      (change)="onPicked(picker)"
      tabindex="-1"
      aria-hidden="true"
    />
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      align-items: center;
      gap: var(--r2m-space-2);
      padding: var(--r2m-space-6) var(--r2m-space-4);
      border: 2px dashed var(--r2m-outline);
      border-radius: var(--r2m-radius-lg);
      background: var(--r2m-surface-low);
      color: var(--r2m-text-muted);
      text-align: center;
      transition:
        border-color 100ms,
        background 100ms;
    }
    :host(.r2m-file-drop--over) {
      border-color: var(--r2m-accent);
      background: color-mix(in srgb, var(--r2m-accent) 8%, var(--r2m-surface-low));
    }
    .r2m-file-drop__icon {
      font-size: 36px;
      width: 36px;
      height: 36px;
    }
    .r2m-file-drop__label {
      margin: 0;
      font-size: var(--r2m-text-md);
      font-weight: 500;
      color: var(--r2m-text);
    }
    .r2m-file-drop__hint {
      margin: 0;
      font-size: var(--r2m-text-sm);
    }
    .r2m-file-drop__input {
      display: none;
    }
  `,
})
export class FileDrop {
  /** Native `accept` syntax: comma-separated extensions and/or MIME types (wildcards allowed). */
  readonly accept = input('');
  readonly maxBytes = input<number>();
  readonly multiple = input(false, { transform: booleanAttribute });
  readonly label = input('Drop a file here');
  readonly hint = input<string>();

  readonly files = output<File[]>();
  readonly rejected = output<RejectedFile[]>();

  protected readonly over = signal(false);

  private readonly rules = computed(() =>
    this.accept()
      .split(',')
      .map((r) => r.trim().toLowerCase())
      .filter((r) => r.length > 0),
  );

  onDragOver(event: DragEvent): void {
    event.preventDefault();
    this.over.set(true);
  }

  onDrop(event: DragEvent): void {
    event.preventDefault();
    this.over.set(false);
    this.take(Array.from(event.dataTransfer?.files ?? []));
  }

  onPicked(picker: HTMLInputElement): void {
    this.take(Array.from(picker.files ?? []));
    picker.value = '';
  }

  private take(all: File[]): void {
    const candidates = this.multiple() ? all : all.slice(0, 1);
    const accepted: File[] = [];
    const rejected: RejectedFile[] = [];
    for (const file of candidates) {
      const reason = this.reject(file);
      if (reason) rejected.push({ file, reason });
      else accepted.push(file);
    }
    if (accepted.length) this.files.emit(accepted);
    if (rejected.length) this.rejected.emit(rejected);
  }

  private reject(file: File): FileRejectReason | null {
    if (!this.matchesAccept(file)) return 'type';
    const max = this.maxBytes();
    if (max !== undefined && file.size > max) return 'size';
    return null;
  }

  private matchesAccept(file: File): boolean {
    const rules = this.rules();
    if (rules.length === 0) return true;
    const name = file.name.toLowerCase();
    const mime = file.type.toLowerCase();
    return rules.some((rule) => {
      if (rule.startsWith('.')) return name.endsWith(rule);
      if (rule.endsWith('/*')) return mime.startsWith(rule.slice(0, -1));
      return mime === rule;
    });
  }
}
