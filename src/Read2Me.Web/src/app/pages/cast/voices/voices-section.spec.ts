import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import type { CharacterSummaryDto, VoiceDto } from '@app/api';
import { override, provide, resetServices } from '@app/core/services';
import { LiveService } from '@app/live/live.service';
import { ConfirmService, type ConfirmOptions } from '@app/ui/dialogs';
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

    async function mountLinked(character: CharacterSummaryDto = WATSON) {
      api.on('GET', `${BASE}/characters/summary`, [character]);
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
      section.character = character;
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

    describe('that differs from series (#222)', () => {
      const DIFFERING: CharacterSummaryDto = {
        ...WATSON,
        seriesLink: { ...WATSON.seriesLink!, differs: true },
      };
      const DETAIL = '/api/series/h/characters/sc-alice';
      const seriesBook = (folder: string, title: string, position: number, differs = false) => ({
        folder,
        title,
        position,
        characterId: `${folder}-alice`,
        characterName: 'Alice',
        defaultVoiceId: null,
        differs,
      });
      let confirms: ConfirmOptions[];
      let answer: boolean;

      beforeEach(() => {
        confirms = [];
        answer = true;
        override(ConfirmService, {
          confirm: async (o: ConfirmOptions) => {
            confirms.push(o);
            return answer;
          },
        } as unknown as ConfirmService);
        api.on('GET', DETAIL, {
          seriesCopyFolder: 'scarlet',
          voices: [],
          books: [
            seriesBook('scarlet', 'A Study in Scarlet', 1),
            seriesBook('dracula', 'Dracula', 2, true),
            seriesBook('four', 'The Sign of the Four', 3),
          ],
          deleteBlocks: {},
        });
      });

      const callout = (section: Element) =>
        section.querySelector<HTMLElement>('[data-testid=differs-callout]');

      it('a callout says so and offers push or take; none when it doesn’t differ', async () => {
        expect(callout(await mountLinked())).toBeNull();
        document.body.replaceChildren();

        const section = await mountLinked(DIFFERING);
        expect(callout(section)?.textContent).toContain(
          'Alice’s Voices in this book differ from the other books in Sherlock Holmes.',
        );
        expect(callout(section)!.querySelector('[data-action=push]')?.textContent).toContain(
          'Use this book’s copy for all',
        );
        expect(callout(section)!.querySelector('[data-action=take]')?.textContent).toContain(
          'Use the series copy',
        );
      });

      it('push confirms which books are replaced, then pushes this book', async () => {
        const push = '/api/series/h/characters/sc-alice/push';
        api.on('POST', push, undefined);
        const section = await mountLinked(DIFFERING);

        answer = false;
        callout(section)!.querySelector<HTMLButtonElement>('[data-action=push]')!.click();
        await settle();
        expect(confirms[0]?.title).toBe('Use Dracula’s copy for all?');
        expect(confirms[0]?.message).toContain(
          'Alice’s Voices in A Study in Scarlet and The Sign of the Four are replaced by the copy in Dracula.',
        );
        expect(api.calls('POST', push)).toEqual([]);

        answer = true;
        callout(section)!.querySelector<HTMLButtonElement>('[data-action=push]')!.click();
        await settle();
        await settle();
        expect(api.calls('POST', push).map((r) => r.body)).toEqual([{ folder: 'dracula' }]);
      });

      it('take confirms what is replaced and from where, then takes the series copy', async () => {
        const take = '/api/series/h/characters/sc-alice/take';
        api.on('POST', take, undefined);
        const section = await mountLinked(DIFFERING);

        callout(section)!.querySelector<HTMLButtonElement>('[data-action=take]')!.click();
        await settle();
        await settle();
        expect(confirms[0]?.title).toBe('Use the series copy in Dracula?');
        expect(confirms[0]?.message).toContain(
          'replaced by the series copy, from A Study in Scarlet',
        );
        expect(confirms[0]?.message).toContain('“Take series voices” History step');
        expect(api.calls('POST', take).map((r) => r.body)).toEqual([{ folder: 'dracula' }]);
      });
    });
  });
});
