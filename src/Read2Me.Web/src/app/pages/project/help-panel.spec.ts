import { describe, expect, it } from 'bun:test';
import { render } from 'lit-html';
import { HELP } from '@app/help/help.generated';
import { GUIDE_URL, helpPanel } from './help-panel';

function mount(onClose: () => void = () => {}) {
  const host = document.createElement('div');
  render(
    helpPanel({ id: 'Structure', title: 'Structure', icon: 'call_split' }, onClose, 'side'),
    host,
  );
  return host.firstElementChild as HTMLElement;
}

describe('helpPanel', () => {
  it('heads the step help with its icon and name, closes on ×, and links the guide page on GitHub', () => {
    let closed = 0;
    const panel = mount(() => closed++);
    expect(panel.classList.contains('overview__help')).toBe(true);
    expect(panel.classList.contains('overview__help--side')).toBe(true);
    expect(panel.getAttribute('aria-label')).toBe('Help: Structure');
    const heading = panel.querySelector('.overview__help-title')!;
    expect(heading.textContent).toContain('Structure');
    expect(heading.querySelector('.r2m-icon')?.textContent).toBe('call_split');
    // The r2m-help body renders once, under the panel's own heading: no second title.
    expect(panel.querySelector('.r2m-help[data-step="Structure"] .r2m-help__body')).not.toBeNull();
    expect(panel.querySelector('.r2m-help__title')).toBeNull();
    const body = panel.querySelector('.r2m-help__body')!;
    const markup = [...body.children].map((el) => el.outerHTML).join('\n');
    expect(`${markup}\n`).toBe(HELP.Structure.html);

    const link = panel.querySelector<HTMLAnchorElement>('a.overview__help-guide')!;
    expect(link.textContent).toContain('Open the guide');
    expect(link.getAttribute('href')).toBe(`${GUIDE_URL}/structure.md`);
    expect(link.getAttribute('target')).toBe('_blank');
    expect(link.getAttribute('rel')).toBe('noopener');

    panel.querySelector<HTMLButtonElement>('button[aria-label="Close help"]')!.click();
    expect(closed).toBe(1);
  });
});
