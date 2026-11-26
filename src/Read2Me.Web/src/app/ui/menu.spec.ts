import { describe, expect, it } from 'bun:test';
import { html, render } from 'lit-html';
import { menuHeading, menuItem, menuKeys } from './menu';

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

describe('menuKeys', () => {
  function menu(): HTMLElement {
    const host = mount(
      html`<button type="button" class="trigger">More</button>
        <div popover role="menu" @keydown=${menuKeys.keydown} @toggle=${menuKeys.toggle}>
          ${menuHeading('Group')} ${menuItem({ label: 'One', action: 'one', run: () => {} })}
          ${menuItem({ label: 'Two', action: 'two', disabled: true, run: () => {} })}
          <hr class="r2m-menu__divider" />
          ${menuItem({ label: 'Three', action: 'three', run: () => {} })}
          ${menuItem({ label: 'Four', action: 'four', run: () => {} })}
        </div>`,
    );
    document.body.append(host);
    return host;
  }
  const item = (host: HTMLElement, action: string) =>
    host.querySelector<HTMLButtonElement>(`[data-action="${action}"]`)!;
  const panel = (host: HTMLElement) => host.querySelector<HTMLElement>('[role="menu"]')!;
  function open(host: HTMLElement): void {
    const event = new Event('toggle');
    Object.assign(event, { newState: 'open', oldState: 'closed' });
    panel(host).dispatchEvent(event);
  }
  function press(key: string): KeyboardEvent {
    const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true });
    (document.activeElement ?? document.body).dispatchEvent(event);
    return event;
  }
  function keyboardOpen(host: HTMLElement): void {
    const trigger = host.querySelector<HTMLButtonElement>('.trigger')!;
    trigger.focus();
    trigger.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    open(host);
  }

  it('focuses the first enabled item when Enter or Space on the trigger opens it', () => {
    for (const key of ['Enter', ' ']) {
      const host = menu();
      const trigger = host.querySelector<HTMLButtonElement>('.trigger')!;
      trigger.focus();
      trigger.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true }));
      open(host);
      expect(document.activeElement).toBe(item(host, 'one'));
      host.remove();
    }
  });

  it('leaves focus where it is on a pointer open', () => {
    const host = menu();
    const trigger = host.querySelector<HTMLButtonElement>('.trigger')!;
    trigger.focus();
    trigger.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    trigger.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }));
    open(host);
    expect(document.activeElement).toBe(trigger);
    host.remove();
  });

  it('keeps one item in the tab order once open', () => {
    const host = menu();
    open(host);
    const tabbable = ['one', 'two', 'three', 'four'].map((a) => item(host, a).tabIndex);
    expect(tabbable).toEqual([0, -1, -1, -1]);
    host.remove();
  });

  it('moves with Up and Down, skipping disabled items and wrapping', () => {
    const host = menu();
    keyboardOpen(host);
    expect(press('ArrowDown').defaultPrevented).toBe(true);
    expect(document.activeElement).toBe(item(host, 'three'));
    expect(item(host, 'three').tabIndex).toBe(0);
    expect(item(host, 'one').tabIndex).toBe(-1);
    press('ArrowDown');
    expect(document.activeElement).toBe(item(host, 'four'));
    press('ArrowDown');
    expect(document.activeElement).toBe(item(host, 'one'));
    press('ArrowUp');
    expect(document.activeElement).toBe(item(host, 'four'));
    press('ArrowUp');
    press('ArrowUp');
    expect(document.activeElement).toBe(item(host, 'one'));
    host.remove();
  });

  it('goes to the first and last enabled items with Home and End', () => {
    const host = menu();
    keyboardOpen(host);
    press('End');
    expect(document.activeElement).toBe(item(host, 'four'));
    press('Home');
    expect(document.activeElement).toBe(item(host, 'one'));
    host.remove();
  });

  it('leaves other keys alone, so Tab leaves the menu', () => {
    const host = menu();
    keyboardOpen(host);
    expect(press('Tab').defaultPrevented).toBe(false);
    expect(press('ArrowRight').defaultPrevented).toBe(false);
    expect(document.activeElement).toBe(item(host, 'one'));
    host.remove();
  });

  it('counts menuitemradio entries as items', () => {
    const host = mount(
      html`<div popover role="menu" @keydown=${menuKeys.keydown} @toggle=${menuKeys.toggle}>
        <button type="button" role="menuitemradio" aria-checked="true" data-action="a">A</button>
        <button type="button" role="menuitemradio" aria-checked="false" data-action="b">B</button>
      </div>`,
    );
    document.body.append(host);
    item(host, 'a').focus();
    open(host);
    press('ArrowDown');
    expect(document.activeElement).toBe(item(host, 'b'));
    host.remove();
  });
});
