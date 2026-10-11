import { afterEach, beforeEach, describe, expect, it, mock, spyOn } from 'bun:test';
import { ToastService } from './toast';

// happy-dom has no popover methods; the top layer itself is the browser's and is covered in E2E.
const proto = HTMLElement.prototype as unknown as Record<string, () => void>;
let timers: { run: () => void; ms: number }[];
let popover: string[];

beforeEach(() => {
  timers = [];
  popover = [];
  proto['showPopover'] = () => popover.push('show');
  proto['hidePopover'] = () => popover.push('hide');
  spyOn(globalThis, 'setTimeout').mockImplementation(((run: () => void, ms: number) => {
    timers.push({ run, ms });
    return 0;
  }) as unknown as typeof setTimeout);
});
afterEach(() => {
  mock.restore();
  delete proto['showPopover'];
  delete proto['hidePopover'];
  document.body.replaceChildren();
});

const toasts = () => Array.from(document.querySelectorAll('.r2m-toast'));

describe('ToastService', () => {
  it('shows a message in a manual popover stack, announced as a status', () => {
    new ToastService().success('Saved');
    const stack = document.querySelector<HTMLElement>('.r2m-toasts');
    expect(stack?.getAttribute('popover')).toBe('manual');
    expect(toasts().map((t) => t.textContent)).toEqual(['Saved']);
    expect(toasts()[0]?.className).toBe('r2m-toast r2m-toast--success');
    expect(toasts()[0]?.getAttribute('role')).toBe('status');
    expect(popover.at(-1)).toBe('show');
  });

  it('announces an error as an alert', () => {
    new ToastService().error('Failed');
    expect(toasts()[0]?.getAttribute('role')).toBe('alert');
    expect(toasts()[0]?.classList.contains('r2m-toast--error')).toBe(true);
  });

  it('stacks messages in one container and re-shows it above newer dialogs', () => {
    const service = new ToastService();
    service.info('one');
    service.warn('two');
    expect(document.querySelectorAll('.r2m-toasts')).toHaveLength(1);
    expect(toasts().map((t) => t.textContent)).toEqual(['one', 'two']);
    expect(popover).toEqual(['hide', 'show', 'hide', 'show']);
  });

  it('keeps warnings and errors up longer than successes', () => {
    const service = new ToastService();
    service.success('a');
    service.info('b');
    service.warn('c');
    service.error('d');
    expect(timers.map((t) => t.ms)).toEqual([3000, 3000, 6000, 6000]);
  });

  it('removes each toast when its time is up and hides the empty stack', () => {
    const service = new ToastService();
    service.info('one');
    service.info('two');
    popover = [];
    timers[0]?.run();
    expect(toasts().map((t) => t.textContent)).toEqual(['two']);
    expect(popover).toEqual([]);
    timers[1]?.run();
    expect(toasts()).toEqual([]);
    expect(popover).toEqual(['hide']);
  });

  it('problem() shows the detail, else the title, else a generic message', () => {
    const service = new ToastService();
    service.problem({ title: 'Conflict', detail: ' Audio is still generating. ' });
    service.problem({ title: 'Conflict' });
    service.problem(null);
    expect(toasts().map((t) => t.textContent)).toEqual([
      'Audio is still generating.',
      'Conflict',
      'Request failed',
    ]);
    expect(toasts().every((t) => t.classList.contains('r2m-toast--error'))).toBe(true);
  });
});
