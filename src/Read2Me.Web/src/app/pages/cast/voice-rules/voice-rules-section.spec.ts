import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import type { ChapterVoicePreviewDto, CharacterSummaryDto, VoiceRuleDto } from '@app/api';
import { override, provide, resetServices } from '@app/core/services';
import { LiveService } from '@app/live/live.service';
import { ConfirmService } from '@app/ui/dialogs';
import { ToastService } from '@app/ui/toast';
import { FakeApi } from '../../../../testing/fake-api';
import { FakeLive } from '../../../../testing/fake-live';
import { settle } from '../../../../testing/fake-navigation';
import { ProjectStore } from '../../project/project-store';
import { CastStore } from '../cast-store';
import './voice-rules-section';

const BASE = '/api/projects/dune';
const COMMANDS = `${BASE}/commands`;

function row(name: string, overrides: Partial<CharacterSummaryDto> = {}): CharacterSummaryDto {
  return {
    id: name.toLowerCase(),
    name,
    aliases: [],
    lineCount: 0,
    voiceCount: 1,
    readyVoiceCount: 0,
    isNarrator: name === 'Narrator',
    narratesBook: false,
    ...overrides,
  };
}

function rule(overrides: Partial<VoiceRuleDto> = {}): VoiceRuleDto {
  return {
    ruleId: 'r1',
    voiceId: 'v1',
    voiceName: 'Voice A',
    isDefault: false,
    fromLevel: null,
    fromNodeId: null,
    fromTitle: null,
    fromDangling: false,
    toLevel: null,
    toNodeId: null,
    toTitle: null,
    toDangling: false,
    order: 'a1',
    ...overrides,
  };
}

const DEFAULT = rule({ ruleId: 'r0', isDefault: true, order: 'a0' });
const ONWARD = rule({
  ruleId: 'r1',
  voiceName: 'Voice B',
  fromLevel: 'Chapter',
  fromNodeId: 'c3',
  fromTitle: 'Chapter 3',
});
const SINGLE = rule({
  ruleId: 'r2',
  fromLevel: 'Chapter',
  fromNodeId: 'c4',
  fromTitle: 'Chapter 4',
  toLevel: 'Chapter',
  toNodeId: 'c4',
  toTitle: 'Chapter 4',
  order: 'a2',
});

const VOICE = {
  id: 'v1',
  characterId: 'alice',
  name: 'Voice A',
  description: null,
  source: 'Generated' as const,
  designPrompt: null,
  transcript: null,
  audioFileName: null,
  isEdited: false,
  voiceDesignSettingsOverrideJson: null,
  ttsSettingsOverrideJson: null,
  referenceSeconds: null,
  referenceWarning: null,
};

let api: FakeApi;
let store: CastStore;
let confirms: string[];
let confirmAnswer: boolean;
let narratorLinked: boolean;

beforeEach(() => {
  resetServices();
  api = new FakeApi();
  api.install();
  override(LiveService, new FakeLive() as unknown as LiveService);
  override(ToastService, { problem: () => undefined } as unknown as ToastService);
  confirms = [];
  confirmAnswer = true;
  override(ConfirmService, {
    confirm: async (o: { title: string; message: string }) => {
      confirms.push(o.message);
      return confirmAnswer;
    },
  } as unknown as ConfirmService);
  narratorLinked = false;
  store = new CastStore();
});
afterEach(() => {
  store.close();
  document.body.replaceChildren();
});

async function mount(
  character: CharacterSummaryDto,
  rules: VoiceRuleDto[],
  preview: ChapterVoicePreviewDto[] = [
    { chapterId: 'c1', chapterTitle: 'Chapter 1', voiceName: 'Voice A' },
  ],
) {
  api.on('GET', `${BASE}/characters/summary`, [row('Narrator'), character]);
  api.on('GET', `${BASE}/characters/${character.id}/lines`, []);
  api.on('GET', `${BASE}/characters/${character.id}/voices`, {
    defaultVoiceId: 'v1',
    voices: character.voiceCount ? [VOICE] : [],
  });
  api.on('GET', `${BASE}/characters/${character.id}/voice-rules`, rules);
  api.on('GET', `${BASE}/characters/${character.id}/voice-rules/preview`, preview);
  api.on('POST', COMMANDS, { outcome: 'Committed' });
  await store.open('dune');
  await store.select(character.id);
  const host = document.createElement('div');
  provide(host, CastStore, store);
  provide(host, ProjectStore, {
    detail: () => ({ narrator: { isLinked: narratorLinked } }),
  } as unknown as ProjectStore);
  const section = document.createElement('r2m-voice-rules-section');
  section.character = character;
  host.append(section);
  document.body.append(host);
  await section.rendered();
  return section;
}

const rows = (section: Element) => Array.from(section.querySelectorAll('li.voice-rules__row'));
const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
const commands = () => api.calls('POST', COMMANDS).map((r) => r.body);

