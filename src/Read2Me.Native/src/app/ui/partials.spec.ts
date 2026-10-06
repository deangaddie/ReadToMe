import { describe, expect, it } from 'bun:test';
import { html, render } from 'lit-html';
import { countBadge, emptyState, icon, statusChip } from './partials';

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
        icon: 'verified',
        tooltip: 'All set',
        compact: true,
      }),
    ).querySelector('.r2m-status-chip');
    expect(chip?.querySelector('.r2m-icon')?.textContent).toBe('verified');
    expect(chip?.getAttribute('data-tooltip')).toBe('All set');
    expect(chip?.classList.contains('r2m-status-chip--compact')).toBe(true);
  });
});

describe('emptyState', () => {
  it('renders the icon, headline, hint and action', () => {
    const el = mount(
      emptyState(
        { icon: 'inbox', headline: 'No projects', hint: 'Import a book to start.' },
        html`<button>Import</button>`,
      ),
    );
    expect(el.querySelector('.r2m-empty-state__icon')?.textContent).toBe('inbox');
    expect(el.querySelector('.r2m-empty-state__headline')?.textContent).toBe('No projects');
    expect(el.querySelector('.r2m-empty-state__hint')?.textContent).toBe('Import a book to start.');
    expect(el.querySelector('button')?.textContent).toBe('Import');
  });

  it('leaves the hint out when there is none', () => {
    const el = mount(emptyState({ icon: 'inbox', headline: 'Nothing', compact: true }));
    expect(el.querySelector('.r2m-empty-state__hint')).toBeNull();
    expect(el.querySelector('.r2m-empty-state--compact')).not.toBeNull();
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
