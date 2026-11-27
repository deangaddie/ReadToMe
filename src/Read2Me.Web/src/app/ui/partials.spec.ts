import { afterEach, describe, expect, it } from 'bun:test';
import type { Block } from '@app/api';
import { html, render } from 'lit-html';
import {
  adviceLine,
  gated,
  callout,
  countBadge,
  emptyState,
  icon,
  skeleton,
  spinner,
  statusChip,
} from './partials';
import { description, hasDescription } from '../../testing/description';

function mount(template: unknown): HTMLElement {
  const host = document.createElement('div');
  render(template, host);
  return host;
}

describe('icon', () => {
  it('renders the ligature name, hidden from assistive tech', () => {
    const el = mount(icon('search', 'extra')).querySelector('.r2m-icon');
    expect(el?.textContent).toBe('search');
    expect(el?.getAttribute('aria-hidden')).toBe('true');
    expect(el?.classList.contains('material-symbols-rounded')).toBe(true);
    expect(el?.classList.contains('extra')).toBe(true);
    expect(el?.hasAttribute('role')).toBe(false);
    expect(el?.hasAttribute('aria-label')).toBe(false);
  });

  it('with a label is an image named by it, so the ligature text never names it', () => {
    const el = mount(icon('check_circle', 'extra', 'Done')).querySelector('.r2m-icon');
    expect(el?.getAttribute('role')).toBe('img');
    expect(el?.getAttribute('aria-label')).toBe('Done');
    expect(el?.hasAttribute('aria-hidden')).toBe(false);
    expect(el?.textContent).toBe('check_circle');
    // The ligature stays drawn but hidden, so no snapshot reads it as the image's text.
    expect(el?.querySelector('[aria-hidden="true"]')?.textContent).toBe('check_circle');
    expect(el?.classList.contains('extra')).toBe(true);
  });
});

describe('statusChip', () => {
  it('conveys the status by class, default icon and text', () => {
    const chip = mount(statusChip({ status: 'warn', label: 'Needs review' })).querySelector(
      '.r2m-status-chip',
    );
    expect(chip?.classList.contains('r2m-status-chip--warn')).toBe(true);
    expect(chip?.querySelector('.r2m-icon')?.textContent).toBe('warning');
    expect(chip?.querySelector('span:last-child')?.textContent).toBe('Needs review');
    expect(chip?.hasAttribute('data-tooltip')).toBe(false);
  });

  it('is not a live region, so a list of chips says nothing as it re-renders', () => {
    const host = mount(statusChip({ status: 'busy', label: 'Running' }));
    expect(host.querySelector('[role], [aria-live]')).toBeNull();
  });

  it('takes an icon, a tooltip and the compact form', () => {
    const chip = mount(
      statusChip({
        status: 'ok',
        label: 'Done',
        icon: 'info',
        tooltip: 'All set',
        compact: true,
      }),
    ).querySelector('.r2m-status-chip');
    expect(chip?.querySelector('.r2m-icon')?.textContent).toBe('info');
    expect(chip?.getAttribute('data-tooltip')).toBe('All set');
    expect(chip?.classList.contains('r2m-status-chip--compact')).toBe(true);
  });

  it('is described by a tooltip that differs from its label, which stays its text', () => {
    const host = mount(statusChip({ status: 'info', label: 'GPU', tooltip: 'Needs the GPU' }));
    const chip = host.querySelector('.r2m-status-chip')!;
    expect(description(chip)).toBe('Needs the GPU');
    expect(chip.querySelector('.r2m-status-chip__label')?.textContent).toBe('GPU');
  });

  it('adds no description for a tooltip equal to its label', () => {
    const host = mount(statusChip({ status: 'info', label: 'GPU', tooltip: 'GPU' }));
    expect(hasDescription(host)).toBe(false);
  });
});

