import type { QueueMessage } from '@app/api';
import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { render } from 'lit-html';
import { VIEWING_READ_ONLY } from '@app/api';
import { Router } from '@app/core/router';
import { override, resetServices } from '@app/core/services';
import { ToastService } from '@app/ui/toast';
import { FakeApi } from '../../../testing/fake-api';
import { FakeLive } from '../../../testing/fake-live';
import type { PronunciationCard } from './pronunciation-card';
import { indexSpans, markedText } from './pronunciation-marks';
import './pronunciation-card';

const TRY = '/api/projects/the-adventures-of-sherlock-holmes/pronunciations/try';

const marks = indexSpans({
  pronunciations: [{ id: 'holmes', writtenForm: 'Holmes', spokenForm: 'HOHMZ' }],
  spans: [
    {
      itemId: 'i1',
      start: 12,
      length: 6,
      pronunciationId: 'holmes',
      sentenceStart: 12,
    },
  ],
});

let api: FakeApi;
let live: FakeLive;
let navigations: { path: string; query?: Record<string, string | null> }[];
let host: HTMLElement;
let card: PronunciationCard;

beforeEach(async () => {
  resetServices();
  api = new FakeApi();
  api.install();
  api.on('POST', TRY, () => new Response(new Uint8Array([82, 73, 70, 70])));
  live = new FakeLive().install();
  navigations = [];
  override(Router, {
    navigate: (path: string, options: { query?: Record<string, string | null> } = {}) =>
      navigations.push({ path, ...options }),
  } as unknown as Router);
  override(ToastService, {
    problem: () => undefined,
  } as unknown as ToastService);

  host = document.createElement('div');
  render(markedText('i1', 'Holmes ran. Holmes fell.', marks.byItem['i1']), host);
  card = document.createElement('r2m-pronunciation-card') as PronunciationCard;
  card.folder = 'the-adventures-of-sherlock-holmes';
  card.marks = marks;
  card.block = null;
  host.append(card);
  document.body.append(host);
  await settle();
});

afterEach(() => host.remove());

async function settle() {
  for (let i = 0; i < 5; i++) await Promise.resolve();
  await new Promise((r) => setTimeout(r, 0));
  await card.rendered();
}

const mark = () => host.querySelector<HTMLElement>('.r2m-pron-mark')!;
const play = () => card.querySelector<HTMLButtonElement>('[data-action=play-sentence]')!;
const reasonOf = (button: HTMLElement) =>
  button.closest('.r2m-blockable')?.getAttribute('data-tooltip') ?? null;

async function focusMark() {
  mark().dispatchEvent(new FocusEvent('focusin', { bubbles: true }));
  await settle();
}

describe('the Pronunciation hover card', () => {
  it('opens on focus of a mark with written → spoken, anchored to the mark', async () => {
    expect(card.open()).toBeNull();

    await focusMark();

    expect(card.open()?.itemId).toBe('i1');
    expect(card.querySelector('[data-testid=pron-card-forms]')!.textContent).toContain('Holmes');
    expect(card.querySelector('[data-testid=pron-card-forms]')!.textContent).toContain('HOHMZ');
    expect(mark().style.getPropertyValue('anchor-name')).toBe('--r2m-pron-card');
  });

  it('plays the sentence in the item’s own voice with the list applied', async () => {
    await focusMark();

    play().click();
    await settle();

    expect(api.calls('POST', TRY).map((c) => c.body)).toEqual([
      {
        itemId: 'i1',
        sentenceStart: 12,
        draft: null,
        asWritten: false,
        voice: 'item',
      },
    ]);
  });

  it('is off with its reason while a GPU job is queued or running', async () => {
    live.queue.set({
      attribution: { queuedCount: 2, processingCount: 0, isBusy: true },
      audio: { queuedCount: 0, processingCount: 0 },
    } as QueueMessage);
    await focusMark();

    expect(play().disabled).toBe(true);
    expect(reasonOf(play())).toBe("Can't play while Attribution is running: it has the GPU");
  });

  it('is off while viewing a step', async () => {
    card.block = { reason: VIEWING_READ_ONLY };
    await focusMark();

    expect(play().disabled).toBe(true);
    expect(reasonOf(play())).toBe(VIEWING_READ_ONLY);
  });

  it('Edit asks for the entry in the panel, from the mark it was opened on', async () => {
    const asked: unknown[] = [];
    card.addEventListener('pronunciation-edit', (e) => asked.push((e as CustomEvent).detail));
    await focusMark();

    card.querySelector<HTMLButtonElement>('[data-action=edit-pronunciation]')!.click();

    expect(asked).toEqual([{ pronunciationId: 'holmes', place: { itemId: 'i1', offset: 12 } }]);
    expect(navigations).toEqual([]);
    expect(card.open()).toBeNull();
  });

  it('Edit is off with the viewing reason while a step is viewed', async () => {
    card.block = { reason: VIEWING_READ_ONLY };
    await focusMark();

    const edit = card.querySelector<HTMLButtonElement>('[data-action=edit-pronunciation]')!;
    expect(edit.disabled).toBe(true);
    expect(reasonOf(edit)).toBe(VIEWING_READ_ONLY);
  });

  it('Escape closes it and gives focus back to the mark', async () => {
    await focusMark();

    card.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await settle();

    expect(card.open()).toBeNull();
    expect(mark().style.getPropertyValue('anchor-name')).toBe('');
  });
});

