import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { ApiError, type ProjectSummary, type SeriesCastDto, type SeriesDto } from '@app/api';
import { override, resetServices } from '@app/core/services';
import { SeriesStore } from '@app/series/series-store';
import { ConfirmService, type ConfirmOptions, PromptService } from '@app/ui/dialogs';
import { ToastService } from '@app/ui/toast';
import { settle } from '../../../testing/fake-navigation';
import { ProjectsStore } from './projects-store';
import { SeriesActions } from './series-actions';
import { membershipOf, seriesViews, type SeriesView } from './series-shelf';

const book = (folderName: string, title: string): ProjectSummary => ({
  folderName,
  title,
  author: null,
  coverImage: null,
  audioItemTotal: 0,
  audioItemDone: 0,
  audioPercent: 0,
  series: null,
  fileType: 'Epub',
});

const scarlet = book('scarlet', 'A Study in Scarlet');
const four = book('four', 'The Sign of the Four');
const emma = book('emma', 'Emma');
const projects = [emma, four, scarlet];

const noPoster = { url: null, source: 'None', coverPosition: null } as const;
const holmes: SeriesDto = {
  id: 'h',
  name: 'Sherlock Holmes',
  poster: noPoster,
  books: [
    { folder: 'scarlet', position: 1 },
    { folder: 'four', position: 2 },
  ],
};
const novels: SeriesDto = { id: 'x', name: 'Holmes novels', poster: noPoster, books: [] };

const linked = (characterId: string, name: string) => ({ characterId, name, differs: false });
const narratorRow = {
  seriesCharacterId: 'sc-narrator',
  label: 'Narrator',
  isNarrator: true,
  cells: [
    { folder: 'scarlet', linked: linked('s-narrator', 'Narrator'), suggested: null },
    { folder: 'four', linked: linked('f-narrator', 'Narrator'), suggested: null },
  ],
};
const holmesRow = {
  seriesCharacterId: 'sc-holmes',
  label: 'Sherlock Holmes',
  isNarrator: false,
  cells: [
    { folder: 'scarlet', linked: linked('s-holmes', 'Sherlock Holmes'), suggested: null },
    { folder: 'four', linked: null, suggested: null },
  ],
};
const board: SeriesCastDto = {
  books: [
    { folder: 'scarlet', title: 'A Study in Scarlet', position: 1 },
    { folder: 'four', title: 'The Sign of the Four', position: 2 },
  ],
  rows: [narratorRow, holmesRow],
  suggestedRows: [],
  unlinked: { scarlet: [], four: [] },
  suggestionCount: 0,
};

/** Every call the actions made on the store, by method; `answers` sets what one resolves or throws. */
let calls: [string, ...unknown[]][];
let answers: Record<string, (...args: unknown[]) => unknown>;
let confirms: ConfirmOptions[];
let confirmAnswer: boolean;
let prompts: { title: string; initial?: string }[];
let promptAnswers: (string | null)[];
let toasts: string[];
let views: SeriesView[];

beforeEach(() => {
  resetServices();
  calls = [];
  answers = { castOrNull: () => board };
  override(
    SeriesStore,
    new Proxy({} as SeriesStore, {
      get:
        (_, method: string) =>
        async (...args: unknown[]) => {
          calls.push([method, ...args]);
          return answers[method]?.(...args);
        },
    }),
  );
  views = seriesViews([holmes, novels], projects);
  override(ProjectsStore, {
    series: () => views,
    membership: () => membershipOf(views),
    projects: () => projects,
  } as unknown as ProjectsStore);
  confirms = [];
  confirmAnswer = true;
  override(ConfirmService, {
    confirm: async (o: ConfirmOptions) => {
      confirms.push(o);
      return confirmAnswer;
    },
  } as unknown as ConfirmService);
  prompts = [];
  promptAnswers = [];
  override(PromptService, {
    text: async (o: { title: string; initial?: string }) => {
      prompts.push(o);
      return promptAnswers.shift() ?? null;
    },
  } as unknown as PromptService);
  toasts = [];
  override(ToastService, {
    success: (m: string) => toasts.push(m),
    problem: (p: { detail?: string }) => toasts.push(`problem: ${p.detail}`),
    warn: (m: string) => toasts.push(`warn: ${m}`),
  } as unknown as ToastService);
});
afterEach(() => document.body.replaceChildren());

/** The store calls that write: not the cast read or a confirm's preview. */
const writes = () => calls.filter(([m]) => m !== 'castOrNull' && !m.startsWith('preview'));
const series = (id: string) => views.find((s) => s.id === id)!;

/** Picks a row of the series picker that `addToSeries` opens. */
async function pickSeries(selector: string) {
  await settle();
  const picker = document.querySelector('r2m-series-picker')!;
  await picker.rendered();
  picker.querySelector<HTMLButtonElement>(selector)!.click();
  await new Promise((r) => setTimeout(r, 0));
  await settle();
}