describe('emptyState', () => {
  it('renders the icon, headline and hint', () => {
    const el = mount(
      emptyState({ icon: 'search', headline: 'No projects', hint: 'Import a book to start.' }),
    );
    expect(el.querySelector('.r2m-empty-state__icon')?.textContent).toBe('search');
    expect(el.querySelector('.r2m-empty-state__headline')?.textContent).toBe('No projects');
    expect(el.querySelector('.r2m-empty-state__hint')?.textContent).toBe('Import a book to start.');
    expect(el.querySelector('.r2m-empty-state__actions')).toBeNull();
  });

  it('leaves the hint out when there is none', () => {
    const el = mount(emptyState({ icon: 'search', headline: 'Nothing', compact: true }));
    expect(el.querySelector('.r2m-empty-state__hint')).toBeNull();
    expect(el.querySelector('.r2m-empty-state--compact')).not.toBeNull();
  });

  it('renders a run action as a button and an href action as a link, the first filled', () => {
    let ran = 0;
    const el = mount(
      emptyState({ icon: 'search', headline: 'No cast' }, [
        { label: 'Discover characters', icon: 'person_search', run: () => ran++ },
        { label: 'Back to the cast', href: 'cast' },
      ]),
    );
    const [first, second] = el.querySelectorAll<HTMLElement>('.r2m-empty-state__action');
    expect(first?.tagName).toBe('BUTTON');
    expect(first?.getAttribute('type')).toBe('button');
    expect(first?.querySelector('.r2m-icon')?.textContent).toBe('person_search');
    expect(first?.textContent).toContain('Discover characters');
    expect(first?.classList.contains('r2m-button--filled')).toBe(true);
    first?.click();
    expect(ran).toBe(1);
    expect(second?.tagName).toBe('A');
    expect(second?.getAttribute('href')).toBe('cast');
    expect(second?.textContent?.trim()).toBe('Back to the cast');
    expect(second?.classList.contains('r2m-button--filled')).toBe(false);
  });

  it('shows at most two actions', () => {
    const el = mount(
      emptyState({ icon: 'search', headline: 'x' }, [
        { label: 'a', href: 'a' },
        { label: 'b', href: 'b' },
        { label: 'c', href: 'c' },
      ]),
    );
    expect(el.querySelectorAll('.r2m-empty-state__action')).toHaveLength(2);
  });
});

