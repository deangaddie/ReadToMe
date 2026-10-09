import { LiveService, type LiveConnectionState } from '@app/live/live.service';
import { Emitter } from '@app/core/emitter';
import { override } from '@app/core/services';
import { signal } from '@app/core/signals';
import type {
  AssemblyState,
  ProjectSnapshot,
  QueueMessage,
  ServiceStatusState,
  StreamKind,
  ThroughputSnapshot,
  VoiceBatchState,
  WatchdogState,
} from '@app/live/live-messages';
import { IDLE_ASSEMBLY, IDLE_VOICE_BATCH } from '@app/live/live-state';

/**
 * The slice of `LiveService` the shell, the project store and the activity centre read, with
 * group membership recorded. Streams are real Emitters: a spec pushes a hub message with
 * {@link emit} (fold the matching state signal first, as `LiveService` does).
 */
export class FakeLive {
  readonly state = signal<LiveConnectionState>('connected');
  readonly statusLabel = () => (this.state() === 'connected' ? 'Live updates connected' : 'Lost');
  readonly projects = signal<Record<string, ProjectSnapshot>>({});
  readonly queue = signal<QueueMessage | null>(null);
  readonly assembly = signal<AssemblyState>(IDLE_ASSEMBLY);
  readonly voiceBatch = signal<VoiceBatchState>(IDLE_VOICE_BATCH);
  readonly watchdog = signal<WatchdogState>({});
  readonly serviceStatus = signal<ServiceStatusState>({});
  readonly throughput = signal<ThroughputSnapshot | null>(null);
  readonly joined: string[] = [];
  readonly left: string[] = [];
  readonly joinedStreams: StreamKind[] = [];
  readonly leftStreams: StreamKind[] = [];

  readonly #streams = new Map<string, Emitter<unknown>>();

  /** `never` lets any family's typed listener in, as `LiveService.on<K>` does. */
  on(family: string, listener: (message: never) => void) {
    return this.stream(family).subscribe(listener as (message: unknown) => void);
  }
  /** Delivers one message to every `on(family)` listener. */
  emit(family: string, message: unknown): void {
    this.stream(family).emit(message);
  }
  private stream(family: string): Emitter<unknown> {
    let stream = this.#streams.get(family);
    if (!stream) this.#streams.set(family, (stream = new Emitter<unknown>()));
    return stream;
  }
  receipts() {
    return () => undefined;
  }
  resynced() {
    return () => undefined;
  }
  connectionId(): string | null {
    return 'fake-connection';
  }
  async joinProject(folder: string) {
    this.joined.push(folder);
  }
  async leaveProject(folder: string) {
    this.left.push(folder);
  }
  async joinStream(kind: StreamKind) {
    this.joinedStreams.push(kind);
  }
  async leaveStream(kind: StreamKind) {
    this.leftStreams.push(kind);
  }

  /** Makes this the app-wide `LiveService`. */
  install(): this {
    override(LiveService, this as unknown as LiveService);
    return this;
  }
}
