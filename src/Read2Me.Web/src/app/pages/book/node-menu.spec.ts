import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { render } from 'lit-html';
import { override, provide, resetServices } from '@app/core/services';
import { signal } from '@app/core/signals';
import { ConfirmService, PromptService } from '@app/ui/dialogs';
import { FakeTimers } from '../../../testing/fake-timers';
import { BookEditor } from './book-editor';
import { NODE_MENU_OPEN, type NodeMenu, type NodeMenuRequest, nodeMenuTrigger } from './node-menu';
import type { NodeMenuTarget } from './node-menu-entries';
import './node-menu';

/**
 * Ported from the Angular TestBed spec, case for case, plus the native-only behaviour: one shared
 * menu serves every trigger under its parent, re-anchored per open, with the roving keys and the
 * focus return the tree relies on (spec §7, risk 2). happy-dom has no `showPopover`, so the open
 * state is read off the rendered entries; the real top layer is E2E's job.
 */
const locked = signal(false);
let runs: unknown[];
let confirms: unknown[];
let confirmAnswer: boolean;
let prompts: unknown[];
let promptAnswer: string | null;
let host: HTMLElement;
let menu: NodeMenu;

function newHost() {
  host = document.createElement('div');
  provide(host, BookEditor, {
    locked,
    run: async (c: unknown) => (runs.push(c), true),
  } as unknown as BookEditor);
  document.body.append(host);
  menu = document.createElement('r2m-node-menu');
  host.append(menu);
}

beforeEach(() => {
  resetServices();
  runs = [];
  confirms = [];
  confirmAnswer = true;
  prompts = [];
  promptAnswer = 'Renamed';
  locked.set(false);
  override(ConfirmService, {
    confirm: async (o: unknown) => (confirms.push(o), confirmAnswer),
  } as unknown as ConfirmService);
  override(PromptService, {
    text: async (o: unknown) => (prompts.push(o), promptAnswer),
  } as unknown as PromptService);
  newHost();
});
afterEach(() => document.body.replaceChildren());

/** Renders a trigger for `target` next to the shared menu. */
async function mount(target: NodeMenuTarget, disabled = false) {
  const slot = document.createElement('div');
  host.append(slot);
  render(nodeMenuTrigger(target, { disabled }), slot);
  await menu.rendered();
  return slot.querySelector<HTMLButtonElement>('.r2m-node-menu__trigger')!;
}

const entries = () =>
  Array.from(menu.querySelectorAll<HTMLElement>('[role="menu"] [data-entry]')).map(
    (e) => e.dataset['entry'],
  );
const entry = (id: string) => menu.querySelector<HTMLButtonElement>(`[data-entry="${id}"]`)!;
const settle = () => new Promise((r) => setTimeout(r, 0));
const press = (key: string, target: Element | null = document.activeElement) =>
  target!.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }));
/** What the tree does: fill in where focus returns and who takes the selection actions. */
const onOpen = (fill: (request: NodeMenuRequest) => void) =>
  host.addEventListener(NODE_MENU_OPEN, (e) => fill((e as CustomEvent<NodeMenuRequest>).detail));

async function open(trigger: HTMLButtonElement) {
  trigger.click();
  await menu.rendered();
  await settle();
}

const CHAPTER: NodeMenuTarget = { kind: 'chapter', id: 'c1', text: 'One', isFirst: true, isLast: false };
const ITEM: NodeMenuTarget = { kind: 'item', id: 'i1', text: 'Line', isFirst: false, isLast: true };