describe('gated', () => {
  const reason = { reason: 'Preview first' };

  /** A host in the document, so the activation guard sees its events. */
  function inDocument(): HTMLElement {
    const host = document.createElement('div');
    document.body.append(host);
    return host;
  }

  /** An Apply button behind `block`, redrawn by `draw`; `ran` counts the clicks that ran it. */
  function apply(block: Block | null, options: { tabbable?: boolean; tip?: string } = {}) {
    const host = inDocument();
    let ran = 0;
    const draw = (next: Block | null) =>
      render(
        gated(
          next,
          (gate) => html`<button ${gate.attrs(options.tip)} @click=${() => ran++}>Apply</button>`,
          options,
        ),
        host,
      );
    draw(block);
    return { host, button: host.querySelector('button')!, draw, ran: () => ran };
  }

  /** What a viewer and assistive technology get from the control. */
  const seen = ({ host, button }: ReturnType<typeof apply>) => ({
    disabled: button.disabled,
    ariaDisabled: button.getAttribute('aria-disabled'),
    description: description(button) ?? null,
    hidden: host.querySelector('.visually-hidden')?.textContent ?? null,
    tooltip: button.getAttribute('data-tooltip'),
    tabindex: button.getAttribute('tabindex'),
  });
  const open = {
    disabled: false,
    ariaDisabled: null,
    description: null,
    hidden: null,
    tooltip: null,
    tabindex: null,
  };
  const reasoned = {
    ...open,
    ariaDisabled: 'true',
    description: 'Preview first',
    hidden: 'Preview first',
    tooltip: 'Preview first',
  };

  /** Whether the guard refused a key pressed on `el`. */
  const refused = (el: Element, key: string, modifiers: KeyboardEventInit = {}) => {
    const event = new KeyboardEvent('keydown', {
      key,
      bubbles: true,
      cancelable: true,
      ...modifiers,
    });
    el.dispatchEvent(event);
    return event.defaultPrevented;
  };

  afterEach(() => document.body.replaceChildren());

  it('renders the control alone and working while open', () => {
    const control = apply(null);
    expect(seen(control)).toEqual(open);
    expect(control.host.children).toHaveLength(1);
    control.button.click();
    expect(control.ran()).toBe(1);
  });

  it('is plain disabled while blocked without a reason', () => {
    expect(seen(apply({ reason: null }))).toEqual({ ...open, disabled: true });
  });

  it('stays focusable while blocked with a reason, which is its description and tooltip', () => {
    const control = apply(reason);
    expect(seen(control)).toEqual(reasoned);
    expect(control.button.textContent).toBe('Apply');
    control.button.focus();
    expect(document.activeElement).toBe(control.button);
  });

  it('keeps the description out of the names of what surrounds the control, as a tree row’s', () => {
    const { host, button } = apply(reason);
    // Hidden from the tree, a node `aria-describedby` names is still read as the description.
    const node = host.querySelector(`#${button.getAttribute('aria-describedby')}`);
    expect(node?.getAttribute('aria-hidden')).toBe('true');
  });

  it('adds no wrapper round the control', () => {
    const { host, button } = apply(reason);
    expect(button.parentElement).toBe(host);
  });

  it('runs nothing on a click while blocked with a reason, not even for a listener above it', () => {
    const control = apply(reason);
    let above = 0;
    control.host.addEventListener('click', () => above++);
    const event = new MouseEvent('click', { bubbles: true, cancelable: true });
    control.button.dispatchEvent(event);
    expect(event.defaultPrevented).toBe(true);
    expect([control.ran(), above]).toEqual([0, 0]);
  });

  it('swallows Enter and Space, and lets Tab and Escape by', () => {
    const { host, button } = apply(reason);
    const heard: string[] = [];
    host.addEventListener('keydown', (e) => heard.push(e.key));
    expect(['Enter', ' ', 'Tab', 'Escape'].map((k) => refused(button, k))).toEqual([
      true,
      true,
      false,
      false,
    ]);
    expect(heard).toEqual(['Tab', 'Escape']);
  });

  it('leaves Enter and Space alone while open', () => {
    const { button } = apply(null);
    expect([refused(button, 'Enter'), refused(button, ' ')]).toEqual([false, false]);
  });

  it('keeps one element through every change of the gate, with the attributes in step', () => {
    const control = apply(null);
    const states = [
      [reason, reasoned],
      [{ reason: null }, { ...open, disabled: true }],
      [reason, reasoned],
      [null, open],
    ] as const;
    for (const [block, expected] of states) {
      control.draw(block);
      expect(control.host.querySelector('button')).toBe(control.button);
      expect(seen(control)).toEqual(expected);
    }
    control.button.click();
    expect(control.ran()).toBe(1);
  });

  it('shows the control’s own tooltip until a reason takes its place', () => {
    const control = apply(null, { tip: 'Apply the edit' });
    expect(seen(control).tooltip).toBe('Apply the edit');
    control.draw(reason);
    expect(seen(control).tooltip).toBe('Preview first');
    control.draw({ reason: null });
    expect(seen(control).tooltip).toBe('Apply the edit');
  });

  it('gives each rendered control a description of its own', () => {
    const button = (text: string) =>
      gated({ reason: text }, (gate) => html`<button ${gate.attrs()}>Apply</button>`);
    const host = mount(html`${button('One')}${button('Two')}`);
    expect(Array.from(host.querySelectorAll('button'), description)).toEqual(['One', 'Two']);
  });

  it('keeps the reason but stays out of the tab order when not tabbable, as a tree node’s trigger', () => {
    const control = apply(reason, { tabbable: false });
    expect(seen(control)).toEqual({ ...reasoned, tabindex: '-1' });
    control.draw(null);
    expect(seen(control)).toEqual(open);
  });

  it('leaves a tabindex the control sets itself', () => {
    const host = document.createElement('div');
    for (const block of [reason, null]) {
      render(
        gated(block, (gate) => html`<button tabindex="-1" ${gate.attrs()}>Apply</button>`, {
          tabbable: false,
        }),
        host,
      );
      expect(host.querySelector('button')?.getAttribute('tabindex')).toBe('-1');
    }
  });

  it('calls the control as it is called itself, so the signals it reads are tracked', () => {
    const told: unknown[] = [];
    for (const block of [null, { reason: null }, reason]) {
      gated(block, (gate) => told.push([gate.blocked, gate.reason]));
    }
    expect(told).toEqual([
      [false, null],
      [true, null],
      [true, 'Preview first'],
    ]);
  });

  it('makes a text field read-only, not disabled, while it gives a reason', () => {
    const host = document.createElement('div');
    const fields = (block: Block | null) => {
      render(
        gated(
          block,
          (gate) =>
            html`<input ${gate.attrs()} /><input type="number" ${gate.attrs()} />
              <textarea ${gate.attrs()}></textarea>`,
        ),
        host,
      );
      return Array.from(host.querySelectorAll<HTMLInputElement>('input, textarea'), (f) => [
        f.readOnly,
        f.disabled,
      ]);
    };
    expect(fields(reason)).toEqual(Array(3).fill([true, false]));
    expect(fields(null)).toEqual(Array(3).fill([false, false]));
  });

  it('lets a blocked text field be read: its caret keys and copy still work', () => {
    const host = inDocument();
    render(
      gated(reason, (gate) => html`<input ${gate.attrs()} />`),
      host,
    );
    const field = host.querySelector('input')!;
    expect([refused(field, 'ArrowLeft'), refused(field, 'c', { ctrlKey: true })]).toEqual([
      false,
      false,
    ]);
    // Enter would submit a form.
    expect(refused(field, 'Enter')).toBe(true);
  });

  it('swallows every key that would change a blocked select, slider or radio', () => {
    const host = inDocument();
    render(
      gated(
        reason,
        (gate) =>
          html`<select ${gate.attrs()}></select
            ><input type="range" ${gate.attrs()} /><input type="radio" ${gate.attrs()} />`,
      ),
      host,
    );
    for (const control of host.querySelectorAll('select, input')) {
      const changing = ['ArrowDown', 'ArrowRight', 'Home', 'PageUp', 'Enter', ' ', 'a'];
      expect(changing.map((k) => refused(control, k))).toEqual(changing.map(() => true));
      expect(refused(control, 'ArrowDown', { altKey: true })).toBe(true);
      // F4 opens a select on Windows; Alt+F4 is the window's.
      expect(refused(control, 'F4')).toBe(true);
      expect(refused(control, 'F4', { altKey: true })).toBe(false);
      expect(['Tab', 'Escape', 'F5'].map((k) => refused(control, k))).toEqual([
        false,
        false,
        false,
      ]);
      expect(refused(control, 'r', { ctrlKey: true })).toBe(false);
    }
  });

  it('keeps a value set on a blocked control from reaching the app', () => {
    const host = inDocument();
    let heard = 0;
    render(
      gated(
        reason,
        (gate) =>
          html`<select ${gate.attrs()} @input=${() => heard++} @change=${() => heard++}></select>`,
      ),
      host,
    );
    const select = host.querySelector('select')!;
    select.dispatchEvent(new Event('input', { bubbles: true }));
    select.dispatchEvent(new Event('change', { bubbles: true }));
    expect(heard).toBe(0);
  });

  it('leaves a blocked checkbox as it was when it or its label is clicked', () => {
    const host = inDocument();
    let changed = 0;
    render(
      gated(
        reason,
        (gate) =>
          html`<label ${gate.hover()}
            ><input type="checkbox" ${gate.attrs()} @change=${() => changed++} /> Narrator
            only</label
          >`,
      ),
      host,
    );
    const box = host.querySelector('input')!;
    box.click();
    host.querySelector('label')!.click();
    expect([box.checked, changed]).toEqual([false, 0]);
  });

  it('shows the reason over what surrounds the control, and stays out of its label', () => {
    const host = document.createElement('div');
    const draw = (block: Block | null) =>
      render(
        gated(
          block,
          (gate) => html`<label ${gate.hover()}><input ${gate.attrs()} />Written form</label>`,
        ),
        host,
      );
    draw(reason);
    const label = host.querySelector('label')!;
    expect(label.getAttribute('data-tooltip')).toBe('Preview first');
    expect(label.hasAttribute('aria-disabled')).toBe(false);
    expect(label.textContent).toBe('Written form');
    draw(null);
    expect(label.hasAttribute('data-tooltip')).toBe(false);
  });
});

