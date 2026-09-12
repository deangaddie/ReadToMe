import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  afterRenderEffect,
  computed,
  input,
  output,
  signal,
  viewChild,
  booleanAttribute,
} from '@angular/core';
import { MatIconModule } from '@angular/material/icon';

/**
 * Click-to-edit text (design §7, §8): Enter saves, Escape cancels, blur saves. Emits `save` only
 * when the value actually changed and is valid (non-empty when `required`, within `maxLength`).
 */
@Component({
  selector: 'r2m-inline-edit',
  imports: [MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'r2m-inline-edit', '[class.r2m-inline-edit--editing]': 'editing()' },
  template: `
    @if (editing()) {
      <input
        #field
        class="r2m-inline-edit__input"
        type="text"
        [value]="draft()"
        [placeholder]="placeholder()"
        [attr.maxlength]="maxLength() ?? null"
        [attr.aria-invalid]="!valid()"
        (input)="draft.set(field.value)"
        (keydown.enter)="commit(); $event.preventDefault()"
        (keydown.escape)="abort(); $event.preventDefault()"
        (blur)="commit()"
      />
    } @else {
      <button
        type="button"
        class="r2m-inline-edit__display"
        [class.r2m-inline-edit__display--empty]="!value()"
        (click)="begin()"
        [attr.aria-label]="'Edit ' + (placeholder() || 'value')"
      >
        <span class="r2m-inline-edit__text">{{ value() || placeholder() }}</span>
        <mat-icon class="r2m-inline-edit__pencil" aria-hidden="true">edit</mat-icon>
      </button>
    }
  `,
  styles: `
    :host {
      display: inline-block;
      max-width: 100%;
    }
    .r2m-inline-edit__display {
      display: inline-flex;
      align-items: center;
      gap: var(--r2m-space-1);
      max-width: 100%;
      padding: 2px var(--r2m-space-1);
      margin: -2px calc(-1 * var(--r2m-space-1));
      border: 0;
      border-radius: var(--r2m-radius-sm);
      background: transparent;
      color: inherit;
      font: inherit;
      text-align: left;
      cursor: text;
    }
    .r2m-inline-edit__display:hover,
    .r2m-inline-edit__display:focus-visible {
      background: var(--r2m-surface-high);
      outline: none;
    }
    .r2m-inline-edit__display--empty .r2m-inline-edit__text {
      color: var(--r2m-text-muted);
      font-style: italic;
    }
    .r2m-inline-edit__text {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .r2m-inline-edit__pencil {
      font-size: 16px;
      width: 16px;
      height: 16px;
      color: var(--r2m-text-muted);
      opacity: 0;
      transition: opacity 100ms;
    }
    .r2m-inline-edit__display:hover .r2m-inline-edit__pencil,
    .r2m-inline-edit__display:focus-visible .r2m-inline-edit__pencil {
      opacity: 1;
    }
    .r2m-inline-edit__input {
      width: 100%;
      padding: 2px var(--r2m-space-1);
      margin: -2px calc(-1 * var(--r2m-space-1));
      border: 1px solid var(--r2m-accent);
      border-radius: var(--r2m-radius-sm);
      background: var(--r2m-surface);
      color: var(--r2m-text);
      font: inherit;
      outline: none;
    }
    .r2m-inline-edit__input[aria-invalid='true'] {
      border-color: var(--r2m-status-error);
    }
  `,
})
export class InlineEdit {
  readonly value = input.required<string>();
  readonly placeholder = input('');
  readonly required = input(false, { transform: booleanAttribute });
  readonly maxLength = input<number>();

  readonly save = output<string>();
  readonly cancelled = output<void>();

  protected readonly editing = signal(false);
  protected readonly draft = signal('');
  private readonly field = viewChild<ElementRef<HTMLInputElement>>('field');

  protected readonly valid = computed(() => {
    const text = this.draft().trim();
    if (this.required() && text.length === 0) return false;
    const max = this.maxLength();
    return max === undefined || text.length <= max;
  });

  constructor() {
    afterRenderEffect(() => {
      if (this.editing()) {
        const el = this.field()?.nativeElement;
        el?.focus();
        el?.select();
      }
    });
  }

  begin(): void {
    this.draft.set(this.value());
    this.editing.set(true);
  }

  commit(): void {
    if (!this.editing()) return;
    const text = this.draft().trim();
    this.editing.set(false);
    if (!this.valid() || text === this.value()) {
      this.cancelled.emit();
      return;
    }
    this.save.emit(text);
  }

  abort(): void {
    if (!this.editing()) return;
    this.editing.set(false);
    this.cancelled.emit();
  }
}