describe('r2m-voice-rules-section', () => {
  it('renders nothing without rules', async () => {
    const section = await mount(row('Alice'), []);
    expect(section.children).toHaveLength(0);
  });

  it('describes each rule in order, hides the controls on the default row, and shows the preview', async () => {
    const section = await mount(
      row('Alice'),
      [DEFAULT, ONWARD, SINGLE],
      [
        { chapterId: 'c1', chapterTitle: 'Chapter 1', voiceName: 'Voice A' },
        { chapterId: 'c3', chapterTitle: '', voiceName: null },
      ],
    );
    expect(section.querySelector('.voice-rules__count')?.textContent).toBe('3');
    expect(rows(section).map((r) => text(r.querySelector('.voice-rules__text')))).toEqual([
      'Default → Voice A',
      'From Chapter Chapter 3 onward → Voice B',
      'Chapter Chapter 4 → Voice A',
    ]);
    expect(rows(section)[0]?.getAttribute('data-default')).toBe('true');
    expect(rows(section)[0]?.querySelector('[data-action=delete-rule]')).toBeNull();
    const onward = rows(section)[1]!;
    expect(onward.querySelector<HTMLButtonElement>('[data-action=move-up]')?.disabled).toBe(true);
    expect(onward.querySelector<HTMLButtonElement>('[data-action=move-down]')?.disabled).toBe(
      false,
    );
    const single = rows(section)[2]!;
    expect(single.querySelector<HTMLButtonElement>('[data-action=move-up]')?.disabled).toBe(false);
    expect(single.querySelector<HTMLButtonElement>('[data-action=move-down]')?.disabled).toBe(true);
    const preview = Array.from(section.querySelectorAll('table.voice-rules__preview tbody tr'));
    expect(preview.map((tr) => text(tr))).toEqual(['Chapter 1 Voice A', 'Untitled —']);
    expect(preview[1]?.querySelector('.voice-rules__none')).not.toBeNull();
  });

  it('Move posts MoveVoiceRule; Delete sits behind a confirm that names the rule', async () => {
    const section = await mount(row('Alice'), [DEFAULT, ONWARD, SINGLE]);
    rows(section)[2]!.querySelector<HTMLButtonElement>('[data-action=move-up]')!.click();
    await settle();
    await section.rendered();
    confirmAnswer = false;
    rows(section)[1]!.querySelector<HTMLButtonElement>('[data-action=delete-rule]')!.click();
    await settle();
    await section.rendered();
    expect(confirms).toEqual([
      'Delete the rule "From Chapter Chapter 3 onward → Voice B"? The default rule takes over where it applied.',
    ]);
    confirmAnswer = true;
    rows(section)[1]!.querySelector<HTMLButtonElement>('[data-action=delete-rule]')!.click();
    await settle();
    expect(commands()).toEqual([
      { type: 'MoveVoiceRule', ruleId: 'r2', direction: 'Up' },
      { type: 'DeleteVoiceRule', ruleId: 'r1' },
    ]);
  });

  it('flags a dangling rule', async () => {
    const section = await mount(row('Alice'), [
      DEFAULT,
      rule({
        fromLevel: 'Chapter',
        fromNodeId: 'gone',
        fromTitle: null,
        fromDangling: true,
        toLevel: 'Chapter',
        toNodeId: 'gone',
        toTitle: null,
        toDangling: true,
      }),
    ]);
    const dangling = rows(section)[1]!;
    expect(dangling.classList.contains('voice-rules__row--dangling')).toBe(true);
    expect(dangling.querySelector('[data-role=dangling]')).not.toBeNull();
    expect(text(dangling.querySelector('.voice-rules__text'))).toBe('(missing node) → Voice A');
  });

  it('the seed Narrator never gets Add rule, and loses Move/Delete while the link points elsewhere', async () => {
    const unlinked = await mount(row('Narrator'), [DEFAULT, ONWARD]);
    expect(unlinked.querySelector('[data-action=add-rule]')).toBeNull();
    expect(rows(unlinked)[1]?.querySelector('[data-action=delete-rule]')).not.toBeNull();

    document.body.replaceChildren();
    narratorLinked = true;
    const linked = await mount(row('Narrator'), [DEFAULT, ONWARD]);
    expect(rows(linked)[1]?.querySelector('[data-action=delete-rule]')).toBeNull();
  });

  it('Add rule opens the cascading dialog and is off without voices', async () => {
    const section = await mount(row('Alice'), [DEFAULT]);
    api.on('GET', `${BASE}/book`, { volumes: [] });
    const add = section.querySelector<HTMLButtonElement>('[data-action=add-rule]')!;
    expect(add.disabled).toBe(false);
    add.click();
    await settle();
    expect(document.querySelector('r2m-add-voice-rule-dialog')).not.toBeNull();

    document.body.replaceChildren();
    const none = await mount(row('Alice', { voiceCount: 0 }), [DEFAULT]);
    expect(none.querySelector<HTMLButtonElement>('[data-action=add-rule]')?.disabled).toBe(true);
  });
});
