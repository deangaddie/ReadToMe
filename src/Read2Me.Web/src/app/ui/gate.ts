/**
 * The **Edit gate** (context/web.md) on screen. A control blocked without a **Blocked reason** is
 * plain `disabled`. With one it stays focusable, so a keyboard or screen reader user can land on it
 * and learn why: `aria-disabled="true"`, the reason as its description (`aria-describedby` at a
 * visually hidden node) and as its tooltip. ARIA only says it is off, so the one capturing guard
 * below refuses what would run it or change its value.
 */
import type { Block } from '@app/api';
import { html, nothing } from 'lit-html';
import {
  Directive,
  type DirectiveResult,
  type ElementPart,
  directive,
} from 'lit-html/directive.js';

/** What `gated` hands a control to render itself behind its gate. */
export interface Gate {
  /** Blocked, with or without a reason: for what else the control turns off with it. */
  readonly blocked: boolean;
  /** The Blocked reason; null while open, or blocked without one. */
  readonly reason: string | null;
  /**
   * The element binding that puts the gate on a control: `<button ${gate.attrs()}>`. It is the
   * control's only source of `disabled`, and of its tooltip: pass the control's own as `tip`, and
   * the reason takes its place. It is the control's only source of `aria-describedby` too: pass
   * the id of its own description (an error on screen) as `describedBy`, read after the reason.
   */
  attrs(tip?: string, describedBy?: string): DirectiveResult;
  /** The binding for what surrounds a control (its label), so hovering that shows the reason too. */
  hover(): DirectiveResult;
}

/** One rendered gate. `id` names its description, set before the bindings that read it commit. */
interface Rendering {
  readonly block: Block | null;
  readonly tabbable: boolean;
  id: string;
}

/** Inputs that hold no text to read, so `readonly` means nothing to them. */
const NOT_TEXT = ['button', 'checkbox', 'color', 'file', 'radio', 'range', 'reset', 'submit'];

const isTextField = (el: Element) =>
  el instanceof HTMLTextAreaElement ||
  (el instanceof HTMLInputElement && !NOT_TEXT.includes(el.type));

class GateBinding extends Directive {
  /** The attributes the gate put on the element: it removes no others. */
  readonly #added = new Set<string>();

  render(_gate: Rendering, _on: 'control' | 'surround', _tip?: string, _describedBy?: string) {
    return nothing;
  }

  override update(
    part: ElementPart,
    [gate, on, tip, describedBy]: Parameters<GateBinding['render']>,
  ) {
    const el = part.element;
    const reason = gate.block?.reason ?? null;
    const reasoned = reason !== null;
    const description = [reasoned ? gate.id : null, describedBy].filter(Boolean).join(' ');
    const wanted: Record<string, string | undefined> =
      on === 'surround'
        ? { 'data-tooltip': reason ?? undefined }
        : {
            disabled: gate.block !== null && !reasoned ? '' : undefined,
            'aria-disabled': reasoned ? 'true' : undefined,
            'aria-describedby': description || undefined,
            'data-tooltip': reason ?? tip,
            readonly: reasoned && isTextField(el) ? '' : undefined,
            tabindex:
              reasoned &&
              !gate.tabbable &&
              (this.#added.has('tabindex') || !el.hasAttribute('tabindex'))
                ? '-1'
                : undefined,
          };
    for (const [name, value] of Object.entries(wanted)) {
      if (value !== undefined) {
        el.setAttribute(name, value);
        this.#added.add(name);
      } else if (this.#added.delete(name)) {
        el.removeAttribute(name);
      }
    }
    return nothing;
  }
}
const gateBinding = directive(GateBinding);

let nextId = 0;

/**
 * The control followed by the description its gate gives it, one id for as long as it is rendered.
 * `aria-hidden` keeps the reason out of the name of what surrounds the control (a tree row, named
 * from its content); `aria-describedby` reads a hidden node all the same.
 */
class Described extends Directive {
  readonly #id = `r2m-blocked-${nextId++}`;

  render(gate: Rendering, control: unknown) {
    gate.id = this.#id;
    const reason = gate.block?.reason ?? null;
    return html`${control}${
      reason === null
        ? nothing
        : html`<span class="visually-hidden r2m-blocked-reason" id=${this.#id} aria-hidden="true"
            >${reason}</span
          >`
    }`;
  }
}
const described = directive(Described);

/**
 * Renders a control behind its **Edit gate**. `control` is handed the {@link Gate} and binds
 * `gate.attrs()` on the control element itself (on each, for a group such as radios), with no
 * `?disabled` or `data-tooltip` of its own: fold the control's own conditions into `block` with
 * `withLocal` instead. `control` runs at once, so the signals it reads are tracked. The element
 * stays the same through every change of the gate, and keeps focus. A control kept out of the tab
 * order (a tree node's trigger, reached by the tree's own keys) passes `tabbable: false`.
 */
export function gated(
  block: Block | null,
  control: (gate: Gate) => unknown,
  { tabbable = true }: { tabbable?: boolean } = {},
): DirectiveResult {
  const rendering: Rendering = { block, tabbable, id: '' };
  return described(
    rendering,
    control({
      blocked: block !== null,
      reason: block?.reason ?? null,
      attrs: (tip, describedBy) => gateBinding(rendering, 'control', tip, describedBy),
      hover: () => gateBinding(rendering, 'surround'),
    }),
  );
}

// ---- the activation guard ------------------------------------------------------------------------

/** The keys that move a select, a slider or a radio group to another value. */
const VALUE_KEYS = [
  'ArrowDown',
  'ArrowLeft',
  'ArrowRight',
  'ArrowUp',
  'End',
  'Home',
  'PageDown',
  'PageUp',
];

const blockedControl = (e: Event): Element | null =>
  e.target instanceof Element ? e.target.closest('[aria-disabled="true"]') : null;

/** Whether this key would run the control or change its value. Tab, Escape and shortcuts pass. */
function activates(control: Element, e: KeyboardEvent): boolean {
  if (e.key === 'Enter' || e.key === ' ') return true;
  if (e.ctrlKey || e.metaKey) return false;
  if (!control.matches('select, input[type="range"], input[type="radio"]')) return false;
  if (VALUE_KEYS.includes(e.key)) return true;
  // F4 opens a select on Windows, and a single character picks an option by type-ahead.
  return !e.altKey && (e.key === 'F4' || e.key.length === 1);
}

function refuse(e: Event): void {
  e.preventDefault();
  e.stopImmediatePropagation();
}

// On the document, capturing: ahead of every listener on the control or above it.
document.addEventListener('click', (e) => blockedControl(e) && refuse(e), true);
for (const type of ['input', 'change']) {
  document.addEventListener(type, (e) => blockedControl(e) && refuse(e), true);
}
document.addEventListener(
  'keydown',
  (e) => {
    const control = blockedControl(e);
    if (control && activates(control, e)) refuse(e);
  },
  true,
);
