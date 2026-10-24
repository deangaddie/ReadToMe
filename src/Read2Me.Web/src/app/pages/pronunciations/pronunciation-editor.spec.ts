import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { type PronunciationDto, type PronunciationImpactDto, VIEWING_READ_ONLY } from '@app/api';
import { Router } from '@app/core/router';
import { override, resetServices } from '@app/core/services';
import { type ConfirmOptions, ConfirmService } from '@app/ui/dialogs';
import type { ToastAction } from '@app/ui/toast';
import { ToastService } from '@app/ui/toast';
import { FakeApi } from '../../../testing/fake-api';
import { FakeTimers } from '../../../testing/fake-timers';
import { AUDIO_RUNNING } from './pronunciation-logic';
import type { PronunciationEditor } from './pronunciation-editor';
import './pronunciation-editor';

const BASE = '/api/projects/magician/pronunciations';
const IMPACT = `${BASE}/impact`;

const KULGAN: PronunciationDto = {
  id: 'k1',
  writtenForm: 'Kulgan',
  spokenForm: 'KULL-gun',
  matchCount: 14,
  itemCount: 9,
  withAudioCount: 3,
};

function impact(overrides: Partial<PronunciationImpactDto> = {}): PronunciationImpactDto {
  return {
    matchCount: 14,
    itemCount: 9,
    withAudioCount: 3,
    audioDiscardCount: 0,
    overlaps: [],
    duplicateOf: null,
    matches: [],
    ...overrides,
  };
}

let api: FakeApi;
let timers: FakeTimers;
let toasts: { message: string; actions?: ToastAction[] }[];
let confirms: ConfirmOptions[];
let confirmAnswer: boolean;
let navigated: { path: string; query?: Record<string, string | null> }[];
let editor: PronunciationEditor;
let closes: number;

beforeEach(() => {
  resetServices();
  api = new FakeApi();
  api.install();
  timers = new FakeTimers().install();
  toasts = [];
  override(ToastService, {
    info: (message: string, actions?: ToastAction[]) =>
      toasts.push(actions ? { message, actions } : { message }),
    success: (message: string) => toasts.push({ message }),
    problem: (p: { detail?: string }) => toasts.push({ message: `problem: ${p.detail}` }),
  } as unknown as ToastService);
  confirms = [];
  confirmAnswer = true;
  override(ConfirmService, {
    confirm: async (o: ConfirmOptions) => {
      confirms.push(o);
      return confirmAnswer;
    },
  } as unknown as ConfirmService);
  navigated = [];
  override(Router, {
    navigate: (path: string, options?: { query?: Record<string, string | null> }) =>
      navigated.push(options?.query ? { path, query: options.query } : { path }),
  } as unknown as Router);
  closes = 0;
});

afterEach(() => {
  editor?.remove();
  timers.restore();
});

async function mount(
  entry: PronunciationDto | null,
  setup: (e: PronunciationEditor) => void = () => undefined,
): Promise<PronunciationEditor> {
  editor = document.createElement('r2m-pronunciation-editor') as PronunciationEditor;
  editor.folder = 'magician';
  editor.entry = entry;
  setup(editor);
  editor.addEventListener('editor-close', () => closes++);
  document.body.append(editor);
  await editor.rendered();
  return editor;
}

/** Lets the debounce fire and the impact answer land. */
async function settleImpact(): Promise<void> {
  await timers.advance(300);
  await editor.rendered();
}

function q<T extends Element = HTMLElement>(selector: string): T | null {
  return editor.querySelector<T & Element>(selector);
}

async function type(field: 'written' | 'spoken', value: string): Promise<void> {
  const input = q<HTMLInputElement>(`[data-field=${field}]`)!;
  input.value = value;
  input.dispatchEvent(new Event('input', { bubbles: true }));
  await editor.rendered();
}

