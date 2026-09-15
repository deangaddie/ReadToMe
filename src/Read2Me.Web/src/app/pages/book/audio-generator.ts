import { Injectable, inject, signal } from '@angular/core';
import { AudioApi, Guid, NodeLevel, toApiError } from '@app/api';
import { Preflight } from '@app/shared/preflight';
import { ToastService } from '@app/ui/toast/toast.service';
import { BookEditor } from './book-editor';
import { BookStore } from './book-store';

/**
 * Every audio-generation request the reader makes (ticket 13, design §6.3): a selection, a whole
 * node, or one item to retry, each behind the audio preflight, plus dismissing an item's review
 * flag. The queue answers by receipt and hub status, so nothing here patches a row; the toast
 * says how many were queued. A 409 (no paragraph TTS service) surfaces as a problem toast.
 */
@Injectable()
export class AudioGenerator {
  private readonly audio = inject(AudioApi);
  private readonly editor = inject(BookEditor);
  private readonly store = inject(BookStore);
  private readonly preflight = inject(Preflight);
  private readonly toast = inject(ToastService);

  private readonly _working = signal(false);
  /** A preflight or enqueue is in flight: the action bar waits for it. */
  readonly working = this._working.asReadonly();

  /** The action bar's Generate audio. True when something was queued. */
  enqueueItems(itemIds: readonly Guid[]): Promise<boolean> {
    if (itemIds.length === 0) return Promise.resolve(false);
    return this.enqueue((folder) => this.audio.enqueueItems(folder, [...itemIds]));
  }

  /** "Generate audio for this node": the node-scoped enqueue the overview also uses. */
  enqueueNode(level: NodeLevel, nodeId: Guid, narratorOnlyMode: boolean): Promise<boolean> {
    return this.enqueue((folder) =>
      this.audio.enqueue(folder, { level, nodeId, needsAudioOnly: true, narratorOnlyMode }),
    );
  }

  /** Retry on a Failed/Unfinished chip: re-queues just that item (the queue state wins over the outcome). */
  retry(itemId: Guid): Promise<boolean> {
    return this.enqueue((folder) => this.audio.enqueueItems(folder, [itemId]));
  }

  /** The review chip's dismiss: the flag stays, faded, and the node's review count drops. */
  dismissReview(itemId: Guid): Promise<boolean> {
    return this.editor.run({ type: 'DismissAudioReview', paragraphItemId: itemId });
  }

  private async enqueue(request: (folder: string) => Promise<{ enqueued: number }>): Promise<boolean> {
    const folder = this.store.folder();
    if (!folder || this._working()) return false;
    this._working.set(true);
    try {
      if (!(await this.preflight.ensureReady('audio'))) return false;
      const { enqueued } = await request(folder);
      this.toast.success(
        enqueued === 0 ? 'Nothing to queue' : `Queued ${enqueued} item${enqueued === 1 ? '' : 's'}`,
      );
      return enqueued > 0;
    } catch (error) {
      this.toast.problem(toApiError(error).toProblem());
      return false;
    } finally {
      this._working.set(false);
    }
  }
}