describe('callout', () => {
  it('is a banner of its kind with the text and no action', () => {
    const el = mount(callout({ kind: 'warn', text: 'Some chapters have no title.' }))
      .firstElementChild as HTMLElement;
    expect(el.classList.contains('r2m-callout')).toBe(true);
    expect(el.classList.contains('r2m-callout--warn')).toBe(true);
    expect(el.querySelector('.r2m-icon')?.textContent).toBe('warning');
    expect(el.querySelector('.r2m-callout__text')?.textContent).toBe(
      'Some chapters have no title.',
    );
    expect(el.querySelector('button, a')).toBeNull();
  });

  it('runs a button action', () => {
    let ran = 0;
    const el = mount(
      callout({ kind: 'info', text: 'Ready.', action: { label: 'Add titles', run: () => ran++ } }),
    );
    expect(el.querySelector('.r2m-icon')?.textContent).toBe('info');
    const button = el.querySelector<HTMLButtonElement>('button.r2m-callout__action');
    expect(button?.getAttribute('type')).toBe('button');
    expect(button?.textContent?.trim()).toBe('Add titles');
    button?.click();
    expect(ran).toBe(1);
  });

  it('links an href action', () => {
    const el = mount(
      callout({
        kind: 'info',
        text: 'No TTS provider.',
        action: { label: 'Open settings', href: 'settings/providers' },
      }),
    );
    const link = el.querySelector('a.r2m-callout__action');
    expect(link?.getAttribute('href')).toBe('settings/providers');
    expect(link?.textContent?.trim()).toBe('Open settings');
  });
});