describe('SeriesActions', () => {
  it('a taken name toasts and asks again with the typed name, until one is free', async () => {
    let tries = 0;
    answers.rename = () => {
      if (tries++ === 0)
        throw new ApiError(409, 'Conflict', 'There is already a series called Oz.');
    };
    promptAnswers = ['Oz', 'Ozma'];

    await new SeriesActions().rename(series('h'));

    expect(prompts.map((p) => p.initial)).toEqual(['Sherlock Holmes', 'Oz']);
    expect(toasts).toEqual(['problem: There is already a series called Oz.']);
    expect(writes()).toEqual([
      ['rename', 'h', 'Oz'],
      ['rename', 'h', 'Ozma'],
    ]);
  });

  it('a refusal other than a taken name toasts and stops asking', async () => {
    answers.create = () => {
      throw new ApiError(422, 'Unprocessable', 'registry unreadable');
    };
    promptAnswers = ['Austen', 'never asked'];

    await new SeriesActions().newSeries();

    expect(prompts).toHaveLength(1);
    expect(toasts).toEqual(['problem: registry unreadable']);
  });

  it('Leave names the book’s linked Characters', async () => {
    await new SeriesActions().leave(four);

    expect(confirms[0]!.title).toBe('Remove The Sign of the Four from Sherlock Holmes?');
    expect(confirms[0]!.message).toStartWith(
      'Narrator loses its series link. This book keeps its own copy of its Voices.',
    );
    expect(writes()).toEqual([['leave', 'h', 'four']]);
    expect(toasts).toEqual(['Removed The Sign of the Four from Sherlock Holmes']);
  });

  it('a cast that can’t be read still allows Leave, naming no Characters', async () => {
    answers.castOrNull = () => null;

    await new SeriesActions().leave(four);

    expect(confirms[0]!.message).toStartWith(
      'Any of its Characters linked to the series lose their link.',
    );
    expect(writes()).toEqual([['leave', 'h', 'four']]);
  });

  it('Move to another series names the linked Characters, then joins with move', async () => {
    const moving = new SeriesActions().addToSeries(four);
    await pickSeries('[data-series-id="x"]');
    await moving;

    expect(confirms[0]!.title).toBe('Move The Sign of the Four to Holmes novels?');
    expect(confirms[0]!.message).toContain('Narrator loses its series link.');
    expect(writes()).toEqual([['join', 'x', 'four', true]]);
  });

  it('New series… for a member asks to move first, creates it on its own, then moves the book in', async () => {
    answers.create = () => 'n';
    answers.join = () => 1;
    promptAnswers = ['Holmes novels'];

    const moving = new SeriesActions().addToSeries(four);
    await pickSeries('[data-action="new-series"]');
    await moving;

    expect(confirms[0]!.title).toBe('Move The Sign of the Four to a new series?');
    expect(writes()).toEqual([
      ['create', 'Holmes novels'],
      ['join', 'n', 'four', true],
    ]);
    expect(toasts).toEqual([
      'The Sign of the Four is Book 1 of Holmes novels. Edit the number if it belongs elsewhere.',
    ]);
  });

  it('Delete series names the Series characters whose links go', async () => {
    confirmAnswer = false;

    expect(await new SeriesActions().deleteSeries(series('h'))).toBe(false);

    expect(confirms[0]!.message).toContain('the links of Narrator and Sherlock Holmes are removed');
    expect(writes()).toEqual([]);
  });

  it('every write goes through SeriesStore, so each is refreshed by the same rule', async () => {
    answers.previewLink = () => ({ bookGains: [], seriesGains: [] });
    answers.previewShareAndLink = () => ({ books: [] });
    const actions = new SeriesActions();
    const h = series('h');
    const poster = new File(['x'], 'poster.png', { type: 'image/png' });
    const suggested = {
      label: 'Watson',
      members: [
        { folder: 'scarlet', characterId: 's-watson', name: 'Watson' },
        { folder: 'four', characterId: 'f-watson', name: 'Watson' },
      ],
    };
    promptAnswers = ['Austen', 'Holmes stories'];

    await actions.newSeries();
    await actions.rename(h);
    await actions.uploadPoster(h, poster);
    await actions.removePoster(h);
    await actions.link(h, board, holmesRow, 'four', { characterId: 'f-holmes', name: 'Holmes' });
    await actions.unlink(h, board, narratorRow, 'four', 'Narrator');
    await actions.share(h, board, 'scarlet', { characterId: 's-watson', name: 'Watson' });
    await actions.shareAndLink(h, board, suggested as never);
    await actions.notTheSame(h, holmesRow, 'four', 'f-holmes');
    await actions.notTheSameRow(h, suggested as never);
    await actions.deleteSeries(h);

    expect(writes().map(([method]) => method)).toEqual([
      'create',
      'rename',
      'uploadPoster',
      'clearPoster',
      'link',
      'unlink',
      'share',
      'shareAndLink',
      'notTheSame',
      'notTheSameRow',
      'delete',
    ]);
  });
});
