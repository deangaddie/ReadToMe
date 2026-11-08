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

  describe('data-tooltip-overflow (a tip only for cut-off text)', () => {
    /** A one-line title whose content and box widths are stubbed, as happy-dom lays nothing out. */
    function title(text: string, widths: { content: number; box: number }): HTMLElement {
      const span = document.createElement('span');
      span.textContent = text;
      span.dataset['tooltip'] = text;
      span.dataset['tooltipOverflow'] = '';
      Object.defineProperty(span, 'scrollWidth', { get: () => widths.content });
      Object.defineProperty(span, 'clientWidth', { get: () => widths.box });
      document.body.append(span);
      return span;
    }

    it('shows the tip when the text is wider than its box', () => {
      const span = title('CHAPTER III. THE LAURISTON GARDENS MYSTERY', { content: 320, box: 180 });
      hover(span);
      elapse();
      expect(open).toBe(true);
      expect(tip.textContent).toBe('CHAPTER III. THE LAURISTON GARDENS MYSTERY');
      expect(span.style.getPropertyValue('anchor-name')).toBe('--r2m-tooltip');
    });

    it('stays silent when the text fits', () => {
      const span = title('PART I.', { content: 60, box: 180 });
      hover(span);
      elapse();
      expect(open).toBe(false);
      expect(span.style.getPropertyValue('anchor-name')).toBe('');
    });

    it('measures at show time, once per hover', () => {
      const widths = { content: 60, box: 180 };
      const span = title('CHAPTER I. MR. SHERLOCK HOLMES.', widths);
      hover(span);
      widths.box = 40;
      elapse();
      expect(open).toBe(true);
    });

    it('a fitting title hides a tip left open by another element', () => {
      hover(target('Split here'));
      elapse();
      hover(title('PART I.', { content: 60, box: 180 }));
      elapse();
      expect(open).toBe(false);
    });
  });

  describe('data-tooltip-delegate (a focusable element whose tip is on a descendant)', () => {
    function row(widths: { content: number; box: number }): {
      row: HTMLElement;
      span: HTMLElement;
    } {
      const item = document.createElement('div');
      item.tabIndex = 0;
      item.dataset['tooltipDelegate'] = '.title';
      const span = document.createElement('span');
      span.className = 'title';
      span.dataset['tooltip'] = 'CHAPTER II. THE SCIENCE OF DEDUCTION.';
      span.dataset['tooltipOverflow'] = '';
      Object.defineProperty(span, 'scrollWidth', { get: () => widths.content });
      Object.defineProperty(span, 'clientWidth', { get: () => widths.box });
      item.append(span);
      document.body.append(item);
      return { row: item, span };
    }

    it('focusing the element shows the descendant tip, anchored to the descendant', () => {
      const { row: item, span } = row({ content: 300, box: 120 });
      item.dispatchEvent(new Event('focusin', { bubbles: true }));
      elapse();
      expect(open).toBe(true);
      expect(tip.textContent).toBe('CHAPTER II. THE SCIENCE OF DEDUCTION.');
      expect(span.style.getPropertyValue('anchor-name')).toBe('--r2m-tooltip');
    });

    it('keeps the overflow gate: a fitting descendant shows nothing on focus', () => {
      const { row: item } = row({ content: 100, box: 120 });
      item.dispatchEvent(new Event('focusin', { bubbles: true }));
      elapse();
      expect(open).toBe(false);
    });
  });

  it('Escape hides it', () => {
    hover(target('Hint'));
    elapse();
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    expect(open).toBe(false);
  });
});
