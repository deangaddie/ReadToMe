import { describe, expect, it } from 'bun:test';
import { html, render } from 'lit-html';
import {
  adviceLine,
  blockable,
  callout,
  countBadge,
  emptyState,
  icon,
  spinner,
  statusChip,
} from './partials';

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
  });
});

describe('statusChip', () => {
  it('conveys the status by class, default icon and text', () => {
    const chip = mount(statusChip({ status: 'warn', label: 'Needs review' })).querySelector(
      '.r2m-status-chip',
    );
    expect(chip?.classList.contains('r2m-status-chip--warn')).toBe(true);
    expect(chip?.getAttribute('role')).toBe('status');
    expect(chip?.querySelector('.r2m-icon')?.textContent).toBe('warning');
    expect(chip?.querySelector('span:last-child')?.textContent).toBe('Needs review');
    expect(chip?.hasAttribute('data-tooltip')).toBe(false);
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

describe('blockable', () => {
  it('carries the reason as a focusable tooltip while blocked', () => {
    const wrap = mount(blockable('Preview first', html`<button disabled>Apply</button>`))
      .firstElementChild as HTMLElement;
    expect(wrap.classList.contains('r2m-blockable')).toBe(true);
    expect(wrap.getAttribute('data-tooltip')).toBe('Preview first');
    expect(wrap.getAttribute('tabindex')).toBe('0');
    expect(wrap.querySelector('button')?.textContent).toBe('Apply');
  });

  it('is inert and out of the tab order when not blocked', () => {
    const wrap = mount(blockable(null, html`<button>Apply</button>`))
      .firstElementChild as HTMLElement;
    expect(wrap.hasAttribute('data-tooltip')).toBe(false);
    expect(wrap.hasAttribute('tabindex')).toBe(false);
  });

  it('keeps the reason but stays out of the tab order when not tabbable, as a tree node’s trigger', () => {
    const wrap = mount(
      blockable('Viewing a past step', html`<button disabled tabindex="-1">⋮</button>`, {
        tabbable: false,
      }),
    ).firstElementChild as HTMLElement;
    expect(wrap.getAttribute('data-tooltip')).toBe('Viewing a past step');
    expect(wrap.hasAttribute('tabindex')).toBe(false);
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

describe('countBadge', () => {
  it('shows the count with the icon of its kind', () => {
    const badge = mount(countBadge('review', 3, '3 to review')).querySelector('.r2m-count-badge');
    expect(badge?.classList.contains('r2m-count-badge--review')).toBe(true);
    expect(badge?.querySelector('.r2m-count-badge__count')?.textContent).toBe('3');
    expect(badge?.querySelector('.r2m-count-badge__icon')?.textContent).toBe('rate_review');
    expect(badge?.getAttribute('data-tooltip')).toBe('3 to review');
  });

  it('renders nothing at zero unless asked to show it', () => {
    expect(mount(countBadge('audio', 0, 'none')).querySelector('.r2m-count-badge')).toBeNull();
    expect(
      mount(countBadge('audio', 0, 'none', true)).querySelector('.r2m-count-badge__count')
        ?.textContent,
    ).toBe('0');
  });
});