async function click(action: string): Promise<void> {
  q<HTMLButtonElement>(`[data-action=${action}]`)!.click();
  await Promise.resolve();
  await timers.advance(0);
  await editor.rendered();
}

function save(): HTMLButtonElement {
  return q<HTMLButtonElement>('[data-action=save]')!;
}

function reasonOf(action: string): string | null {
  return q(`[data-action=${action}]`)!.closest('.r2m-blockable')!.getAttribute('data-tooltip');
}

describe('a new pronunciation', () => {
  it('keeps Add off until a form is typed, then asks the impact once typing settles', async () => {
    api.on('POST', IMPACT, impact({ audioDiscardCount: 0 }));
    await mount(null);
    expect(save().disabled).toBe(true);
    expect(save().textContent).toContain('Add');

    await type('written', 'Kul');
    await type('written', 'Kulgan');
    await type('spoken', 'KULL-gun');
    expect(save().disabled).toBe(false);
    expect(api.calls('POST', IMPACT)).toHaveLength(0);

    await settleImpact();
    expect(api.calls('POST', IMPACT).map((c) => c.body)).toEqual([
      {
        id: null,
        writtenForm: 'Kulgan',
        spokenForm: 'KULL-gun',
        delete: false,
      },
    ]);
    expect(q('[data-role=count]')!.textContent?.trim()).toBe(
      '14 matches in 9 items · 3 have audio',
    );
  });

  it('says what saving discards under Save, and nothing when it discards none', async () => {
    let discard = 12;
    api.on('POST', IMPACT, () => impact({ audioDiscardCount: discard }));
    await mount(null);
    await type('written', 'Kulgan');
    await settleImpact();
    expect(q('.r2m-advice')!.textContent?.trim()).toBe(
      "Saving discards the audio of 12 items; it can't be brought back.",
    );

    discard = 0;
    await type('spoken', 'KULL-gun');
    await settleImpact();
    expect(q('.r2m-advice')).toBeNull();
  });

  it('warns of a duplicate and keeps Add off with it as the reason', async () => {
    api.on(
      'POST',
      IMPACT,
      impact({
        duplicateOf: { id: 'k1', writtenForm: 'Kulgan', spokenForm: 'x' },
      }),
    );
    await mount(null);
    await type('written', 'kulgan');
    await type('spoken', 'KULL-gun');
    await settleImpact();
    expect(q('[data-role=duplicate]')!.textContent).toContain('“Kulgan” is already in the list.');
    expect(save().disabled).toBe(true);
    expect(reasonOf('save')).toBe('“Kulgan” is already in the list.');
  });

  it('notes the entries it overlaps', async () => {
    api.on(
      'POST',
      IMPACT,
      impact({
        overlaps: [
          {
            id: 'm',
            writtenForm: 'Minas Tirith',
            spokenForm: 'x',
            draftWins: false,
          },
        ],
      }),
    );
    await mount(null);
    await type('written', 'Tirith');
    await settleImpact();
    expect(q('[data-role=overlap]')!.textContent).toContain(
      'Inside “Minas Tirith”, that longer entry wins.',
    );
  });

  it('shows the host’s reason for an invalid written form and keeps Add off', async () => {
    api.on(
      'POST',
      IMPACT,
      () =>
        new Response(
          JSON.stringify({
            status: 400,
            title: 'Bad Request',
            detail: 'Written form must end with a letter or digit.',
          }),
          {
            status: 400,
            headers: { 'Content-Type': 'application/problem+json' },
          },
        ),
    );
    await mount(null);
    await type('written', 'Kulgan-');
    await type('spoken', 'x');
    await settleImpact();
    expect(q('[data-role=invalid]')!.textContent).toContain(
      'Written form must end with a letter or digit.',
    );
    expect(save().disabled).toBe(true);
  });

  it('adds it, toasts what needs generating with Show in Book, and closes', async () => {
    api.on('POST', IMPACT, impact());
    api.on('POST', BASE, {
      ...KULGAN,
      audioDiscardCount: 0,
      needsAudioCount: 12,
    });
    await mount(null);
    await type('written', ' Kulgan ');
    await type('spoken', 'KULL-gun');
    await click('save');

    expect(api.calls('POST', BASE).map((c) => c.body)).toEqual([
      { writtenForm: ' Kulgan ', spokenForm: 'KULL-gun' },
    ]);
    expect(toasts).toHaveLength(1);
    expect(toasts[0]!.message).toBe('Saved. 12 items need generating.');
    expect(toasts[0]!.actions!.map((a) => a.label)).toEqual(['Show in Book']);
    toasts[0]!.actions![0]!.run();
    expect(navigated).toEqual([{ path: 'projects/magician/book', query: { mode: 'audio' } }]);
    expect(closes).toBe(1);
  });

  it('says just Saved when nothing needs generating', async () => {
    api.on('POST', IMPACT, impact());
    api.on('POST', BASE, {
      ...KULGAN,
      audioDiscardCount: 0,
      needsAudioCount: 0,
    });
    await mount(null);
    await type('written', 'Kulgan');
    await type('spoken', 'KULL-gun');
    await click('save');
    expect(toasts).toEqual([{ message: 'Saved.' }]);
  });

  it('keeps the draft open and toasts a refusal', async () => {
    api.on('POST', IMPACT, impact());
    api.on(
      'POST',
      BASE,
      () =>
        new Response(
          JSON.stringify({
            status: 409,
            title: 'Conflict',
            detail: 'Audio generation is running: wait for it or cancel it.',
          }),
          {
            status: 409,
            headers: { 'Content-Type': 'application/problem+json' },
          },
        ),
    );
    await mount(null);
    await type('written', 'Kulgan');
    await type('spoken', 'KULL-gun');
    await click('save');
    expect(toasts).toEqual([
      {
        message: 'problem: Audio generation is running: wait for it or cancel it.',
      },
    ]);
    expect(closes).toBe(0);
    expect(q<HTMLInputElement>('[data-field=written]')!.value).toBe('Kulgan');
  });

  it('has no Delete', async () => {
    await mount(null);
    expect(q('[data-action=delete]')).toBeNull();
  });
});

