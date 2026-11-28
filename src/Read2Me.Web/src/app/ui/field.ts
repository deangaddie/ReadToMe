import { html, nothing } from 'lit-html';
import {
  Directive,
  type ElementPart,
  type PartInfo,
  PartType,
  directive,
} from 'lit-html/directive.js';
import { announce } from './announcer';

/** The text of the input, select or textarea an event came from. */
export const controlValue = (e: Event): string =>
  (e.target as HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement).value;

/**
 * What the control inside a {@link field} binds:
 * `id=${a.id} aria-describedby=${a.describedBy} aria-invalid=${a.invalid}`.
 */
export interface FieldControlAttrs {
  id: string;
  /** The id of the hint or error on screen, or `nothing` so the attribute is left off. */
  describedBy: string | typeof nothing;
  /** `'true'` while an error is shown, else `'false'`: never unset, which reads as invalid early. */
  invalid: 'true' | 'false';
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
 * control is never described by text that is not on screen. The error is not a live region: it is
 * announced once by {@link announceError}.
 */
export function field(o: FieldOptions, control: (attrs: FieldControlAttrs) => unknown) {
  const errorId = o.error ? `${o.id}-error` : null;
  const hintId = !errorId && o.hint ? `${o.id}-hint` : null;
  const describedBy = errorId ?? hintId ?? nothing;
  return html`<div class="r2m-field ${o.inline ? 'r2m-field--inline' : ''} ${o.cls ?? ''}">
    <label class="r2m-field__label" for=${o.id}>${o.label}</label>
    ${control({ id: o.id, describedBy, invalid: errorId ? 'true' : 'false' })}
    ${
      errorId
        ? html`<p class="r2m-field__error" id=${errorId} ${announceError(o.label)}>${o.error}</p>`
        : hintId
          ? html`<p class="r2m-field__hint" id=${hintId}>${o.hint}</p>`
          : nothing
    }
  </div>`;
}

/** How long an error waits before it is announced, so it doesn't talk over the next field's name. */
export const ERROR_ANNOUNCE_DELAY_MS = 500;

/** The control the user last left (blur) or committed (change), until the task it happened in ends. */
let lastLeft: { target: EventTarget; type: string } | null = null;

function onLeave(e: Event): void {
  if (!e.target) return;
  const left = { target: e.target, type: e.type };
  lastLeft = left;
  setTimeout(() => {
    if (lastLeft === left) lastLeft = null;
  });
}
document.addEventListener('blur', onLeave, true);
document.addEventListener('change', onLeave, true);

class AnnounceErrorDirective extends Directive {
  #seen = false;

  constructor(part: PartInfo) {
    super(part);
    if (part.type !== PartType.ELEMENT) throw new Error('announceError() goes on an element');
  }

  render(_label: string) {
    return nothing;
  }

  override update(part: ElementPart, [label]: [string]) {
    if (this.#seen) return nothing;
    this.#seen = true;
    const error = part.element;
    const left = lastLeft;
    // Only an error the user's leaving or changing its own field brought up: not one a submit
    // showed (focus on the first invalid field reads that), nor one coming back while typing.
    if (!left || !(left.target instanceof Node)) return nothing;
    const target = left.target;
    setTimeout(() => {
      // The row is read now: on first render the error is still in a template fragment.
      const row = error.parentElement;
      if (!error.isConnected || !row?.contains(target)) return;
      // Focus came back to the field it left: focus reads its description already.
      if (left.type === 'blur' && row.contains(document.activeElement)) return;
      const text = error.textContent?.replace(/\s+/g, ' ').trim();
      if (!text) return;
      // "Book title is required" names its field already; "Use #rgb or #rrggbb" does not.
      announce(text.toLowerCase().includes(label.toLowerCase()) ? text : `${label}: ${text}`);
    }, ERROR_ANNOUNCE_DELAY_MS);
    return nothing;
  }
}

/**
 * On a field's error element (`.r2m-field__error`, or a row's own error span): announces the error
 * politely through the shared announcer, once, a moment after it appears because the user left or
 * changed a control in the error's parent row. A change only to the error's text says nothing
 * again; an error that clears and comes back does, on the next blur or change. `label` prefixes
 * the announcement when the error text does not name the field already.
 */
export const announceError = directive(AnnounceErrorDirective);
