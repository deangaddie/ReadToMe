import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { openDialog } from '@app/core/dialog';
import { announce } from './announcer';

interface Call {
  target: Node;
  text: string;
  priority: string | undefined;
}

let calls: Call[] = [];

// happy-dom has no ariaNotify: record what lands on a document or an element.
function record(this: Node, text: string, options?: AriaNotificationOptions): void {
  calls.push({ target: this, text, priority: options?.priority });
}

function removeAriaNotify(): void {
  delete document.ariaNotify;
  delete Element.prototype.ariaNotify;
}

beforeEach(() => {
  calls = [];
  document.ariaNotify = record;
  Element.prototype.ariaNotify = record;
});

afterEach(() => {
  removeAriaNotify();
  document.body.replaceChildren();
});

/** Opens a modal and returns its `<dialog>`, with the content that closes it. */
async function modal(): Promise<{ dialog: HTMLDialogElement; content: HTMLElement }> {
  const content = document.createElement('div');
  void openDialog(content);
  await Promise.resolve();
  return { dialog: content.closest('dialog')!, content };
}

describe('announce', () => {
  it('announces on the document when no modal is open', () => {
    announce('Saved');
    expect(calls).toEqual([{ target: document, text: 'Saved', priority: 'normal' }]);
  });

  it('maps assertive to high and polite to normal', () => {
    announce('Connection lost', 'assertive');
    announce('Reconnected', 'polite');
    expect(calls.map((c) => c.priority)).toEqual(['high', 'normal']);
  });

  it('announces on the open modal, which is not inert', async () => {
    const { dialog } = await modal();
    announce('Queued');
    expect(calls).toHaveLength(1);
    expect(calls[0]?.target).toBe(dialog);
  });

  it('announces on the topmost of two stacked modals, then on the one under it', async () => {
    const under = await modal();
    const top = await modal();
    announce('first');
    top.content.dispatchEvent(new CustomEvent('r2m-close'));
    announce('second');
    under.content.dispatchEvent(new CustomEvent('r2m-close'));
    announce('third');
    expect(calls.map((c) => c.target)).toEqual([top.dialog, under.dialog, document]);
  });

  it('skips a modal that has shut but not yet fired close', async () => {
    const { dialog } = await modal();
    // What a browser shows between `close()` and its `close` event, which it fires a task later.
    dialog.removeAttribute('open');
    announce('Saved');
    expect(calls[0]?.target).toBe(document);
  });

  it('follows the order the modals opened in, not the order they were asked for', async () => {
    let finishRender = () => {};
    const slow = Object.assign(document.createElement('div'), {
      rendered: () => new Promise<void>((resolve) => (finishRender = resolve)),
    });
    void openDialog(slow);
    const quick = await modal();
    announce('while the first renders');
    finishRender();
    await new Promise((r) => setTimeout(r));
    announce('once it is on top');
    expect(calls.map((c) => c.target)).toEqual([quick.dialog, slow.closest('dialog')!]);
  });

  it('does nothing in a browser without ariaNotify', async () => {
    removeAriaNotify();
    announce('unheard');
    await modal();
    announce('unheard', 'assertive');
    expect(calls).toEqual([]);
  });
});
