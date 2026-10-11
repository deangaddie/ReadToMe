import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import type { CharacterSummaryDto, VoiceDto } from '@app/api';
import { override, provide, resetServices } from '@app/core/services';
import { LiveService } from '@app/live/live.service';
import { ToastService } from '@app/ui/toast';
import { FakeApi } from '../../../../testing/fake-api';
import { FakeLive } from '../../../../testing/fake-live';
import { settle } from '../../../../testing/fake-navigation';
import { CastStore } from '../cast-store';
import type { AddVoiceDialog } from './add-voice-dialog';
import { ProviderSchemas } from './provider-schemas';
import './voices-section';

const BASE = '/api/projects/dune';
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
  await store.open('dune');
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
});
