import { LiveService, type LiveConnectionState } from '@app/live/live.service';
import { override } from '@app/core/services';
import { signal } from '@app/core/signals';
import type { ProjectSnapshot } from '@app/live/live-messages';

/**
 * The slice of `LiveService` the shell and the project store read, with group membership
 * recorded. Streams deliver nothing; a spec that needs to push messages uses its own Emitters.
 */
export class FakeLive {
  readonly state = signal<LiveConnectionState>('connected');
  readonly statusLabel = () => (this.state() === 'connected' ? 'Live updates connected' : 'Lost');
  readonly projects = signal<Record<string, ProjectSnapshot>>({});
  readonly joined: string[] = [];
  readonly left: string[] = [];

  on() {
    return () => undefined;
  }
  receipts() {
    return () => undefined;
  }
  resynced() {
    return () => undefined;
  }
  async joinProject(folder: string) {
    this.joined.push(folder);
  }
  async leaveProject(folder: string) {
    this.left.push(folder);
  }

  /** Makes this the app-wide `LiveService`. */
  install(): this {
    override(LiveService, this as unknown as LiveService);
    return this;
  }
}
