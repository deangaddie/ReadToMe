import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import type { VoiceDto } from '@app/api';
import { resetServices } from '@app/core/services';
import { FakeApi } from '../../../../testing/fake-api';
import { settle } from '../../../../testing/fake-navigation';
import type { AddVoiceRuleDialog } from './add-voice-rule-dialog';
import { openAddVoiceRuleDialog } from './add-voice-rule-dialog';
import './add-voice-rule-dialog';

const BASE = '/api/projects/dune';

function voice(id: string, name: string): VoiceDto {
  return {
    id,
    characterId: 'alice',
    name,
    description: null,
    source: 'Generated',
    designPrompt: null,
    transcript: null,
    audioFileName: null,
    isEdited: false,
    voiceDesignSettingsOverrideJson: null,
    ttsSettingsOverrideJson: null,
    referenceSeconds: null,
    referenceWarning: null,
  };
}

let api: FakeApi;

beforeEach(() => {
  resetServices();
  api = new FakeApi();
  api.install();
  api.on('GET', `${BASE}/book`, { volumes: [{ id: 'v1', title: 'v1' }] });
  api.on('GET', `${BASE}/nodes/volume/v1/children`, {
    parts: [{ id: 'p1', title: '' }],
  });
  api.on('GET', `${BASE}/nodes/part/p1/children`, {
    chapters: [
      { id: 'c1', title: 'Chapter 1' },
      { id: 'c3', title: 'Chapter 3' },
    ],
  });
  api.on('GET', `${BASE}/nodes/chapter/c3/children`, {
    paragraphs: [{ id: 'para', items: [{ id: 'item', text: 'Hello there, said Alice to Bob.' }] }],
  });
});
afterEach(() => document.body.replaceChildren());

async function open(voices = [voice('va', 'Voice A'), voice('vb', 'Voice B')]) {
  const result = openAddVoiceRuleDialog({ folder: 'dune', characterId: 'alice', voices });
  await settle();
  const dialog = document.querySelector<AddVoiceRuleDialog>('r2m-add-voice-rule-dialog')!;
  await dialog.rendered();
  await settle();
  await dialog.rendered();
  return { result, dialog };
}

const select = (dialog: Element, field: string) =>
  dialog.querySelector<HTMLSelectElement>(`select[data-field=${field}]`);

async function choose(dialog: AddVoiceRuleDialog, field: string, value: string) {
  const el = select(dialog, field)!;
  el.value = value;
  el.dispatchEvent(new Event('change'));
  await settle();
  await dialog.rendered();
}

const add = (dialog: Element) => dialog.querySelector<HTMLButtonElement>('[data-action=add]')!;
const labels = (dialog: Element, field: string) =>
  Array.from(select(dialog, field)!.querySelectorAll('option:not([disabled])')).map((o) =>
    o.textContent?.trim(),
  );

describe('r2m-add-voice-rule-dialog', () => {
  it('preselects the first voice, lists the volumes and keeps Add off until an anchor is chosen', async () => {
    const { dialog } = await open();
    expect(select(dialog, 'voice')?.value).toBe('va');
    expect(labels(dialog, 'volume')).toEqual(['v1']);
    expect(select(dialog, 'part')).toBeNull();
    expect(add(dialog).disabled).toBe(true);
  });

  it('cascades volume → part → chapter → paragraph → line, each level fetched on pick', async () => {
    const { result, dialog } = await open();
    await choose(dialog, 'voice', 'vb');
    await choose(dialog, 'volume', 'v1');
    expect(add(dialog).disabled).toBe(false);
    expect(labels(dialog, 'part')).toEqual(['Untitled']);
    await choose(dialog, 'part', 'p1');
    expect(labels(dialog, 'chapter')).toEqual(['Chapter 1', 'Chapter 3']);
    await choose(dialog, 'chapter', 'c3');
    expect(labels(dialog, 'paragraph')).toEqual(['Hello there, said Alice to Bob.']);
    await choose(dialog, 'paragraph', 'para');
    expect(labels(dialog, 'item')).toEqual(['Hello there, said Alice to Bob.']);
    add(dialog).click();
    expect(await result).toEqual({
      type: 'CreateVoiceRule',
      characterId: 'alice',
      voiceId: 'vb',
      fromLevel: 'Paragraph',
      fromNodeId: 'para',
      toLevel: null,
      toNodeId: null,
    });
  });

  it('"Just this node" closes the rule on the anchor; clearing a level drops the deeper ones', async () => {
    const { result, dialog } = await open();
    dialog.querySelector<HTMLInputElement>('input[value=justThisNode]')!.click();
    await choose(dialog, 'volume', 'v1');
    await choose(dialog, 'part', 'p1');
    await choose(dialog, 'chapter', 'c3');
    expect(select(dialog, 'paragraph')).not.toBeNull();
    dialog.querySelector<HTMLButtonElement>('[aria-label="Clear part"]')!.click();
    await dialog.rendered();
    expect(select(dialog, 'chapter')).toBeNull();
    expect(select(dialog, 'paragraph')).toBeNull();
    expect(dialog.selection()).toMatchObject({ volumeId: 'v1', partId: null, chapterId: null });
    await choose(dialog, 'part', 'p1');
    await choose(dialog, 'chapter', 'c3');
    add(dialog).click();
    expect(await result).toMatchObject({
      fromLevel: 'Chapter',
      fromNodeId: 'c3',
      toLevel: 'Chapter',
      toNodeId: 'c3',
    });
  });

  it('Cancel answers null', async () => {
    const { result, dialog } = await open();
    Array.from(dialog.querySelectorAll('button'))
      .find((b) => b.textContent?.trim() === 'Cancel')!
      .click();
    expect(await result).toBeNull();
  });
});
