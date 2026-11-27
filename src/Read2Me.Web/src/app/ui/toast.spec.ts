import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { openDialog } from '@app/core/dialog';
import { FakeTimers } from '../../testing/fake-timers';
import { ToastService } from './toast';

// happy-dom has no popover methods or ariaNotify, so both are stubbed: the top layer, inertness
// and what a screen reader says are the browser's.
const proto = HTMLElement.prototype as unknown as Record<string, () => void>;
let timers: FakeTimers;
let popover: string[];
let announced: { text: string; priority: string | undefined }[];

beforeEach(() => {
  timers = new FakeTimers().install();
  popover = [];
  announced = [];
  proto['showPopover'] = () => popover.push('show');
  proto['hidePopover'] = () => popover.push('hide');
  document.ariaNotify = (text, options) =>
    void announced.push({ text, priority: options?.priority });
});
afterEach(() => {
  timers.restore();
  delete proto['showPopover'];
  delete proto['hidePopover'];
  delete document.ariaNotify;
  document.body.replaceChildren();
});

const toasts = () => Array.from(document.querySelectorAll<HTMLElement>('.r2m-toast'));
const messages = () => toasts().map((t) => t.querySelector('.r2m-toast__text')?.textContent);

describe('ToastService', () => {
  it('shows a message in a manual popover stack, a region named Notifications', () => {
    new ToastService().success('Saved');
    const stack = document.querySelector<HTMLElement>('.r2m-toasts');
    expect(stack?.getAttribute('popover')).toBe('manual');
    expect(stack?.getAttribute('role')).toBe('region');
    expect(stack?.getAttribute('aria-label')).toBe('Notifications');
    expect(messages()).toEqual(['Saved']);
    expect(toasts()[0]?.className).toBe('r2m-toast r2m-toast--success');
    expect(popover.at(-1)).toBe('show');
  });

  it('announces each message once, an error assertively, with no live region of its own', () => {
    const service = new ToastService();
    service.success('Saved');
    service.warn('Slow');
    service.error('Failed');
    expect(announced).toEqual([
      { text: 'Saved', priority: 'normal' },
      { text: 'Slow', priority: 'normal' },
      { text: 'Failed', priority: 'high' },
    ]);
    expect(document.querySelector('[role="status"], [role="alert"], [aria-live]')).toBeNull();
  });

  it('says that a toast with actions has them', () => {
    new ToastService().info('Restored.', [{ label: 'Show in Book', run: () => {} }]);
    expect(announced.map((a) => a.text)).toEqual(['Restored. Actions available']);
  });

  it('stacks messages in one container and re-shows it above newer dialogs', () => {
    const service = new ToastService();
    service.info('one');
    service.warn('two');
    expect(document.querySelectorAll('.r2m-toasts')).toHaveLength(1);
    expect(messages()).toEqual(['one', 'two']);
    expect(popover).toEqual(['hide', 'show', 'hide', 'show']);
  });

  it('keeps warnings and errors up longer than successes', async () => {
    const service = new ToastService();
    service.success('a');
    service.info('b');
    service.warn('c');
    service.error('d');
    await timers.advance(2999);
    expect(messages()).toEqual(['a', 'b', 'c', 'd']);
    await timers.advance(1);
    expect(messages()).toEqual(['c', 'd']);
    await timers.advance(2999);
    expect(messages()).toEqual(['c', 'd']);
    await timers.advance(1);
    expect(messages()).toEqual([]);
  });

  it('removes each toast when its time is up and hides the empty stack', async () => {
    const service = new ToastService();
    service.info('one');
    await timers.advance(1000);
    service.info('two');
    popover = [];
    await timers.advance(2000);
    expect(messages()).toEqual(['two']);
    expect(popover).toEqual([]);
    await timers.advance(1000);
    expect(messages()).toEqual([]);
    expect(popover).toEqual(['hide']);
  });

  it('offers actions as buttons, runs the clicked one and dismisses', () => {
    const ran: string[] = [];
    new ToastService().info('Restored to #2 Edit text.', [
      { label: 'Show in Book', run: () => ran.push('book') },
      { label: 'Undo restore', run: () => ran.push('undo') },
    ]);
    const toast = toasts()[0]!;
    const buttons = Array.from(toast.querySelectorAll<HTMLButtonElement>('.r2m-toast__action'));
    expect(toast.querySelector('.r2m-toast__text')?.textContent).toBe('Restored to #2 Edit text.');
    expect(buttons.map((b) => b.textContent)).toEqual(['Show in Book', 'Undo restore']);

    buttons[1]?.click();

    expect(ran).toEqual(['undo']);
    expect(toasts()).toEqual([]);
    expect(popover.at(-1)).toBe('hide');
  });

  it('keeps a toast with actions up for 12 s', async () => {
    new ToastService().info('Restored.', [{ label: 'Show in Book', run: () => {} }]);
    await timers.advance(11999);
    expect(toasts()).toHaveLength(1);
    await timers.advance(1);
    expect(toasts()).toEqual([]);
  });

  it('gives every toast a Dismiss button that removes it', () => {
    const service = new ToastService();
    service.error('Failed');
    const close = toasts()[0]!.querySelector<HTMLButtonElement>('.r2m-toast__dismiss')!;
    expect(close.type).toBe('button');
    expect(close.getAttribute('aria-label')).toBe('Dismiss');
    expect(close.dataset['tooltip']).toBe('Dismiss');

    close.click();

    expect(toasts()).toEqual([]);
    expect(popover.at(-1)).toBe('hide');
  });

  it('problem() shows the detail, else the title, else a generic message', () => {
    const service = new ToastService();
    service.problem({ title: 'Conflict', detail: ' Audio is still generating. ' });
    service.problem({ title: 'Conflict' });
    service.problem(null);
    expect(messages()).toEqual(['Audio is still generating.', 'Conflict', 'Request failed']);
    expect(toasts().every((t) => t.classList.contains('r2m-toast--error'))).toBe(true);
  });
});

