import { afterEach, beforeEach, describe, expect, it, mock, spyOn } from 'bun:test';
import { html, render } from 'lit-html';
import { installTooltips, revealTip, withTip } from './tooltip';
import { description, hasDescription } from '../../testing/description';

// happy-dom has no popover methods or anchor positioning; where the tip lands is covered in E2E.
const proto = HTMLElement.prototype as unknown as Record<string, unknown>;
let pending: Map<number, () => void>;
let nextId: number;
let delays: number[];
let tip: HTMLElement;
let open: boolean;
let hovered: boolean;

// The listeners go on the document, so install once for the file.
installTooltips();
tip = document.querySelector<HTMLElement>('.r2m-tooltip')!;

beforeEach(() => {
  open = false;
  hovered = false;
  pending = new Map();
  nextId = 1;
  delays = [];
  proto['showPopover'] = () => (open = true);
  proto['hidePopover'] = () => (open = false);
  spyOn(tip, 'matches').mockImplementation(
    ((selector: string) =>
      (selector === ':popover-open' && open) ||
      (selector === ':hover' && hovered)) as typeof tip.matches,
  );
  spyOn(globalThis, 'setTimeout').mockImplementation(((run: () => void, ms: number) => {
    const id = nextId++;
    pending.set(id, run);
    delays.push(ms);
    return id;
  }) as unknown as typeof setTimeout);
  spyOn(globalThis, 'clearTimeout').mockImplementation(((id: number | undefined) => {
    if (id !== undefined) pending.delete(id);
  }) as typeof clearTimeout);
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
/** Runs every timer pending now; ones they set wait for the next call. */
const elapse = () => {
  const runs = [...pending.values()];
  pending.clear();
  for (const run of runs) run();
};

/** A one-line text opted into the overflow gate, its widths stubbed as happy-dom lays nothing out. */
function overflowText(text: string, widths: { content: number; box: number }): HTMLElement {
  const span = document.createElement('span');
  span.textContent = text;
  span.dataset['tooltip'] = text;
  span.dataset['tooltipOverflow'] = '';
  Object.defineProperty(span, 'scrollWidth', { get: () => widths.content });
  Object.defineProperty(span, 'clientWidth', { get: () => widths.box });
  return span;
}

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

  it('hides and drops the anchor after the hide delay once the pointer moves off', () => {
    const button = target('Hint');
    hover(button);
    elapse();
    hover(document.body);
    expect(open).toBe(true);
    expect(delays.at(-1)).toBe(200);
    elapse();
    expect(open).toBe(false);
    expect(button.style.getPropertyValue('anchor-name')).toBe('');
  });

  describe('hoverable (WCAG 1.4.13)', () => {
    it('stays open while the pointer moves from the trigger onto the tip', () => {
      const button = target('Blocked: a job is running on this chapter');
      hover(button);
      elapse();
      hover(document.body); // the gap between trigger and tip
      hover(tip);
      elapse();
      expect(open).toBe(true);
      expect(button.style.getPropertyValue('anchor-name')).toBe('--r2m-tooltip');
    });

    it('a child of the tip holds it too', () => {
      hover(target('Hint'));
      elapse();
      const inner = document.createElement('span');
      tip.append(inner);
      hover(document.body);
      hover(inner);
      elapse();
      expect(open).toBe(true);
    });

    it('hides after the delay once the pointer leaves the tip', () => {
      hover(target('Hint'));
      elapse();
      hover(tip);
      hover(document.body);
      expect(open).toBe(true);
      elapse();
      expect(open).toBe(false);
    });

    it('returning to the trigger cancels the hide', () => {
      const button = target('Hint');
      hover(button);
      elapse();
      hover(document.body);
      hover(button);
      elapse();
      expect(open).toBe(true);
    });

    it('entering the tip cancels a pending switch to another trigger it passed over', () => {
      const first = target('First');
      const second = target('Second');
      hover(first);
      elapse();
      hover(second);
      hover(tip);
      elapse();
      expect(tip.textContent).toBe('First');
      expect(first.style.getPropertyValue('anchor-name')).toBe('--r2m-tooltip');
    });

    it('hovering a closed tip does nothing', () => {
      hover(tip);
      elapse();
      expect(open).toBe(false);
    });

    it('a blur while the pointer is on the tip keeps it, so a click on it doesn\x27t close it', () => {
      const button = target('Hint');
      button.dispatchEvent(new Event('focusin', { bubbles: true }));
      elapse();
      hovered = true;
      button.dispatchEvent(new Event('focusout', { bubbles: true }));
      expect(open).toBe(true);
    });

    it('Escape still hides it at once while hovered', () => {
      hover(target('Hint'));
      elapse();
      hover(tip);
      document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
      expect(open).toBe(false);
    });
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
    function title(text: string, widths: { content: number; box: number }): HTMLElement {
      return document.body.appendChild(overflowText(text, widths));
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

    it('a fitting label inside a trigger with its own tip shows that tip instead', () => {
      const button = target('Assign a speaker first');
      const label = overflowText('Generate audio', { content: 90, box: 180 });
      button.append(label);
      hover(label);
      elapse();
      expect(open).toBe(true);
      expect(tip.textContent).toBe('Assign a speaker first');
      expect(button.style.getPropertyValue('anchor-name')).toBe('--r2m-tooltip');
    });

    it('moving between that label and its trigger leaves the open tip as it is', () => {
      const button = target('Assign a speaker first');
      const label = overflowText('Generate audio', { content: 90, box: 180 });
      button.append(label);
      hover(label);
      elapse();
      hover(button);
      expect(pending.size).toBe(0);
      hover(label);
      let reopened = false;
      proto['showPopover'] = () => (reopened = true);
      elapse();
      expect(open).toBe(true);
      expect(reopened).toBe(false);
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
      const span = overflowText('CHAPTER II. THE SCIENCE OF DEDUCTION.', widths);
      span.className = 'title';
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

    it('focusing a control inside the element with no tip of its own shows the descendant tip', () => {
      const { row: item } = row({ content: 300, box: 120 });
      const link = document.createElement('a');
      link.href = '#';
      item.append(link);
      link.dispatchEvent(new Event('focusin', { bubbles: true }));
      elapse();
      expect(open).toBe(true);
      expect(tip.textContent).toBe('CHAPTER II. THE SCIENCE OF DEDUCTION.');
    });

    it('a control inside the element with a tip of its own keeps it', () => {
      const { row: item } = row({ content: 300, box: 120 });
      const remove = document.createElement('button');
      remove.dataset['tooltip'] = 'Delete';
      item.append(remove);
      remove.dispatchEvent(new Event('focusin', { bubbles: true }));
      elapse();
      expect(tip.textContent).toBe('Delete');
    });

    it('on an element with a tip of its own, a cut descendant wins and a fitting one gives way', () => {
      const widths = { content: 300, box: 120 };
      const { row: item } = row(widths);
      item.dataset['tooltip'] = 'Assign a speaker first';
      item.dispatchEvent(new Event('focusin', { bubbles: true }));
      elapse();
      expect(tip.textContent).toBe('CHAPTER II. THE SCIENCE OF DEDUCTION.');
      widths.content = 100;
      item.dispatchEvent(new Event('focusin', { bubbles: true }));
      elapse();
      expect(tip.textContent).toBe('Assign a speaker first');
    });
  });

  describe('revealTip (a highlight that does not move focus: aria-activedescendant)', () => {
    it('shows the element\x27s tip after the delay, keeping the overflow gate', () => {
      const cut = document.body.appendChild(
        overflowText('Don Quixote of La Mancha Knight of the Sorrowful Countenance', {
          content: 300,
          box: 120,
        }),
      );
      revealTip(cut);
      elapse();
      expect(open).toBe(true);
      expect(tip.textContent).toBe('Don Quixote of La Mancha Knight of the Sorrowful Countenance');

      revealTip(document.body.appendChild(overflowText('Sancho', { content: 50, box: 120 })));
      elapse();
      expect(open).toBe(false);
    });
  });

  describe('touch (a tap is the hover)', () => {
    const tap = (el: Element) =>
      el.dispatchEvent(new PointerEvent('pointerover', { bubbles: true, pointerType: 'touch' }));

    it('a tip opened by a tap stays when the tap moves focus away (a row link opening its page)', () => {
      const button = target('Don Quixote of La Mancha Knight of the Sorrowful Countenance');
      tap(button);
      button.dispatchEvent(new Event('focusin', { bubbles: true }));
      button.dispatchEvent(new Event('focusout', { bubbles: true }));
      elapse();
      expect(open).toBe(true);
      button.dispatchEvent(new Event('focusout', { bubbles: true }));
      expect(open).toBe(true);
    });

    it('focus moving into a dialog or menu the tap opened still hides it', () => {
      const button = target('Delete');
      tap(button);
      elapse();
      const menu = document.createElement('div');
      menu.popover = 'auto';
      const item = menu.appendChild(document.createElement('button'));
      document.body.append(menu);
      button.dispatchEvent(new FocusEvent('focusout', { bubbles: true, relatedTarget: item }));
      expect(open).toBe(false);
    });

    it('a tap elsewhere hides it', () => {
      tap(target('Rocinante'));
      elapse();
      tap(document.body);
      elapse();
      expect(open).toBe(false);
    });

    it('focus on another trigger still takes over from it', () => {
      tap(target('Rocinante'));
      elapse();
      const other = target('Dapple');
      other.dispatchEvent(new Event('focusin', { bubbles: true }));
      elapse();
      expect(tip.textContent).toBe('Dapple');
      other.dispatchEvent(new Event('focusout', { bubbles: true }));
      expect(open).toBe(false);
    });

    it('shows nothing for a trigger taken out of the page before the delay ends', () => {
      const button = target('Sancho Panza');
      tap(button);
      button.remove();
      elapse();
      expect(open).toBe(false);
    });

    it('a mouse-opened tip still hides on focus out', () => {
      const button = target('Rocinante');
      button.dispatchEvent(
        new PointerEvent('pointerover', { bubbles: true, pointerType: 'mouse' }),
      );
      elapse();
      button.dispatchEvent(new Event('focusout', { bubbles: true }));
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

describe('withTip (a tip that says more than the name is also the description)', () => {
  const trigger = (text: string | undefined, name?: string) =>
    withTip(
      text,
      name,
      (t) =>
        html`<span class="trigger" data-tooltip=${t.tooltip} aria-describedby=${t.describedBy}
          >${name}${t.description}</span
        >`,
    );

  function mounted(template: unknown): HTMLElement {
    const host = document.body.appendChild(document.createElement('div'));
    render(template, host);
    return host;
  }
  const triggers = (host: HTMLElement) => Array.from(host.querySelectorAll('.trigger'));

  it('describes the trigger with a visually hidden element when the tip differs from the name', () => {
    const el = triggers(mounted(trigger('Needs the GPU', 'GPU')))[0]!;
    expect(el.getAttribute('data-tooltip')).toBe('Needs the GPU');
    expect(description(el)).toBe('Needs the GPU');
    expect(el.querySelector('.visually-hidden')?.id).toBe(el.getAttribute('aria-describedby')!);
  });

  it('describes a tip given no name', () => {
    const el = triggers(mounted(trigger('Needs the GPU')))[0]!;
    expect(description(el)).toBe('Needs the GPU');
  });

  it('adds nothing but the tip when it equals the name', () => {
    const host = mounted(trigger('Activity', 'Activity'));
    expect(triggers(host)[0]!.getAttribute('data-tooltip')).toBe('Activity');
    expect(hasDescription(host)).toBe(false);
  });

  it('adds nothing at all without a tip', () => {
    for (const text of [undefined, '']) {
      const host = mounted(trigger(text, 'GPU'));
      expect(triggers(host)[0]!.hasAttribute('data-tooltip')).toBe(false);
      expect(hasDescription(host)).toBe(false);
    }
  });

  it('gives each trigger its own id, kept across renders', () => {
    const both = (second: string) => [trigger('Same tip', 'A'), trigger(second, 'B')];
    const host = mounted(both('Same tip'));
    const ids = () => triggers(host).map((el) => el.getAttribute('aria-describedby'));
    const first = ids();
    expect(new Set(first).size).toBe(2);

    render(both('A new tip'), host);
    expect(ids()).toEqual(first);
    expect(description(triggers(host)[1]!)).toBe('A new tip');
  });

  it('drops the description once the tip matches the name', () => {
    const host = mounted(trigger('Needs the GPU', 'GPU'));
    render(trigger('GPU', 'GPU'), host);
    expect(hasDescription(host)).toBe(false);
  });
});
