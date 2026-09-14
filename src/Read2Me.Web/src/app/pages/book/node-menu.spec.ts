import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ConfirmService } from '@app/ui/confirm-dialog/confirm-dialog';
import { PromptService } from '@app/ui/text-prompt-dialog/text-prompt-dialog';
import { BookEditor } from './book-editor';
import { NodeMenu } from './node-menu';
import { NodeMenuTarget } from './node-menu-entries';

describe('NodeMenu', () => {
  const locked = signal(false);
  let run: ReturnType<typeof vi.fn>;
  let confirm: ReturnType<typeof vi.fn>;
  let text: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    run = vi.fn().mockResolvedValue(true);
    confirm = vi.fn().mockResolvedValue(true);
    text = vi.fn().mockResolvedValue('Renamed');
    locked.set(false);
    TestBed.configureTestingModule({
      imports: [NodeMenu],
      providers: [
        { provide: BookEditor, useValue: { locked, run } },
        { provide: ConfirmService, useValue: { confirm } },
        { provide: PromptService, useValue: { text } },
      ],
    });
  });

  function render(target: NodeMenuTarget, disabled = false) {
    const fixture = TestBed.createComponent(NodeMenu);
    fixture.componentRef.setInput('target', target);
    fixture.componentRef.setInput('disabled', disabled);
    fixture.detectChanges();
    return fixture;
  }

  const trigger = (fixture: ReturnType<typeof render>) =>
    fixture.nativeElement.querySelector('.r2m-node-menu__trigger') as HTMLButtonElement;

  const open = (fixture: ReturnType<typeof render>) => {
    trigger(fixture).click();
    fixture.detectChanges();
    return Array.from(document.querySelectorAll<HTMLElement>('.mat-mdc-menu-panel [data-entry]'));
  };

  afterEach(() => {
    document.querySelectorAll('.cdk-overlay-container').forEach((n) => n.remove());
  });

  it('opens with the entry set for the target and posts the chosen command', async () => {
    const fixture = render({ kind: 'chapter', id: 'c1', text: 'One', isFirst: true, isLast: false });
    expect(trigger(fixture).getAttribute('aria-label')).toBe('Actions for One');

    const entries = open(fixture);
    expect(entries.map((e) => e.dataset['entry'])).toEqual([
      'edit-title',
      'split',
      'merge-next',
      'delete',
    ]);

    entries.find((e) => e.dataset['entry'] === 'edit-title')!.click();
    await fixture.whenStable();
    expect(text).toHaveBeenCalledWith(expect.objectContaining({ initial: 'One' }));
    expect(run).toHaveBeenCalledWith({ type: 'UpdateChapterTitle', chapterId: 'c1', title: 'Renamed' });
  });

  it('an item offers the pause submenus and a delete separated from the rest', () => {
    const fixture = render({ kind: 'item', id: 'i1', text: 'Line', isFirst: false, isLast: true });
    const entries = open(fixture);
    expect(entries.map((e) => e.dataset['entry'])).toEqual([
      'edit-text',
      'split',
      'merge-previous',
      'insert-before',
      'insert-after',
      'pause-before',
      'pause-after',
      'delete',
    ]);
  });

  it('a declined delete posts nothing', async () => {
    confirm.mockResolvedValue(false);
    const fixture = render({ kind: 'paragraph', id: 'p1', text: null, isFirst: true, isLast: true });
    open(fixture)
      .find((e) => e.dataset['entry'] === 'delete')!
      .click();
    await fixture.whenStable();
    expect(confirm).toHaveBeenCalledWith(expect.objectContaining({ destructive: true }));
    expect(run).not.toHaveBeenCalled();
  });

  it('a selection entry is emitted as an action, never posted as a command', async () => {
    const fixture = render({
      kind: 'chapter',
      id: 'c1',
      text: 'One',
      isFirst: true,
      isLast: true,
      selectable: true,
    });
    const actions: string[] = [];
    fixture.componentInstance.action.subscribe((a) => actions.push(a));

    const entries = open(fixture);
    expect(entries.slice(0, 2).map((e) => e.dataset['entry'])).toEqual([
      'select-unprocessed',
      'attribute-node',
    ]);
    entries[0]!.click();
    await fixture.whenStable();
    expect(actions).toEqual(['select-unprocessed']);
    expect(run).not.toHaveBeenCalled();
  });

  it('is disabled for a busy row and while the editor is locked', () => {
    const target: NodeMenuTarget = { kind: 'volume', id: 'v', text: 'V', isFirst: true, isLast: true };
    expect(trigger(render(target, true)).disabled).toBe(true);
    locked.set(true);
    expect(trigger(render(target)).disabled).toBe(true);
    locked.set(false);
    expect(trigger(render(target)).disabled).toBe(false);
  });
});