describe('the mark the card hangs from', () => {
  const second = () => host.querySelectorAll<HTMLElement>('.r2m-pron-mark')[1]!;
  const expanded = (el: HTMLElement) => el.getAttribute('aria-expanded');
  const key = async (target: HTMLElement, k: string) => {
    const e = new KeyboardEvent('keydown', { key: k, bubbles: true, cancelable: true });
    target.dispatchEvent(e);
    await settle();
    return e;
  };

  beforeEach(async () => {
    const two = indexSpans({
      pronunciations: [{ id: 'holmes', writtenForm: 'Holmes', spokenForm: 'HOHMZ' }],
      spans: [
        { itemId: 'i1', start: 0, length: 6, pronunciationId: 'holmes', sentenceStart: 0 },
        { itemId: 'i1', start: 12, length: 6, pronunciationId: 'holmes', sentenceStart: 12 },
      ],
    });
    card.remove();
    render(markedText('i1', 'Holmes ran. Holmes fell.', two.byItem['i1']), host);
    card.marks = two;
    host.append(card);
    await settle();
  });

  it('is expanded while its card is open and collapsed once it closes', async () => {
    expect(expanded(mark())).toBe('false');

    await focusMark();
    expect(expanded(mark())).toBe('true');

    card.hide();
    expect(expanded(mark())).toBe('false');
  });

  it('collapses when the card moves to another mark', async () => {
    await focusMark();

    card.show(second());
    await settle();

    expect(expanded(mark())).toBe('false');
    expect(expanded(second())).toBe('true');
  });

  it('collapses when the panel closes by any other route (its toggle event)', async () => {
    await focusMark();

    const panel = card.querySelector<HTMLElement>('.pron-card__panel')!;
    panel.dispatchEvent(Object.assign(new Event('toggle'), { newState: 'closed' }));

    expect(expanded(mark())).toBe('false');
  });

  it('Escape collapses it; Enter reopens the card and moves focus into it', async () => {
    await focusMark();
    await key(card, 'Escape');
    expect(expanded(mark())).toBe('false');

    await key(mark(), 'Enter');

    expect(card.open()?.anchor).toBe(mark());
    expect(expanded(mark())).toBe('true');
    expect(document.activeElement).toBe(play());
  });

  it('Escape on the mark whose focus opened the card closes it there', async () => {
    await focusMark();

    const e = await key(mark(), 'Escape');

    expect(e.defaultPrevented).toBe(true);
    expect(card.open()).toBeNull();
    expect(expanded(mark())).toBe('false');
  });

  it('Space activates it as Enter does, without scrolling the page', async () => {
    await focusMark();
    await key(card, 'Escape');

    const e = await key(mark(), ' ');

    expect(e.defaultPrevented).toBe(true);
    expect(expanded(mark())).toBe('true');
    expect(document.activeElement).toBe(play());
  });
});