describe('r2m-node-menu', () => {
  it('opens with the entry set for the target and posts the chosen command', async () => {
    const trigger = await mount(CHAPTER);
    expect(trigger.getAttribute('aria-label')).toBe('Actions for One');
    expect(entries()).toEqual([]);

    await open(trigger);
    expect(menu.querySelector('[role="menu"]')?.getAttribute('aria-label')).toBe('Actions for One');
    expect(entries()).toEqual(['edit-title', 'split', 'merge-next', 'delete']);

    entry('edit-title').click();
    await settle();
    expect(prompts).toEqual([expect.objectContaining({ initial: 'One' })]);
    expect(runs).toEqual([{ type: 'UpdateChapterTitle', chapterId: 'c1', title: 'Renamed' }]);
    // Choosing closes the menu.
    expect(entries()).toEqual([]);
  });

  it('an item offers the pause submenus and a delete separated from the rest', async () => {
    const trigger = await mount(ITEM);
    await open(trigger);
    expect(entries()).toEqual([
      'edit-text',
      'split',
      'merge-previous',
      'insert-before',
      'insert-after',
      'pause-before',
      'pause-after',
      'delete',
    ]);
    // The pause entries live in a submenu that opens from its entry.
    entry('pause-before').click();
    await menu.rendered();
    expect(entries()).toContain('pause-before:ChapterPause');
    expect(entry('pause-before').getAttribute('aria-expanded')).toBe('true');
    entry('pause-before:ChapterPause').click();
    await settle();
    expect(runs).toEqual([
      {
        type: 'InsertPauseParagraph',
        anchorItemId: 'i1',
        position: 'Before',
        pauseKind: 'ChapterPause',
      },
    ]);
  });

  it('a declined delete posts nothing', async () => {
    confirmAnswer = false;
    const trigger = await mount({ kind: 'paragraph', id: 'p1', text: null, isFirst: true, isLast: true });
    await open(trigger);
    entry('delete').click();
    await settle();
    expect(confirms).toEqual([expect.objectContaining({ destructive: true })]);
    expect(runs).toEqual([]);
  });

  it('a selection entry is handed to the opener as an action, never posted as a command', async () => {
    const trigger = await mount({ ...CHAPTER, isLast: true, selection: 'paragraphs' });
    const actions: string[] = [];
    onOpen((request) => (request.onAction = (a) => actions.push(a)));
    await open(trigger);
    expect(entries().slice(0, 2)).toEqual(['select-unprocessed', 'attribute-node']);
    entry('select-unprocessed').click();
    await settle();
    expect(actions).toEqual(['select-unprocessed']);
    expect(runs).toEqual([]);
  });

  it('is disabled for a busy row and refuses to open while the editor is locked', async () => {
    const target: NodeMenuTarget = { kind: 'volume', id: 'v', text: 'V', isFirst: true, isLast: true };
    expect((await mount(target, true)).disabled).toBe(true);

    const trigger = await mount(target);
    expect(trigger.disabled).toBe(false);
    locked.set(true);
    await open(trigger);
    expect(entries()).toEqual([]);
    locked.set(false);
    await open(trigger);
    expect(entries()).toEqual(['edit-title', 'delete']);
  });

  describe('the shared instance (risk 2)', () => {
    it('re-anchors to the trigger of each open and names only the new anchor', async () => {
      const first = await mount(CHAPTER);
      const second = await mount({ kind: 'chapter', id: 'c2', text: 'Two', isFirst: false, isLast: true });
      await open(first);
      expect(first.style.getPropertyValue('anchor-name')).toBe('--r2m-node-menu');
      expect(first.getAttribute('aria-expanded')).toBe('true');
      expect(first.getAttribute('aria-controls')).toBe('r2m-node-menu-panel');
      await open(second);
      expect(first.getAttribute('aria-expanded')).toBe('false');
      expect(first.hasAttribute('aria-controls')).toBe(false);
      expect(entries()).toEqual(['edit-title', 'split', 'merge-previous', 'delete']);
      expect(second.style.getPropertyValue('anchor-name')).toBe('--r2m-node-menu');
      expect(first.style.getPropertyValue('anchor-name')).toBe('');
    });

    it('focuses the first entry on open; Escape closes and returns focus where the opener says', async () => {
      const trigger = await mount(CHAPTER);
      const item = document.createElement('div');
      item.tabIndex = 0;
      host.append(item);
      onOpen((request) => (request.returnTo = item));
      await open(trigger);
      expect(document.activeElement).toBe(entry('edit-title'));

      press('Escape');
      await menu.rendered();
      expect(entries()).toEqual([]);
      expect(document.activeElement).toBe(item);
    });

    it('focus also returns after an entry is chosen', async () => {
      const trigger = await mount(CHAPTER);
      const item = document.createElement('div');
      item.tabIndex = 0;
      host.append(item);
      onOpen((request) => (request.returnTo = item));
      await open(trigger);
      entry('split').click();
      await settle();
      expect(document.activeElement).toBe(item);
    });

    it('arrows and Home/End rove through the entries, wrapping, and letters type ahead', async () => {
      const trigger = await mount({ ...CHAPTER, isFirst: false });
      await open(trigger);
      // Faked after the open: the open itself settles on a real timer.
      const timers = new FakeTimers().install();
      try {
        press('ArrowDown');
        expect(document.activeElement).toBe(entry('split'));
        press('End');
        expect(document.activeElement).toBe(entry('delete'));
        press('ArrowDown');
        expect(document.activeElement).toBe(entry('edit-title'));
        press('ArrowUp');
        expect(document.activeElement).toBe(entry('delete'));
        press('Home');
        expect(document.activeElement).toBe(entry('edit-title'));

        press('m');
        press('e');
        await timers.advance(200);
        expect(document.activeElement).toBe(entry('merge-previous'));
        press('m');
        await timers.advance(200);
        expect(document.activeElement).toBe(entry('merge-next'));
      } finally {
        timers.restore();
      }
    });

    it('Right opens a submenu from its entry and Left closes it back to the entry', async () => {
      const trigger = await mount(ITEM);
      await open(trigger);
      entry('pause-after').focus();
      press('ArrowRight');
      await menu.rendered();
      await settle();
      expect(document.activeElement).toBe(entry('pause-after:Pause'));
      press('ArrowDown');
      expect(document.activeElement).toBe(entry('pause-after:ParagraphPause'));
      press('ArrowLeft');
      await menu.rendered();
      expect(entries()).not.toContain('pause-after:Pause');
      expect(document.activeElement).toBe(entry('pause-after'));
    });

    it('closes when its trigger leaves the DOM (the node was deleted under the open menu)', async () => {
      const trigger = await mount(CHAPTER);
      await open(trigger);
      expect(entries()).not.toEqual([]);
      trigger.parentElement!.remove();
      await settle();
      await menu.rendered();
      expect(entries()).toEqual([]);
    });

    it('a trigger click stops at the trigger, so the row or node around it does not act on it', async () => {
      const trigger = await mount(CHAPTER);
      let clicks = 0;
      host.addEventListener('click', () => clicks++);
      trigger.click();
      expect(clicks).toBe(0);
    });
  });
});