describe('a toast under focus or the pointer', () => {
  const actions = [
    { label: 'Show in Book', run: () => {} },
    { label: 'Undo restore', run: () => {} },
  ];
  const buttonsOf = (toast: HTMLElement) =>
    Array.from(toast.querySelectorAll<HTMLButtonElement>('.r2m-toast__action'));
  const outside = () => document.body.appendChild(document.createElement('button'));
  const pointer = (toast: HTMLElement, type: 'pointerenter' | 'pointerleave') =>
    toast.dispatchEvent(new Event(type));

  it('stays while focus is inside it, then runs out the time it had left', async () => {
    const elsewhere = outside();
    new ToastService().info('Restored.', actions);
    await timers.advance(10000);
    buttonsOf(toasts()[0]!)[0]!.focus();
    await timers.advance(60000);
    expect(toasts()).toHaveLength(1);
    elsewhere.focus();
    await timers.advance(1999);
    expect(toasts()).toHaveLength(1);
    await timers.advance(1);
    expect(toasts()).toEqual([]);
  });

  it('stays paused while focus moves between its own controls', async () => {
    new ToastService().info('Restored.', actions);
    const [first, second] = buttonsOf(toasts()[0]!);
    first!.focus();
    second!.focus();
    await timers.advance(60000);
    expect(toasts()).toHaveLength(1);
  });

  it('stays while the pointer is over it, then runs out the time it had left', async () => {
    new ToastService().success('Saved');
    await timers.advance(1000);
    pointer(toasts()[0]!, 'pointerenter');
    await timers.advance(60000);
    expect(toasts()).toHaveLength(1);
    pointer(toasts()[0]!, 'pointerleave');
    await timers.advance(1999);
    expect(toasts()).toHaveLength(1);
    await timers.advance(1);
    expect(toasts()).toEqual([]);
  });

  it('resumes only once focus and the pointer have both left', async () => {
    const elsewhere = outside();
    new ToastService().info('Restored.', actions);
    const toast = toasts()[0]!;
    pointer(toast, 'pointerenter');
    buttonsOf(toast)[0]!.focus();
    pointer(toast, 'pointerleave');
    await timers.advance(60000);
    expect(toasts()).toHaveLength(1);
    pointer(toast, 'pointerenter');
    elsewhere.focus();
    await timers.advance(60000);
    expect(toasts()).toHaveLength(1);
    pointer(toast, 'pointerleave');
    await timers.advance(12000);
    expect(toasts()).toEqual([]);
  });

  it('pauses only the toast in use', async () => {
    const service = new ToastService();
    service.success('held');
    service.success('free');
    pointer(toasts()[0]!, 'pointerenter');
    await timers.advance(3000);
    expect(messages()).toEqual(['held']);
  });

  const closeOf = (toast: HTMLElement) =>
    toast.querySelector<HTMLButtonElement>('.r2m-toast__dismiss')!;

  it('hands focus to the next toast when dismissed while holding it, then back where it was', () => {
    const elsewhere = outside();
    elsewhere.focus();
    const service = new ToastService();
    service.error('first');
    service.info('second', actions);
    const [first, second] = toasts();

    closeOf(first!).focus();
    closeOf(first!).click();
    expect(document.activeElement).toBe(closeOf(second!));

    closeOf(second!).click();
    expect(document.activeElement).toBe(elsewhere);
  });

  it('goes back where focus was when an action dismisses the last toast, before the action runs', () => {
    const elsewhere = outside();
    elsewhere.focus();
    const focusedInAction: (Element | null)[] = [];
    new ToastService().info('Restored.', [
      { label: 'Show in Book', run: () => focusedInAction.push(document.activeElement) },
    ]);
    const action = buttonsOf(toasts()[0]!)[0]!;
    action.focus();
    action.click();
    expect(focusedInAction).toEqual([elsewhere]);
    expect(document.activeElement).toBe(elsewhere);
  });

  it('forgets where focus was once it has left the stack', () => {
    const elsewhere = outside();
    elsewhere.focus();
    const service = new ToastService();
    service.error('first');
    service.error('second');
    const close = closeOf(toasts()[1]!);
    close.focus();
    close.blur();

    // Back in from nowhere, as by a click on the page and then on Dismiss.
    close.focus();
    close.click();

    expect(document.activeElement).not.toBe(elsewhere);
  });

  it('remembers where focus was while the window is away', () => {
    const elsewhere = outside();
    elsewhere.focus();
    new ToastService().error('Failed');
    const close = closeOf(toasts()[0]!);
    close.focus();
    // A window losing and regaining focus: the events fire, the active element stays.
    close.dispatchEvent(new FocusEvent('focusout', { bubbles: true }));
    close.dispatchEvent(new FocusEvent('focusin', { bubbles: true }));

    close.click();

    expect(document.activeElement).toBe(elsewhere);
  });

  it('starts no new countdown for a toast already dismissed', () => {
    new ToastService().error('Failed');
    const toast = toasts()[0]!;
    closeOf(toast).focus();
    closeOf(toast).click();
    // Chromium fires this as it removes a toast that still holds the focus.
    toast.dispatchEvent(new FocusEvent('focusout', { bubbles: true }));
    expect(timers.pending).toBe(0);
  });

  it('leaves focus alone when the dismissed toast did not hold it', async () => {
    const elsewhere = outside();
    const other = outside();
    const service = new ToastService();
    service.success('first');
    service.error('second');
    closeOf(toasts()[1]!).focus();
    elsewhere.focus();
    other.focus();
    await timers.advance(3000);
    expect(document.activeElement).toBe(other);
    closeOf(toasts()[0]!).click();
    expect(document.activeElement).toBe(other);
  });
});