describe('an existing pronunciation', () => {
  it('opens with its forms and reach, Save off until something changes', async () => {
    api.on('POST', IMPACT, impact());
    await mount(KULGAN);
    expect(q<HTMLInputElement>('[data-field=written]')!.value).toBe('Kulgan');
    expect(q<HTMLInputElement>('[data-field=spoken]')!.value).toBe('KULL-gun');
    expect(q('[data-role=count]')!.textContent?.trim()).toBe(
      '14 matches in 9 items · 3 have audio',
    );
    expect(save().disabled).toBe(true);
    expect(save().textContent).toContain('Save');

    await type('spoken', 'KULL-gn');
    expect(save().disabled).toBe(false);
    await type('spoken', 'KULL-gun');
    expect(save().disabled).toBe(true);
  });

  it('edits it with a PUT', async () => {
    api.on('POST', IMPACT, impact({ audioDiscardCount: 3 }));
    api.on('PUT', `${BASE}/k1`, {
      ...KULGAN,
      spokenForm: 'KULL-gn',
      audioDiscardCount: 3,
      needsAudioCount: 3,
    });
    await mount(KULGAN);
    await type('spoken', 'KULL-gn');
    await settleImpact();
    expect(api.calls('POST', IMPACT).at(-1)!.body).toEqual({
      id: 'k1',
      writtenForm: 'Kulgan',
      spokenForm: 'KULL-gn',
      delete: false,
    });
    expect(q('.r2m-advice')!.textContent?.trim()).toBe(
      "Saving discards the audio of 3 items; it can't be brought back.",
    );
    await click('save');
    expect(api.calls('PUT', `${BASE}/k1`).map((c) => c.body)).toEqual([
      { writtenForm: 'Kulgan', spokenForm: 'KULL-gn' },
    ]);
    expect(toasts[0]!.message).toBe('Saved. 3 items need generating.');
    expect(closes).toBe(1);
  });

  it('Cancel discards the draft without a request', async () => {
    await mount(KULGAN);
    await type('spoken', 'KULL-gn');
    await click('cancel');
    expect(closes).toBe(1);
    expect(api.calls('PUT', `${BASE}/k1`)).toHaveLength(0);
  });

  it('a new entry resets the draft', async () => {
    api.on('POST', IMPACT, impact());
    await mount(KULGAN);
    await type('spoken', 'KULL-gn');
    editor.entry = {
      ...KULGAN,
      id: 'p1',
      writtenForm: 'Pug',
      spokenForm: 'pug',
    };
    await editor.rendered();
    expect(q<HTMLInputElement>('[data-field=written]')!.value).toBe('Pug');
    expect(q<HTMLInputElement>('[data-field=spoken]')!.value).toBe('pug');
    expect(editor.dirty()).toBe(false);
  });

  it('keeps the draft when the same entry is refetched', async () => {
    api.on('POST', IMPACT, impact());
    await mount(KULGAN);
    await type('spoken', 'KULL-gn');
    editor.entry = { ...KULGAN, withAudioCount: 0 };
    await editor.rendered();
    expect(q<HTMLInputElement>('[data-field=spoken]')!.value).toBe('KULL-gn');
    expect(editor.dirty()).toBe(true);
  });

  it('asks the impact again when the same entry is refetched with an unchanged draft', async () => {
    let withAudio = 3;
    api.on('POST', IMPACT, () => impact({ withAudioCount: withAudio }));
    await mount(KULGAN);
    await settleImpact();
    withAudio = 0;
    editor.entry = { ...KULGAN, withAudioCount: 0 };
    await settleImpact();
    expect(q('[data-role=count]')!.textContent?.trim()).toBe(
      '14 matches in 9 items · 0 have audio',
    );
  });

  describe('Delete', () => {
    it('always confirms, naming the audio lost, then deletes and toasts', async () => {
      api.on('POST', IMPACT, (body) =>
        (body as { delete: boolean }).delete ? impact({ audioDiscardCount: 12 }) : impact(),
      );
      api.on('DELETE', `${BASE}/k1`, {
        audioDiscardCount: 12,
        needsAudioCount: 40,
      });
      await mount(KULGAN);
      await click('delete');
      await timers.advance(0);
      await editor.rendered();

      expect(api.calls('POST', IMPACT).some((c) => (c.body as { delete: boolean }).delete)).toBe(
        true,
      );
      expect(confirms).toEqual([
        {
          title: 'Delete “Kulgan”?',
          message: 'Kulgan goes back to how the provider reads it. 12 items lose their audio.',
          confirmLabel: 'Delete',
          destructive: true,
        },
      ]);
      expect(api.calls('DELETE', `${BASE}/k1`)).toHaveLength(1);
      expect(toasts[0]!.message).toBe('Deleted. 40 items need generating.');
      expect(closes).toBe(1);
    });

    it('sends nothing when the confirm is declined', async () => {
      api.on('POST', IMPACT, impact());
      confirmAnswer = false;
      await mount(KULGAN);
      await click('delete');
      await timers.advance(0);
      expect(confirms[0]!.message).toBe('Kulgan goes back to how the provider reads it.');
      expect(api.calls('DELETE', `${BASE}/k1`)).toHaveLength(0);
      expect(closes).toBe(0);
    });
  });
});

