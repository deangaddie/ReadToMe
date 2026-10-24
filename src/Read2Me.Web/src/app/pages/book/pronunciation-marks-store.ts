import { type Guid, PronunciationsApi } from '@app/api';
import { use } from '@app/core/services';
import { signal } from '@app/core/signals';
import { hasFacet } from '@app/live/live-messages';
import { LiveService, type Unsubscribe } from '@app/live/live.service';
import { Debounced } from '@app/shared/debounced';
import { type IndexedMarks, NO_MARKS, indexSpans } from './pronunciation-marks';

/**
 * The Pronunciation marks of the chapters the reader shows (pronunciation ticket 15): the host's
 * `…/matches` for them, read again whenever the reader names its chapters (a window change or a
 * chapter read again, so marks follow text edits and a viewed step, whose reads carry `?at=`) and
 * on every receipt with the `Pronunciations` facet. Reads are debounced into one per burst and the
 * latest wins; a failed read keeps the marks there are, since they are only a hint.
 */
export class PronunciationMarks {
  private readonly api = use(PronunciationsApi);
  private readonly live = use(LiveService);

  private readonly _marks = signal<IndexedMarks>(NO_MARKS);
  readonly marks = this._marks.asReadonly();

  private folder: string | null = null;
  private chapterIds: readonly Guid[] = [];
  private unsubscribe: Unsubscribe | null = null;
  private seq = 0;
  private readonly refetch = new Debounced(() => this.load());

  /** The reader shows these chapters of `folder` (again): read their marks. */
  follow(folder: string, chapterIds: readonly Guid[]): void {
    if (folder !== this.folder) {
      this.close();
      this.folder = folder;
      this.unsubscribe = this.live.receipts(folder, (r) => {
        if (hasFacet(r.effects.facets, 'Pronunciations')) this.refetch.schedule();
      });
    }
    this.chapterIds = chapterIds;
    this.refetch.schedule();
  }

  close(): void {
    this.unsubscribe?.();
    this.unsubscribe = null;
    this.refetch.cancel();
    this.folder = null;
    this.chapterIds = [];
    this.seq++;
    this._marks.set(NO_MARKS);
  }

  private async load(): Promise<void> {
    const folder = this.folder;
    const chapterIds = this.chapterIds;
    const seq = ++this.seq;
    if (folder === null || chapterIds.length === 0) {
      this._marks.set(NO_MARKS);
      return;
    }
    try {
      const report = await this.api.matches(folder, chapterIds);
      if (seq === this.seq) this._marks.set(indexSpans(report));
    } catch {
      // Marks are a hint: the last ones stay until a read succeeds.
    }
  }
}
