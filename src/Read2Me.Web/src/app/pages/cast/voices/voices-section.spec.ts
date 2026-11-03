import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import type { CharacterSummaryDto, VoiceDto } from '@app/api';
import { override, provide, resetServices } from '@app/core/services';
import { LiveService } from '@app/live/live.service';
import { ToastService } from '@app/ui/toast';
import { FakeApi } from '../../../../testing/fake-api';
import { FakeLive } from '../../../../testing/fake-live';
import { settle } from '../../../../testing/fake-navigation';
import { CastStore, SERIES_VOICES_READ_ONLY } from '../cast-store';
import type { AddVoiceDialog } from './add-voice-dialog';
import { ProviderSchemas } from './provider-schemas';
import './voices-section';

const BASE = '/api/projects/dracula';
const COMMANDS = `${BASE}/commands`;

const ALICE: CharacterSummaryDto = {
  id: 'alice',
  name: 'Alice',
  aliases: [],
  lineCount: 0,
  voiceCount: 0,
  readyVoiceCount: 0,
  isNarrator: false,
  narratesBook: false,
  seriesLink: null,
};

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
let store: CastStore;

beforeEach(() => {
  resetServices();
  api = new FakeApi();
  api.install();
  override(LiveService, new FakeLive() as unknown as LiveService);
  override(ToastService, { problem: () => undefined } as unknown as ToastService);
  override(ProviderSchemas, { active: async () => null } as unknown as ProviderSchemas);
  store = new CastStore();
});
afterEach(() => {
  store.close();
  document.body.replaceChildren();
});

async function mount(voices: VoiceDto[], defaultVoiceId: string | null = null) {
  api.on('GET', `${BASE}/characters/summary`, [ALICE]);
  api.on('GET', `${BASE}/characters/alice/lines`, []);
  api.on('GET', `${BASE}/characters/alice/voices`, { defaultVoiceId, voices });
  api.on('GET', `${BASE}/characters/alice/voice-rules`, []);
  api.on('GET', `${BASE}/characters/alice/voice-rules/preview`, []);
  await store.open('dracula');
  await store.select('alice');
  const host = document.createElement('div');
  provide(host, CastStore, store);
  const section = document.createElement('r2m-voices-section');
  section.character = ALICE;
  host.append(section);
  document.body.append(host);
  await section.rendered();
  return section;
}

describe('r2m-voices-section', () => {
  it('shows the empty state without voices, and the count and cards with them', async () => {
    const empty = await mount([]);
    expect(empty.textContent).toContain('No voices yet');
    expect(empty.querySelector('.voices-section__count')?.textContent).toBe('0');

    document.body.replaceChildren();
    const section = await mount([voice('v1', 'Main'), voice('v2', 'Whisper')], 'v2');
    expect(section.querySelector('.voices-section__count')?.textContent).toBe('2');
    expect(section.textContent).not.toContain('No voices yet');
    const cards = Array.from(section.querySelectorAll('r2m-voice-card'));
    expect(cards.map((c) => c.getAttribute('data-voice-id'))).toEqual(['v1', 'v2']);
    expect(cards.map((c) => c.isDefault)).toEqual([false, true]);
    expect(cards[0]?.characterName).toBe('Alice');
  });

  it('Add voice posts CreateVoice with the dialog answer', async () => {
    const section = await mount([]);
    api.on('POST', COMMANDS, { outcome: 'Committed', newEntityId: 'v9' });
    section.querySelector<HTMLButtonElement>('[data-action=add-voice]')!.click();
    await settle();
    const dialog = document.querySelector<AddVoiceDialog>('r2m-add-voice-dialog')!;
    await dialog.rendered();
    dialog.querySelector<HTMLInputElement>('input[value=prompt]')!.click();
    dialog.querySelector<HTMLButtonElement>('[data-action=add]')!.click();
    await settle();
    await settle();
    expect(api.calls('POST', COMMANDS).map((r) => r.body)).toEqual([
      { type: 'CreateVoice', characterId: 'alice', name: 'Alice', isGenerated: true },
    ]);
  });

  describe('a series-linked Character (#221)', () => {
    const WATSON: CharacterSummaryDto = {
      ...ALICE,
      seriesLink: {
        seriesId: 'h',
        seriesCharacterId: 'sc-alice',
        seriesName: 'Sherlock Holmes',
        bookCount: 2,
        differs: false,
      },
    };

    async function mountLinked() {
      api.on('GET', `${BASE}/characters/summary`, [WATSON]);
      api.on('GET', `${BASE}/characters/alice/lines`, []);
      api.on('GET', `${BASE}/characters/alice/voices`, {
        defaultVoiceId: 'v1',
        voices: [
          voice('v1', 'Main'),
          { ...voice('v2', 'Whisper'), audioFileName: 'voices/alice/v2.wav' },
        ],
      });
      api.on('GET', `${BASE}/characters/alice/voice-rules`, []);
      api.on('GET', `${BASE}/characters/alice/voice-rules/preview`, []);
      await store.open('dracula');
      await store.select('alice');
      const host = document.createElement('div');
      provide(host, CastStore, store);
      const section = document.createElement('r2m-voices-section');
      section.character = WATSON;
      host.append(section);
      document.body.append(host);
      await section.rendered();
      for (const card of section.querySelectorAll('r2m-voice-card')) await card.rendered();
      return section;
    }

    it('its shared Voices are read-only, with the way to the series cast board', async () => {
      const section = await mountLinked();

      expect(store.voicesReadOnly()).toBe(SERIES_VOICES_READ_ONLY);
      expect(section.querySelector('[data-action=edit-on-board]')?.getAttribute('href')).toBe(
        'projects?series=h&character=sc-alice',
      );
      expect(section.querySelector<HTMLButtonElement>('[data-action=add-voice]')!.disabled).toBe(
        true,
      );
      const cards = Array.from(section.querySelectorAll('r2m-voice-card'));
      for (const card of cards) {
        expect(card.querySelector<HTMLButtonElement>('[data-action=delete-voice]')!.disabled).toBe(
          true,
        );
        expect(card.querySelector('r2m-inline-edit')?.readOnly).toBe(SERIES_VOICES_READ_ONLY);
      }
      // The full audio editor isn't offered here either.
      expect(section.querySelector('[data-action=edit-audio]')).toBeNull();
    });

    it('its default stays this book’s own', async () => {
      const section = await mountLinked();
      api.on('POST', COMMANDS, { outcome: 'Committed' });
      const star = section.querySelector<HTMLButtonElement>(
        'r2m-voice-card[data-voice-id=v2] [data-action=set-default]',
      )!;
      expect(star.disabled).toBe(false);
      star.click();
      await settle();
      expect(api.calls('POST', COMMANDS).map((r) => r.body)).toEqual([
        { type: 'SetVoiceDefault', voiceId: 'v2' },
      ]);
    });

    it('an unlinked Character’s Voices stay editable, with no board link', async () => {
      const section = await mount([voice('v1', 'Main')], 'v1');
      expect(store.voicesReadOnly()).toBeNull();
      expect(section.querySelector('[data-action=edit-on-board]')).toBeNull();
      expect(section.querySelector<HTMLButtonElement>('[data-action=add-voice]')!.disabled).toBe(
        false,
      );
    });

    it('the series editor’s own store is not read-only', async () => {
      store.seriesEditor = true;
      const section = await mountLinked();
      expect(store.voicesReadOnly()).toBeNull();
      expect(section.querySelector('[data-action=edit-on-board]')).toBeNull();
      expect(section.querySelector<HTMLButtonElement>('[data-action=add-voice]')!.disabled).toBe(
        false,
      );
    });
  });
});
