import { afterEach, beforeEach, describe, expect, it, mock, spyOn } from 'bun:test';
import { installTooltips } from './tooltip';

// happy-dom has no popover methods or anchor positioning; where the tip lands is covered in E2E.
const proto = HTMLElement.prototype as unknown as Record<string, unknown>;
let pending: (() => void) | undefined;
let delays: number[];
let tip: HTMLElement;
let open: boolean;

// The listeners go on the document, so install once for the file.
installTooltips();
tip = document.querySelector<HTMLElement>('.r2m-tooltip')!;

beforeEach(() => {
  open = false;
  pending = undefined;
  delays = [];
  proto['showPopover'] = () => (open = true);
  proto['hidePopover'] = () => (open = false);
  spyOn(tip, 'matches').mockImplementation(
    ((selector: string) => selector === ':popover-open' && open) as typeof tip.matches,
  );
  spyOn(globalThis, 'setTimeout').mockImplementation(((run: () => void, ms: number) => {
    pending = run;
    delays.push(ms);
    return 1;
  }) as unknown as typeof setTimeout);
  spyOn(globalThis, 'clearTimeout').mockImplementation(() => {
    pending = undefined;
  });
  document.body.append(tip);
});
afterEach(() => {
  mock.restore();
  delete proto['showPopover'];
  delete proto['hidePopover'];
  document.body.replaceChildren();
});

function target(text: string): HTMLElement {
  const button = document.createElement('button');
  button.dataset['tooltip'] = text;
  button.append(document.createElement('span'));
  document.body.append(button);
  return button;
}

const hover = (el: Element) => el.dispatchEvent(new Event('pointerover', { bubbles: true }));
const elapse = () => {
  const run = pending;
  pending = undefined;
  run?.();
};

describe('installTooltips', () => {
  it('adds one shared tooltip popover to the document', () => {
    expect(tip.getAttribute('role')).toBe('tooltip');
    expect(tip.getAttribute('popover')).toBe('manual');
  });

  it('shows the data-tooltip text after the delay, anchored to the element', () => {
    const button = target('Regenerate audio');
    hover(button);
    expect(open).toBe(false);
    expect(delays).toEqual([400]);
    elapse();
    expect(open).toBe(true);
    expect(tip.textContent).toBe('Regenerate audio');
    expect(button.style.getPropertyValue('anchor-name')).toBe('--r2m-tooltip');
  });

  it('finds the tooltip from a child of the element', () => {
    const button = target('Split here');
    hover(button.firstElementChild!);
    elapse();
    expect(tip.textContent).toBe('Split here');
  });

  it('hides and drops the anchor when the pointer moves off', () => {
    const button = target('Hint');
    hover(button);
    elapse();
    hover(document.body);
    expect(open).toBe(false);
    expect(button.style.getPropertyValue('anchor-name')).toBe('');
  });

  it('moving off before the delay cancels the tooltip', () => {
    hover(target('Hint'));
    hover(document.body);
    elapse();
    expect(open).toBe(false);
  });

  it('moves the anchor when another element is hovered', () => {
    const first = target('First');
    const second = target('Second');
    hover(first);
    elapse();
    hover(second);
    elapse();
    expect(tip.textContent).toBe('Second');
    expect(first.style.getPropertyValue('anchor-name')).toBe('');
    expect(second.style.getPropertyValue('anchor-name')).toBe('--r2m-tooltip');
  });

  it('shows on keyboard focus and hides on blur', () => {
    const button = target('Focus hint');
    button.dispatchEvent(new Event('focusin', { bubbles: true }));
    elapse();
    expect(open).toBe(true);
    button.dispatchEvent(new Event('focusout', { bubbles: true }));
    expect(open).toBe(false);
  });

  it('Escape hides it', () => {
    hover(target('Hint'));
    elapse();
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    expect(open).toBe(false);
  });
});
