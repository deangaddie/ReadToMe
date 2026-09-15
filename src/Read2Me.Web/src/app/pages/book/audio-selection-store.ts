import { Injectable } from '@angular/core';
import { SelectionStore } from './selection-store';

/**
 * The reader's item selection in Audio mode (ticket 13, design §6.3): the {@link SelectionStore}
 * shape — ids with the nodes each rolls up into, and the per-node totals it has learnt — over
 * voiced item ids instead of paragraph ids. A separate instance so switching mode never mixes
 * the two selections; the project shell provides and resets both.
 */
@Injectable()
export class AudioSelectionStore extends SelectionStore {}