describe('toasts over a modal dialog', () => {
  /** Opens a modal and returns its `<dialog>`, with the call that closes it. */
  async function modal() {
    const content = document.createElement('div');
    void openDialog(content);
    await Promise.resolve();
    return {
      dialog: content.closest('dialog')!,
      close: () => void content.dispatchEvent(new CustomEvent('r2m-close')),
    };
  }
  const stack = () => document.querySelector<HTMLElement>('.r2m-toasts');
  const home = () => stack()?.parentElement;

  it('mounts the stack in the open modal, and back on the body when it closes', async () => {
    const service = new ToastService();
    service.success('before');
    const { dialog, close } = await modal();
    service.error('Upload failed');
    expect(home()).toBe(dialog);
    expect(messages()).toEqual(['before', 'Upload failed']);

    popover = [];
    close();

    expect(home()).toBe(document.body);
    expect(messages()).toEqual(['before', 'Upload failed']);
    expect(popover.at(-1)).toBe('show');
  });

  it('announces on the modal it mounts in', async () => {
    const targets: Node[] = [];
    Element.prototype.ariaNotify = function (this: Element) {
      targets.push(this);
    };
    try {
      const { dialog } = await modal();
      new ToastService().error('Upload failed');
      expect(targets).toEqual([dialog]);
    } finally {
      delete Element.prototype.ariaNotify;
    }
  });

  it('follows the topmost modal, and falls back to the one under it', async () => {
    const service = new ToastService();
    const under = await modal();
    service.error('one');
    const top = await modal();
    service.error('two');
    expect(home()).toBe(top.dialog);
    top.close();
    expect(home()).toBe(under.dialog);
    under.close();
    expect(home()).toBe(document.body);
  });

  it('stays put when a modal it has left closes', async () => {
    const service = new ToastService();
    const under = await modal();
    service.error('one');
    const top = await modal();
    service.error('two');
    under.close();
    expect(home()).toBe(top.dialog);
  });

  it('does not show an empty stack when the modal closes', async () => {
    const { close } = await modal();
    new ToastService().success('Saved');
    await timers.advance(3000);
    popover = [];
    close();
    expect(home()).toBe(document.body);
    expect(popover).not.toContain('show');
  });

  it('lets a hovered toast run out after the stack moves, which loses the pointer', async () => {
    const { close } = await modal();
    new ToastService().success('Saved');
    toasts()[0]!.dispatchEvent(new Event('pointerenter'));
    close();
    await timers.advance(3000);
    expect(toasts()).toEqual([]);
  });

  it('does not re-show a stack that holds focus, which would drop it', () => {
    const service = new ToastService();
    service.error('one');
    toasts()[0]!.querySelector('button')!.focus();
    popover = [];
    service.error('two');
    expect(popover).not.toContain('hide');
  });
});