describe('disabled with a reason', () => {
  it('turns Save and Delete off while audio generation runs for the project', async () => {
    api.on('POST', IMPACT, impact());
    await mount(KULGAN, (e) => (e.audioInFlight = 2));
    await type('spoken', 'KULL-gn');
    expect(save().disabled).toBe(true);
    expect(reasonOf('save')).toBe(AUDIO_RUNNING);
    expect(q<HTMLButtonElement>('[data-action=delete]')!.disabled).toBe(true);
    expect(reasonOf('delete')).toBe(AUDIO_RUNNING);
  });

  it('turns the inputs, Save and Delete off while a step is viewed, and asks no impact', async () => {
    await mount(KULGAN, (e) => (e.readOnly = VIEWING_READ_ONLY));
    expect(q<HTMLInputElement>('[data-field=written]')!.disabled).toBe(true);
    expect(q<HTMLInputElement>('[data-field=spoken]')!.disabled).toBe(true);
    expect(q('[data-field=written]')!.closest('.r2m-blockable')!.getAttribute('data-tooltip')).toBe(
      VIEWING_READ_ONLY,
    );
    expect(save().disabled).toBe(true);
    expect(reasonOf('save')).toBe(VIEWING_READ_ONLY);
    expect(reasonOf('delete')).toBe(VIEWING_READ_ONLY);
    await settleImpact();
    expect(api.calls('POST', IMPACT)).toHaveLength(0);
    expect(q('[data-role=count]')!.textContent?.trim()).toBe(
      '14 matches in 9 items · 3 have audio',
    );
  });
});