describe('adviceLine', () => {
  it('renders a muted paragraph for a sentence', () => {
    const el = mount(adviceLine('Attribute speakers first.')).querySelector('p');
    expect(el?.className).toBe('r2m-advice');
    expect(el?.textContent).toBe('Attribute speakers first.');
  });

  it('renders nothing for null', () => {
    expect(mount(adviceLine(null)).children).toHaveLength(0);
  });
});

describe('spinner', () => {
  it('is an indeterminate progressbar sized by its argument', () => {
    const el = mount(spinner(14, 'x-ring')).querySelector('.r2m-spinner');
    expect(el?.getAttribute('role')).toBe('progressbar');
    expect(el?.classList.contains('x-ring')).toBe(true);
    expect(el?.getAttribute('style')).toContain('--_size: 14px');
  });
});

describe('skeleton', () => {
  it('is one indeterminate progressbar named by its label', () => {
    const host = mount(skeleton('Loading chapter', 'x-skel'));
    const bars = host.querySelectorAll('[role="progressbar"]');
    expect(bars).toHaveLength(1);
    const bar = bars[0]!;
    expect(bar.getAttribute('aria-label')).toBe('Loading chapter');
    expect(bar.hasAttribute('aria-valuenow')).toBe(false);
    expect(bar.classList.contains('r2m-skeleton')).toBe(true);
    expect(bar.classList.contains('x-skel')).toBe(true);
    expect(host.querySelector('[aria-busy]')).toBeNull();
  });

  it('hides its shapes from assistive tech', () => {
    const host = mount(
      skeleton(
        'Reading the cast',
        'x-skel',
        html`<div class="x-line"></div>
          Reading the cast…`,
      ),
    );
    const shapes = host.querySelector('.r2m-skeleton__shapes');
    expect(shapes?.getAttribute('aria-hidden')).toBe('true');
    expect(shapes?.querySelector('.x-line')).not.toBeNull();
    expect(shapes?.textContent).toContain('Reading the cast…');
  });

  it('adds no shapes wrapper when the skeleton is one block', () => {
    const host = mount(skeleton('Loading outputs', 'x-skel'));
    expect(host.querySelector('.r2m-skeleton__shapes')).toBeNull();
  });
});

describe('countBadge', () => {
  it('shows the count with the icon of its kind', () => {
    const badge = mount(countBadge('review', 3, '3 to review')).querySelector('.r2m-count-badge');
    expect(badge?.classList.contains('r2m-count-badge--review')).toBe(true);
    expect(badge?.querySelector('.r2m-count-badge__count')?.textContent).toBe('3');
    expect(badge?.querySelector('.r2m-count-badge__icon')?.textContent).toBe('rate_review');
    expect(badge?.getAttribute('data-tooltip')).toBe('3 to review');
  });

  it('is described by its tooltip, which says what the count is of', () => {
    const host = mount(countBadge('review', 3, 'Paragraphs with audio to review'));
    const badge = host.querySelector('.r2m-count-badge')!;
    expect(description(badge)).toBe('Paragraphs with audio to review');
    expect(badge.querySelector('.r2m-count-badge__count')?.textContent).toBe('3');
  });

  it('adds no description for a tooltip equal to its count', () => {
    const host = mount(countBadge('review', 3, '3'));
    expect(hasDescription(host)).toBe(false);
  });

  it('renders nothing at zero unless asked to show it', () => {
    expect(mount(countBadge('audio', 0, 'none')).querySelector('.r2m-count-badge')).toBeNull();
    expect(
      mount(countBadge('audio', 0, 'none', true)).querySelector('.r2m-count-badge__count')
        ?.textContent,
    ).toBe('0');
  });
});
