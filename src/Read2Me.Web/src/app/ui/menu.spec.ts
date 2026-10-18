import { describe, expect, it } from 'bun:test';
import { render } from 'lit-html';
import { menuHeading, menuItem } from './menu';

function mount(template: unknown): HTMLElement {
  const host = document.createElement('div');
  render(template, host);
  return host;
}

describe('menuItem', () => {
  it('is a menuitem button with its icon, label and action name', () => {
    let ran = 0;
    const item = mount(
      menuItem({ action: 'add-pauses', label: 'Add pauses', icon: 'pause', run: () => ran++ }),
    ).querySelector<HTMLButtonElement>('button');
    expect(item?.getAttribute('role')).toBe('menuitem');
    expect(item?.getAttribute('type')).toBe('button');
    expect(item?.classList.contains('r2m-menu__item')).toBe(true);
    expect(item?.dataset['action']).toBe('add-pauses');
    expect(item?.querySelector('.r2m-icon')?.textContent).toBe('pause');
    expect(item?.querySelector('.r2m-menu__label')?.textContent).toBe('Add pauses');
    expect(item?.querySelector('.r2m-menu__hint')).toBeNull();
    item?.click();
    expect(ran).toBe(1);
  });

  it('shows a hint as a muted second line', () => {
    const item = mount(
      menuItem({ label: 'Generate audio', hint: 'Assign voices first.', run: () => {} }),
    ).querySelector('button');
    expect(item?.classList.contains('r2m-menu__item--hinted')).toBe(true);
    expect(item?.querySelector('.r2m-menu__hint')?.textContent).toBe('Assign voices first.');
  });

  it('can be destructive or disabled', () => {
    const item = mount(
      menuItem({ label: 'Delete', destructive: true, disabled: true, run: () => {} }),
    ).querySelector('button');
    expect(item?.classList.contains('r2m-menu__item--destructive')).toBe(true);
    expect(item?.disabled).toBe(true);
  });

  it('closes the open popover it sits in before running', () => {
    const order: string[] = [];
    const host = mount(menuItem({ label: 'Go', run: () => order.push('run') }));
    const panel = document.createElement('div');
    panel.setAttribute('popover', '');
    panel.matches = ((s: string) => s === ':popover-open') as unknown as typeof panel.matches;
    panel.hidePopover = () => order.push('hide');
    panel.append(...host.childNodes);
    panel.querySelector('button')?.click();
    expect(order).toEqual(['hide', 'run']);
  });
});

describe('menuHeading', () => {
  it('is a non-interactive label the roving focus skips', () => {
    const heading = mount(menuHeading('Titles')).firstElementChild as HTMLElement;
    expect(heading.classList.contains('r2m-menu__heading')).toBe(true);
    expect(heading.getAttribute('role')).toBe('presentation');
    expect(heading.tagName).not.toBe('BUTTON');
    expect(heading.textContent).toBe('Titles');
  });
});
