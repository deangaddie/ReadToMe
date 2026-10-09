import { html, nothing } from 'lit-html';

/**
 * What the control inside a {@link field} binds:
 * `id=${a.id} aria-describedby=${a.describedBy} aria-invalid=${a.invalid}`.
 */
export interface FieldControlAttrs {
  id: string;
  /** The id of the hint or error on screen, or `nothing` so the attribute is left off. */
  describedBy: string | typeof nothing;
  /** `'true'` while an error is shown, else `nothing`. */
  invalid: 'true' | typeof nothing;
}

export interface FieldOptions {
  /** The control's id; the label's `for` and the hint's and error's ids derive from it. */
  id: string;
  label: string;
  /** mat-hint: shown under the control while there is no error. */
  hint?: unknown;
  /** mat-error: replaces the hint and marks the control invalid. */
  error?: string | null;
  /** A label beside the control instead of above it (`.r2m-field--inline`). */
  inline?: boolean;
  cls?: string;
}

/**
 * The form-field primitive (spec §5.3): a label stacked above a native control with an optional
 * hint or error under it. A partial because it wires ids: the label's `for`, and the control's
 * `aria-describedby` and `aria-invalid`, which the caller binds from the attrs it is handed, so a
 * control is never described by text that is not on screen.
 */
export function field(o: FieldOptions, control: (attrs: FieldControlAttrs) => unknown) {
  const errorId = o.error ? `${o.id}-error` : null;
  const hintId = !errorId && o.hint ? `${o.id}-hint` : null;
  const describedBy = errorId ?? hintId ?? nothing;
  return html`<div class="r2m-field ${o.inline ? 'r2m-field--inline' : ''} ${o.cls ?? ''}">
    <label class="r2m-field__label" for=${o.id}>${o.label}</label>
    ${control({ id: o.id, describedBy, invalid: errorId ? 'true' : nothing })}
    ${
      errorId
        ? html`<p class="r2m-field__error" id=${errorId} role="alert">${o.error}</p>`
        : hintId
          ? html`<p class="r2m-field__hint" id=${hintId}>${o.hint}</p>`
          : nothing
    }
  </div>`;
}
