import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import type { CharacterSummaryDto, VoiceDto } from '@app/api';
import { override, provide, resetServices } from '@app/core/services';
import { LiveService } from '@app/live/live.service';
import { Preflight } from '@app/shared/preflight';
import { ConfirmService } from '@app/ui/dialogs';
import { ToastService } from '@app/ui/toast';
import { FakeApi, problem } from '../../../../testing/fake-api';
import { FakeLive } from '../../../../testing/fake-live';
import { settle } from '../../../../testing/fake-navigation';
import { CastStore } from '../cast-store';
import { ProviderSchemas } from './provider-schemas';
import './voice-card';

const BASE = '/api/projects/dune';
const COMMANDS = `${BASE}/commands`;
const VOICES = `${BASE}/characters/alice/voices`;

const ALICE: CharacterSummaryDto = {
  id: 'alice',
  name: 'Alice',
  aliases: [],
  lineCount: 0,
  voiceCount: 1,
  readyVoiceCount: 0,
  isNarrator: false,
  narratesBook: false,
};

function voice(overrides: Partial<VoiceDto> = {}): VoiceDto {
  return {
    id: 'v1',
    characterId: 'alice',
    name: 'Main',
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
    ...overrides,
  };
}

let api: FakeApi;
let store: CastStore;
let toasts: string[];
let confirms: string[];
let confirmAnswer: boolean;
let preflightTasks: string[];
let preflightAnswer: boolean;

beforeEach(() => {
  resetServices();
  api = new FakeApi();
  api.install();
  new FakeLive().install();
  override(LiveService, new FakeLive() as unknown as LiveService);
  toasts = [];
  override(ToastService, {
    success: (m: string) => toasts.push(`success: ${m}`),
    warn: (m: string) => toasts.push(`warn: ${m}`),
    problem: (p: { detail?: string }) => toasts.push(`problem: ${p.detail}`),
  } as unknown as ToastService);
  confirms = [];
  confirmAnswer = true;
  override(ConfirmService, {
    confirm: async (o: { title: string }) => {
      confirms.push(o.title);
      return confirmAnswer;
    },
  } as unknown as ConfirmService);
  preflightTasks = [];
  preflightAnswer = true;
  override(Preflight, {
    ensureReady: async (task: string) => {
      preflightTasks.push(task);
      return preflightAnswer;
    },
  } as unknown as Preflight);
  override(ProviderSchemas, { active: async () => null } as unknown as ProviderSchemas);
  store = new CastStore();
});
afterEach(() => {
  store.close();
  document.body.replaceChildren();
});

/** The card under a host that provides the store, as the voices section does. */
async function mount(v: VoiceDto, isDefault = false) {
  api.on('GET', `${BASE}/characters/summary`, [ALICE]);
  api.on('GET', `${BASE}/characters/alice/lines`, []);
  api.on('GET', VOICES, { defaultVoiceId: isDefault ? v.id : null, voices: [v] });
  api.on('GET', `${BASE}/characters/alice/voice-rules`, []);
  api.on('GET', `${BASE}/characters/alice/voice-rules/preview`, []);
  api.on('POST', COMMANDS, { outcome: 'Committed' });
  await store.open('dune');
  await store.select('alice');
  const host = document.createElement('div');
  provide(host, CastStore, store);
  const card = document.createElement('r2m-voice-card');
  card.voice = v;
  card.characterName = 'Alice';
  card.isDefault = isDefault;
  host.append(card);
  document.body.append(host);
  await card.rendered();
  return card;
}

const button = (el: Element, action: string) =>
  el.querySelector<HTMLButtonElement>(`[data-action="${action}"]`)!;
const commands = () => api.calls('POST', COMMANDS).map((r) => r.body);
const textarea = (el: Element, label: string) =>
  el.querySelector<HTMLTextAreaElement>(`textarea[aria-label="${label}"]`)!;

function type(el: HTMLTextAreaElement, value: string) {
  el.value = value;
  el.dispatchEvent(new Event('input'));
}

