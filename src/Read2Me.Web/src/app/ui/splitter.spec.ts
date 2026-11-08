import { afterEach, describe, expect, it } from 'bun:test';
import './splitter';

afterEach(() => document.body.replaceChildren());

async function mount(value = 280) {
  const el = document.createElement('r2m-splitter');
  el.min = 220;
  el.max = 480;
  el.defaultValue = 280;
  el.value = value;
  el.label = 'Resize the structure tree';
  const changes: number[] = [];
  el.addEventListener('value-change', (e) => changes.push((e as CustomEvent<number>).detail));
  document.body.append(el);
  await el.rendered();
  return { el, changes };
}

describe('r2m-splitter', () => {
  it('is a focusable, named separator whose ARIA values follow the value and bounds', async () => {
    const { el } = await mount();

    expect(el.getAttribute('role')).toBe('separator');
    expect(el.tabIndex).toBe(0);
    expect(el.getAttribute('aria-label')).toBe('Resize the structure tree');
    expect(el.getAttribute('aria-orientation')).toBe('vertical');
    expect(el.getAttribute('aria-valuenow')).toBe('280');
    expect(el.getAttribute('aria-valuemin')).toBe('220');
    expect(el.getAttribute('aria-valuemax')).toBe('480');

    el.value = 300;
    el.max = 400;
    await el.rendered();
    expect(el.getAttribute('aria-valuenow')).toBe('300');
    expect(el.getAttribute('aria-valuemax')).toBe('400');
  });

  it('steps 16px on an arrow key and 64px with Shift, announcing each new value', async () => {
    const { el, changes } = await mount();

    press(el, 'ArrowRight');
    press(el, 'ArrowRight', { shiftKey: true });
    press(el, 'ArrowLeft');
    await el.rendered();

    expect(changes).toEqual([296, 360, 344]);
    expect(el.value).toBe(344);
    expect(el.getAttribute('aria-valuenow')).toBe('344');
  });

  it('clamps key steps to the bounds and stays silent once there', async () => {
    const { el, changes } = await mount(460);

    press(el, 'ArrowRight', { shiftKey: true });
    press(el, 'ArrowRight');
    el.value = 230;
    press(el, 'ArrowLeft');
    press(el, 'ArrowLeft');

    expect(changes).toEqual([480, 220]);
  });

  it('jumps to the minimum on Home and the maximum on End', async () => {
    const { el, changes } = await mount();

    press(el, 'End');
    press(el, 'Home');

    expect(changes).toEqual([480, 220]);
    expect(el.value).toBe(220);
  });

  it('resets the default on double-click', async () => {
    const { el, changes } = await mount(400);

    el.dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));
    el.dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));

    expect(changes).toEqual([280]);
    expect(el.value).toBe(280);
  });

  it('follows a captured pointer drag live, clamped, until the pointer is released', async () => {
    const { el, changes } = await mount();
    const captured = stubCapture(el);

    pointer(el, 'pointerdown', { clientX: 300 });
    expect(captured).toEqual([7]);
    pointer(el, 'pointermove', { clientX: 340 });
    pointer(el, 'pointermove', { clientX: 900 });
    pointer(el, 'pointermove', { clientX: 0 });
    pointer(el, 'pointerup', { clientX: 0 });
    pointer(el, 'pointermove', { clientX: 400 });
    await el.rendered();

    expect(changes).toEqual([320, 480, 220]);
    expect(el.getAttribute('aria-valuenow')).toBe('220');
    expect(captured).toEqual([]);
  });

  it('drags along the vertical axis when horizontal', async () => {
    const { el, changes } = await mount();
    el.orientation = 'horizontal';
    stubCapture(el);

    pointer(el, 'pointerdown', { clientX: 0, clientY: 100 });
    pointer(el, 'pointermove', { clientX: 500, clientY: 132 });

    expect(changes).toEqual([312]);
  });

  it('ignores a pointer that is not the primary button', async () => {
    const { el, changes } = await mount();
    const captured = stubCapture(el);

    pointer(el, 'pointerdown', { clientX: 300, button: 2 });
    pointer(el, 'pointermove', { clientX: 340 });

    expect(captured).toEqual([]);
    expect(changes).toEqual([]);
  });
});

/** happy-dom has no pointer capture: record the captured pointer ids instead. */
function stubCapture(el: HTMLElement): number[] {
  const captured: number[] = [];
  el.setPointerCapture = (id) => void captured.push(id);
  el.releasePointerCapture = (id) => void captured.splice(captured.indexOf(id), 1);
  el.hasPointerCapture = (id) => captured.includes(id);
  return captured;
}

function pointer(el: HTMLElement, type: string, init: PointerEventInit) {
  el.dispatchEvent(
    new PointerEvent(type, { pointerId: 7, button: 0, bubbles: true, cancelable: true, ...init }),
  );
}

function press(el: HTMLElement, key: string, init: KeyboardEventInit = {}) {
  el.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...init }));
}