describe('the editor in the Book view panel', () => {
  const match = (itemId: string, sentenceStart: number, matchStart: number) => ({
    itemId,
    chapterId: 'c1',
    chapterTitle: 'ONE',
    speaker: 'Narrator',
    hasAudio: false,
    sentence: 'So Kulgan said.',
    sentenceStart,
    matchStart,
    matchLength: 6,
    spokenSentence: 'So KULL-gun said.',
    spokenMatchStart: 3,
    spokenMatchLength: 8,
  });

  it('opens a new entry pre-filled from a selection, ready to Add', async () => {
    api.on('POST', IMPACT, impact());
    await mount(null, (e) => (e.prefill = { writtenForm: 'Kulgan', spokenForm: 'Kulgan' }));
    expect(q<HTMLInputElement>('[data-field=written]')!.value).toBe('Kulgan');
    expect(q<HTMLInputElement>('[data-field=spoken]')!.value).toBe('Kulgan');
    expect(save().disabled).toBe(false);
    await settleImpact();
    expect(api.calls('POST', IMPACT)[0]!.body).toMatchObject({
      writtenForm: 'Kulgan',
    });
  });

  it('tries out the sentence it was opened from in place of the sample cards', async () => {
    api.on('POST', IMPACT, impact({ matches: [match('i1', 0, 3), match('i2', 20, 3)] }));
    await mount(KULGAN, (e) => (e.origin = { itemId: 'i2', offset: 23 }));
    await settleImpact();
    expect(q('r2m-pronunciation-try')).toBeNull();
    const sentence = q<HTMLElement & { match: { itemId: string } }>('r2m-pronunciation-sentence')!;
    expect(sentence.match.itemId).toBe('i2');
  });

  it('says so when the draft no longer matches in the sentence it was opened from', async () => {
    api.on('POST', IMPACT, impact({ matches: [match('i1', 0, 3)] }));
    await mount(KULGAN, (e) => (e.origin = { itemId: 'i2', offset: 23 }));
    await settleImpact();
    expect(q('r2m-pronunciation-sentence')).toBeNull();
    expect(q('[data-role=no-sentence]')).not.toBeNull();
  });

  it('shows Try it on the Pronunciations page, where it was opened from no place', async () => {
    api.on('POST', IMPACT, impact({ matches: [match('i1', 0, 3)] }));
    await mount(KULGAN);
    await settleImpact();
    expect(q('r2m-pronunciation-try')).not.toBeNull();
    expect(q('r2m-pronunciation-sentence')).toBeNull();
  });
});