describe('r2m-voice-card', () => {
  it('shows the name, source chip, Edited and reference chips, and the default star', async () => {
    const card = await mount(
      voice({
        source: 'Uploaded',
        isEdited: true,
        audioFileName: 'voices/alice/v1.wav',
        referenceSeconds: 20,
        referenceWarning: 'over 15 s',
        description: 'Early chapters',
      }),
      true,
    );
    expect(card.getAttribute('data-voice-id')).toBe('v1');
    expect(card.querySelector('r2m-inline-edit')?.value).toBe('Main');
    expect(card.querySelector('.voice-card__description')?.textContent).toBe('Early chapters');
    expect(card.querySelector('.voice-card__meta .r2m-status-chip')?.textContent).toContain(
      'Reference',
    );
    expect(card.querySelector('[data-testid=voice-edited-chip]')?.textContent).toContain('Edited');
    expect(card.querySelector('[data-testid=voice-reference-warning]')?.textContent).toContain(
      '20 s',
    );
    expect(card.querySelector('.voice-card__star--on')).not.toBeNull();
    expect(button(card, 'set-default')).toBeNull();
    expect(button(card, 'edit-audio')?.getAttribute('href')).toBe('projects/dune/voices/v1/editor');
    expect(card.querySelector('[data-mode=reference]')).not.toBeNull();
    expect(card.querySelector('r2m-audio-player')?.src).toBe(
      '/workspace/dune/voices/alice/v1.wav?v=0',
    );
  });

  it('Set as default posts SetVoiceDefault; rename posts UpdateVoice with the stored description', async () => {
    const card = await mount(voice({ description: 'd' }));
    expect(card.querySelector('.voice-card__meta .r2m-status-chip')?.textContent).toContain(
      'Prompt',
    );
    button(card, 'set-default').click();
    await settle();
    await card.rendered();
    card
      .querySelector('r2m-inline-edit')!
      .dispatchEvent(new CustomEvent('save', { detail: 'Lead' }));
    await settle();
    expect(commands()).toEqual([
      { type: 'SetVoiceDefault', voiceId: 'v1' },
      { type: 'UpdateVoice', voiceId: 'v1', name: 'Lead', description: 'd' },
    ]);
  });

  it('the description draft dirty-gates Save and clears once saved', async () => {
    const card = await mount(voice());
    const save = () => button(card, 'save-description');
    expect(save().disabled).toBe(true);
    type(textarea(card, 'Voice description'), 'Where she whispers');
    await card.rendered();
    expect(save().disabled).toBe(false);
    save().click();
    await settle();
    await settle();
    expect(commands()).toEqual([
      { type: 'UpdateVoice', voiceId: 'v1', name: 'Main', description: 'Where she whispers' },
    ]);
    expect(card.descriptionDraft()).toBeNull();
  });

  it('Generate audio needs a prompt, saves a dirty draft first, then synthesises and bumps the player', async () => {
    const card = await mount(voice());
    api.on('POST', `${BASE}/characters/alice/voices/v1/generate-audio`, {
      audioFileName: 'voices/alice/v1.wav',
      referenceWarning: 'over 15 s',
    });
    const generate = () => button(card, 'generate-audio');
    expect(generate().disabled).toBe(true);
    expect(generate().textContent).toContain('Generate audio');
    type(textarea(card, 'Voice description prompt'), 'A warm alto');
    await card.rendered();
    expect(generate().disabled).toBe(false);
    generate().click();
    await settle();
    await settle();
    await settle();
    expect(preflightTasks).toEqual(['voiceDesign']);
    expect(commands()).toEqual([
      { type: 'SetVoiceDesignPrompt', voiceId: 'v1', prompt: 'A warm alto' },
    ]);
    expect(api.calls('POST', `${BASE}/characters/alice/voices/v1/generate-audio`)).toHaveLength(1);
    expect(store.audioVersions()['v1']).toBe(1);
    expect(toasts).toEqual(['warn: over 15 s']);
    expect(card.promptDraft()).toBeNull();
  });

  it('Save prompt posts SetVoiceDesignPrompt; Regenerate with AI stops at a refused preflight', async () => {
    const card = await mount(voice({ designPrompt: 'old' }));
    type(textarea(card, 'Voice description prompt'), 'new');
    await card.rendered();
    button(card, 'save-prompt').click();
    await settle();
    await card.rendered();
    expect(commands()).toEqual([{ type: 'SetVoiceDesignPrompt', voiceId: 'v1', prompt: 'new' }]);

    preflightAnswer = false;
    button(card, 'regenerate-prompt').click();
    await settle();
    expect(preflightTasks).toEqual(['voicePrompt']);
    expect(document.querySelector('r2m-generate-prompt-dialog')).toBeNull();
    expect(card.regeneratingPrompt()).toBe(false);
  });

  it('switching source confirms when something is lost; a refused confirm posts nothing', async () => {
    const card = await mount(voice({ designPrompt: 'warm' }));
    confirmAnswer = false;
    card.querySelector<HTMLInputElement>('[data-source=Uploaded] input')!.click();
    await settle();
    expect(confirms).toEqual(['Switch to reference audio']);
    expect(commands()).toEqual([]);
    await card.rendered();
    expect(card.querySelector<HTMLInputElement>('[data-source=Generated] input')?.checked).toBe(
      true,
    );

    confirmAnswer = true;
    card.querySelector<HTMLInputElement>('[data-source=Uploaded] input')!.click();
    await settle();
    expect(commands()).toEqual([{ type: 'SetVoiceSource', voiceId: 'v1', isGenerated: false }]);
  });

  it('Delete voice sits behind the destructive confirm', async () => {
    const card = await mount(voice());
    confirmAnswer = false;
    button(card, 'delete-voice').click();
    await settle();
    expect(confirms).toEqual(['Delete voice']);
    expect(commands()).toEqual([]);
    confirmAnswer = true;
    button(card, 'delete-voice').click();
    await settle();
    expect(commands()).toEqual([{ type: 'DeleteVoice', voiceId: 'v1' }]);
  });

  it('the transcript saves when dirty, and Send to AI transcribes then reloads the voices', async () => {
    const card = await mount(voice({ source: 'Uploaded', audioFileName: 'voices/alice/v1.wav' }));
    api.on('POST', `${BASE}/voices/v1/transcribe`, { transcript: 'hello there' });
    const save = () => button(card, 'save-transcript');
    expect(save().disabled).toBe(true);
    type(textarea(card, 'Transcript'), 'hand-written');
    await card.rendered();
    save().click();
    await settle();
    await card.rendered();
    expect(commands()).toEqual([
      { type: 'SetVoiceTranscript', voiceId: 'v1', transcript: 'hand-written' },
    ]);

    api.on('GET', VOICES, {
      defaultVoiceId: null,
      voices: [
        voice({
          source: 'Uploaded',
          audioFileName: 'voices/alice/v1.wav',
          transcript: 'hello there',
        }),
      ],
    });
    button(card, 'transcribe').click();
    await settle();
    await settle();
    expect(preflightTasks).toEqual(['transcription']);
    expect(toasts).toEqual(['success: Transcript generated.']);
    expect(store.voices().voices[0]?.transcript).toBe('hello there');
  });

  it('an upload goes through the file drop to the audio endpoint; a refused one is toasted', async () => {
    const card = await mount(voice({ source: 'Uploaded' }));
    const drop = card.querySelector('r2m-file-drop')!;
    expect(drop.label).toBe('Upload audio');
    const file = new File([new Uint8Array(4)], 'take.wav', { type: 'audio/wav' });
    api.on(
      'PUT',
      `${BASE}/voices/v1/audio`,
      voice({ source: 'Uploaded', audioFileName: 'voices/alice/v1.wav' }),
    );
    drop.dispatchEvent(new CustomEvent('files', { detail: [file] }));
    await settle();
    await settle();
    expect(api.calls('PUT', `${BASE}/voices/v1/audio`)).toHaveLength(1);
    expect(store.audioVersions()['v1']).toBe(1);
    expect(toasts).toEqual(['success: Voice audio normalised.']);

    api.on('PUT', `${BASE}/voices/v1/audio`, () => problem(422, '30 s or shorter'));
    drop.dispatchEvent(new CustomEvent('files', { detail: [file] }));
    await settle();
    await settle();
    expect(toasts.at(-1)).toBe('problem: 30 s or shorter');
    drop.dispatchEvent(new CustomEvent('rejected', { detail: [{ file, reason: 'size' }] }));
    expect(toasts.at(-1)).toBe('warn: Voice audio must be 200 MB or smaller.');
  });

  it('an override save from the Advanced tab posts the matching command', async () => {
    const card = await mount(voice());
    const editor = card.querySelector('r2m-voice-override-editor')!;
    expect(editor.getAttribute('data-area')).toBe('voice-design');
    editor.dispatchEvent(
      new CustomEvent('save', { detail: { area: 'voice-design', json: '{"a":1}' } }),
    );
    await settle();
    card.querySelector<HTMLElement>('[role=tab][data-tab=paragraph-tts]')!.click();
    await card.rendered();
    const tts = card.querySelector('r2m-voice-override-editor')!;
    expect(tts.getAttribute('data-area')).toBe('paragraph-tts');
    tts.dispatchEvent(new CustomEvent('save', { detail: { area: 'paragraph-tts', json: null } }));
    await settle();
    expect(commands()).toEqual([
      { type: 'SetVoiceSettingsOverride', voiceId: 'v1', json: '{"a":1}' },
      { type: 'SetVoiceTtsSettingsOverride', voiceId: 'v1', json: null },
    ]);
  });
});
