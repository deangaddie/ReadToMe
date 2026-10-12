import { describe, expect, it } from 'bun:test';
import type { ConfirmOptions, TextPromptOptions } from '@app/ui/dialogs';
import { CUE_HINT, type MenuDialogs, commandFor, displayName } from './node-menu-commands';
import type { NodeMenuKind, NodeMenuTarget } from './node-menu-entries';

function target(kind: NodeMenuKind, overrides: Partial<NodeMenuTarget> = {}): NodeMenuTarget {
  return { kind, id: 'n1', text: 'Old', isFirst: false, isLast: false, ...overrides };
}

/** Records what was asked and answers with the scripted values. */
function dialogs(answers: { text?: string | null; confirm?: boolean } = {}) {
  const asked: { text: TextPromptOptions[]; confirm: ConfirmOptions[] } = { text: [], confirm: [] };
  const fake: MenuDialogs = {
    text: async (options) => {
      asked.text.push(options);
      return answers.text === undefined ? null : answers.text;
    },
    confirm: async (options) => {
      asked.confirm.push(options);
      return answers.confirm ?? false;
    },
  };
  return { fake, asked };
}

describe('commandFor', () => {
  describe('edit title', () => {
    it.each([
      ['volume', 'UpdateVolumeTitle', 'volumeId'],
      ['part', 'UpdatePartTitle', 'partId'],
      ['chapter', 'UpdateChapterTitle', 'chapterId'],
    ] as const)('%s prompts with the current title and posts the new one', async (kind, type, key) => {
      const { fake, asked } = dialogs({ text: 'New' });
      const command = await commandFor('edit-title', target(kind), fake);
      expect(asked.text[0]).toMatchObject({ title: `Edit ${kind} title`, initial: 'Old', required: true });
      expect<unknown>(command).toEqual({ type, [key]: 'n1', title: 'New' });
    });

    it('sends nothing when cancelled or unchanged', async () => {
      expect<unknown>(await commandFor('edit-title', target('chapter'), dialogs({ text: null }).fake)).toBeNull();
      expect<unknown>(await commandFor('edit-title', target('chapter'), dialogs({ text: 'Old' }).fake)).toBeNull();
    });
  });

  describe('edit text', () => {
    it('prompts multiline and posts the text', async () => {
      const { fake, asked } = dialogs({ text: 'Rewritten.' });
      const command = await commandFor('edit-text', target('item'), fake);
      expect(asked.text[0]).toMatchObject({ multiline: true, required: true, initial: 'Old' });
      expect<unknown>(command).toEqual({ type: 'UpdateParagraphItemText', itemId: 'n1', text: 'Rewritten.' });
    });

    it('sends nothing when unchanged', async () => {
      expect<unknown>(await commandFor('edit-text', target('item'), dialogs({ text: 'Old' }).fake)).toBeNull();
    });
  });

  describe('delivery cue (delivery-cue-editing 03)', () => {
    it('add opens a single-line, optional prompt with the hint and posts the typed cue', async () => {
      const { fake, asked } = dialogs({ text: 'whispered' });
      const command = await commandFor('add-cue', target('item', { cue: null }), fake);
      expect(asked.text[0]).toMatchObject({ title: 'Add delivery cue', initial: '', hint: CUE_HINT });
      expect(asked.text[0]!.multiline).toBeFalsy();
      expect(asked.text[0]!.required).toBeFalsy();
      expect<unknown>(command).toEqual({ type: 'SetItemDeliveryCue', itemId: 'n1', cue: 'whispered' });
    });

    it('edit is pre-filled with the current cue and posts the new one', async () => {
      const { fake, asked } = dialogs({ text: 'shouting' });
      const command = await commandFor('edit-cue', target('item', { cue: 'whispered' }), fake);
      expect(asked.text[0]).toMatchObject({ title: 'Edit delivery cue', initial: 'whispered', hint: CUE_HINT });
      expect(asked.text[0]!.required).toBeFalsy();
      expect<unknown>(command).toEqual({ type: 'SetItemDeliveryCue', itemId: 'n1', cue: 'shouting' });
    });

    it('saving empty removes the cue', async () => {
      const cued = target('item', { cue: 'whispered' });
      expect<unknown>(await commandFor('edit-cue', cued, dialogs({ text: '' }).fake)).toEqual({
        type: 'SetItemDeliveryCue',
        itemId: 'n1',
        cue: null,
      });
    });

    it('sends nothing when cancelled, unchanged, or an add is saved empty', async () => {
      const cued = target('item', { cue: 'whispered' });
      expect<unknown>(await commandFor('edit-cue', cued, dialogs({ text: null }).fake)).toBeNull();
      expect<unknown>(await commandFor('edit-cue', cued, dialogs({ text: 'whispered' }).fake)).toBeNull();
      expect<unknown>(await commandFor('add-cue', target('item'), dialogs({ text: null }).fake)).toBeNull();
      expect<unknown>(await commandFor('add-cue', target('item'), dialogs({ text: '' }).fake)).toBeNull();
    });

    it('remove posts no cue without asking', async () => {
      const { fake, asked } = dialogs();
      expect<unknown>(await commandFor('remove-cue', target('item', { cue: 'whispered' }), fake)).toEqual({
        type: 'SetItemDeliveryCue',
        itemId: 'n1',
        cue: null,
      });
      expect(asked.text).toEqual([]);
      expect(asked.confirm).toEqual([]);
    });
  });

  describe('delivery cue on an item with audio (delivery-cue-editing 04)', () => {
    const voiced = (cue: string | null) => target('item', { cue, hasAudio: true });

    it.each([
      ['add-cue', null, 'whispered', 'Add delivery cue', 'Adding a delivery cue', 'Add'],
      ['edit-cue', 'whispered', 'shouting', 'Change delivery cue', 'Changing the delivery cue', 'Change'],
      ['edit-cue', 'whispered', '', 'Remove delivery cue', 'Removing the delivery cue', 'Remove'],
      ['remove-cue', 'whispered', null, 'Remove delivery cue', 'Removing the delivery cue', 'Remove'],
    ] as const)('%s (%p → %p) asks first and posts on confirm', async (entry, cue, typed, title, lead, label) => {
      const { fake, asked } = dialogs({ text: typed, confirm: true });
      const command = await commandFor(entry, voiced(cue), fake);
      expect(asked.confirm).toEqual([
        {
          title,
          message: `${lead} discards this item's audio.`,
          confirmLabel: label,
          destructive: true,
        },
      ]);
      expect<unknown>(command).toEqual({ type: 'SetItemDeliveryCue', itemId: 'n1', cue: typed || null });
    });

    it('cancelling the confirm sends nothing', async () => {
      expect<unknown>(await commandFor('remove-cue', voiced('whispered'), dialogs().fake)).toBeNull();
      expect<unknown>(
        await commandFor('edit-cue', voiced('whispered'), dialogs({ text: 'shouting', confirm: false }).fake),
      ).toBeNull();
    });

    it('an unchanged or cancelled edit does not ask', async () => {
      for (const text of ['whispered', null]) {
        const { fake, asked } = dialogs({ text, confirm: true });
        expect<unknown>(await commandFor('edit-cue', voiced('whispered'), fake)).toBeNull();
        expect(asked.confirm).toEqual([]);
      }
    });

    it('an item without audio is not asked', async () => {
      const { fake, asked } = dialogs({ text: 'shouting' });
      const command = await commandFor('edit-cue', target('item', { cue: 'whispered', hasAudio: false }), fake);
      expect<unknown>(command).toEqual({ type: 'SetItemDeliveryCue', itemId: 'n1', cue: 'shouting' });
      expect(asked.confirm).toEqual([]);
    });
  });

  describe('split', () => {
    it.each([
      ['part', 'SplitAtPart', 'partId', 'newVolumeTitle', 'volume'],
      ['chapter', 'SplitAtChapter', 'chapterId', 'newPartTitle', 'part'],
      ['paragraph', 'SplitAtParagraph', 'paragraphId', 'newChapterTitle', 'chapter'],
    ] as const)('%s asks for the new title, blank allowed', async (kind, type, key, titleKey, created) => {
      const { fake, asked } = dialogs({ text: '' });
      expect<unknown>(await commandFor('split', target(kind), fake)).toEqual({
        type,
        [key]: 'n1',
        [titleKey]: null,
      });
      expect(asked.text[0]).toMatchObject({ title: `New ${created} title` });
      expect(asked.text[0]!.required).toBeFalsy();

      expect<unknown>(await commandFor('split', target(kind), dialogs({ text: 'Two' }).fake)).toEqual({
        type,
        [key]: 'n1',
        [titleKey]: 'Two',
      });
      expect<unknown>(await commandFor('split', target(kind), dialogs({ text: null }).fake)).toBeNull();
    });

    it('item splits the paragraph without a prompt', async () => {
      const { fake, asked } = dialogs();
      expect<unknown>(await commandFor('split', target('item'), fake)).toEqual({
        type: 'SplitAtItem',
        itemId: 'n1',
      });
      expect(asked.text).toEqual([]);
    });
  });

  describe('merge', () => {
    it.each([
      ['volume', 'MergeVolume', 'volumeId'],
      ['part', 'MergePart', 'partId'],
      ['chapter', 'MergeChapter', 'chapterId'],
      ['paragraph', 'MergeParagraph', 'paragraphId'],
      ['item', 'MergeParagraphItem', 'itemId'],
    ] as const)('%s merges in the chosen direction', async (kind, type, key) => {
      const { fake } = dialogs();
      expect<unknown>(await commandFor('merge-previous', target(kind), fake)).toEqual({
        type,
        [key]: 'n1',
        direction: 'Previous',
      });
      expect<unknown>(await commandFor('merge-next', target(kind), fake)).toEqual({
        type,
        [key]: 'n1',
        direction: 'Next',
      });
    });
  });

  describe('insert', () => {
    it('item text is required and anchored at the item', async () => {
      const { fake, asked } = dialogs({ text: 'New line.' });
      expect<unknown>(await commandFor('insert-after', target('item'), fake)).toEqual({
        type: 'InsertParagraphItem',
        anchorItemId: 'n1',
        position: 'After',
        text: 'New line.',
      });
      expect(asked.text[0]).toMatchObject({ title: 'Insert item after', required: true, multiline: true });
      expect<unknown>(await commandFor('insert-before', target('item'), dialogs({ text: null }).fake)).toBeNull();
    });

    it('pauses post without a prompt', async () => {
      const { fake, asked } = dialogs();
      expect<unknown>(await commandFor('pause-before:ChapterPause', target('item'), fake)).toEqual({
        type: 'InsertPauseParagraph',
        anchorItemId: 'n1',
        position: 'Before',
        pauseKind: 'ChapterPause',
      });
      expect(asked.text).toEqual([]);
    });
  });

  describe('delete', () => {
    it.each([
      ['volume', 'DeleteVolume', 'volumeId', true],
      ['part', 'DeletePart', 'partId', true],
      ['chapter', 'DeleteChapter', 'chapterId', true],
      ['paragraph', 'DeleteParagraph', 'paragraphId', false],
      ['item', 'DeleteParagraphItem', 'itemId', false],
      ['pause-paragraph', 'DeleteParagraph', 'paragraphId', false],
    ] as const)('%s confirms destructively and mentions contents only for containers', async (kind, type, key, contents) => {
      const { fake, asked } = dialogs({ confirm: true });
      expect<unknown>(await commandFor('delete', target(kind), fake)).toEqual({ type, [key]: 'n1' });
      const confirm = asked.confirm[0]!;
      expect(confirm.destructive).toBe(true);
      expect(confirm.message).toContain('Delete “Old”?');
      expect(confirm.message.includes('will also delete all contents')).toBe(contents);
    });

    it('sends nothing when declined', async () => {
      expect<unknown>(await commandFor('delete', target('chapter'), dialogs({ confirm: false }).fake)).toBeNull();
    });
  });

  describe('displayName', () => {
    it('falls back to the kind and shortens long text', () => {
      expect(displayName(target('paragraph', { text: '  ' }))).toBe('this paragraph');
      expect(displayName(target('pause-paragraph', { text: null }))).toBe('this pause');
      expect(displayName(target('item', { text: 'x'.repeat(70) }))).toBe(`${'x'.repeat(60)}…`);
    });
  });
});
