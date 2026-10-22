import { use } from '@app/core/services';
import { signal } from '@app/core/signals';
import type { Guid } from './dtos';
import type { paths } from './schema';

/** Why an edit control is off while a History step is viewed (story 40). */
export const VIEWING_READ_ONLY = 'Viewing a past step. Restore it to change anything';

/** Why an item's play button is off while viewing: takes aren't Project state, so it would play today's. */
export const ITEM_AUDIO_NOT_KEPT = "Item audio isn't kept in History";

export interface ViewedStep {
  folder: string;
  stepId: Guid;
}

/**
 * **View a step** for the HTTP layer (context/history.md): the one project, if any, the web app is
 * showing as it stood after a History step. While it is set, every Project state read of that
 * project carries `?at=<stepId>` ({@link ApiClient} adds it to the reads in {@link READS_AT_STEP}),
 * and so does every state file the pages show ({@link stateFileUrl}, {@link voiceOriginalUrl}).
 * Live reads (progress, results, History itself) and writes never do: the host refuses `?at=` on
 * them. The project's {@link HistoryStore} sets and clears it.
 */
export class StepView {
  private readonly _viewing = signal<ViewedStep | null>(null);

  readonly viewing = this._viewing.asReadonly();

  view(folder: string, stepId: Guid): void {
    this._viewing.set({ folder, stepId });
  }

  /** Back to current, for `folder`; a project viewed since stays. */
  clear(folder: string): void {
    if (this._viewing()?.folder === folder) this._viewing.set(null);
  }

  /** The step `folder` is read at, or null for the current project. */
  at(folder: string): Guid | null {
    const viewing = this._viewing();
    return viewing?.folder === folder ? viewing.stepId : null;
  }

  /**
   * The shared read-only flag every edit control reads: why `folder` can't be changed right now
   * ({@link VIEWING_READ_ONLY} while it is viewed at a step), or null on the current project.
   */
  readOnly(folder: string | null): string | null {
    return folder !== null && this.at(folder) !== null ? VIEWING_READ_ONLY : null;
  }
}

/** `url` read at the step `folder` is viewed at, if any: `?at=` added after any query it has. */
export function withViewedStep(url: string, folder: string): string {
  const at = use(StepView).at(folder);
  return at === null ? url : `${url}${url.includes('?') ? '&' : '?'}at=${encodeURIComponent(at)}`;
}

/** The OpenAPI query of a path's GET, or never when it has no GET or no query. */
type GetQuery<P extends keyof paths> = paths[P] extends {
  get: { parameters: { query?: infer Q } };
}
  ? NonNullable<Q>
  : never;

/** Every path whose GET takes `?at=`: the host's `ReadsAt()` project reads. */
type ReadsAtPath = {
  [P in keyof paths]: [GetQuery<P>] extends [never]
    ? never
    : 'at' extends keyof GetQuery<P>
      ? P
      : never;
}[keyof paths];

/**
 * The Project state reads, which accept `?at=`. Checked both ways against the generated schema:
 * a path here that doesn't take `?at=`, or one that does and is missing here, fails typecheck.
 */
export const READS_AT_STEP = [
  '/api/projects/{folder}',
  '/api/projects/{folder}/book',
  '/api/projects/{folder}/characters',
  '/api/projects/{folder}/characters/summary',
  '/api/projects/{folder}/characters/{characterId}/voices',
  '/api/projects/{folder}/characters/{id}/lines',
  '/api/projects/{folder}/characters/{id}/voice-rules',
  '/api/projects/{folder}/characters/{id}/voice-rules/preview',
  '/api/projects/{folder}/nodes/chapter/{id}/voices',
  '/api/projects/{folder}/nodes/{level}/{id}/children',
  '/api/projects/{folder}/nodes/{level}/{id}/item-ids',
  '/api/projects/{folder}/nodes/{level}/{id}/paragraph-ids',
  '/api/projects/{folder}/nodes/{level}/{id}/status',
  '/api/projects/{folder}/paragraphs/{paragraphId}/context',
  '/api/projects/{folder}/status',
  '/api/projects/{folder}/voices/{voiceId}',
  '/api/projects/{folder}/voices/{voiceId}/original.wav',
] as const satisfies readonly ReadsAtPath[];

type Unlisted = Exclude<ReadsAtPath, (typeof READS_AT_STEP)[number]>;
// A ReadsAt read the host added: list it in READS_AT_STEP.
const everyReadListed: [Unlisted] extends [never] ? true : Unlisted = true;
void everyReadListed;

/** Each template as a pattern whose first group is the folder. */
const PATTERNS = READS_AT_STEP.map(
  (template) =>
    new RegExp(
      `^${template
        .replace(/[.]/g, '\\.')
        .replace('{folder}', '([^/]+)')
        .replace(/\{[^}]+\}/g, '[^/]+')}$`,
    ),
);

/** The step to read `path` at: set when it is a Project state read of the project being viewed. */
export function readsAtStep(path: string): Guid | null {
  for (const pattern of PATTERNS) {
    const folder = pattern.exec(path)?.[1];
    if (folder !== undefined) return use(StepView).at(decodeURIComponent(folder));
  }
  return null;
}
